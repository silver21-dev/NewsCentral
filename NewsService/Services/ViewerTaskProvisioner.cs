using NewsCentral.Configuration;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// One provisioning pass over the NewsViewer launch surface: both scheduled tasks are registered,
/// verified (by the <c>nc-hash</c> stamp in their Description) and repaired; only once both are
/// confirmed in place is the legacy launch surface retired — the HKLM Run value and the legacy
/// tasks. That ordering means a machine is never left with neither the Run entry nor a logon
/// trigger. Never throws; every failure is logged and reported as "not done" so the caller retries.
/// </summary>
internal sealed class ViewerTaskProvisioner(
    ServiceConfiguration config,
    IConfiguration configuration,
    ITaskSchedulerClient scheduler,
    IRunKeyAccessor runKey,
    IProcessIdentity identity,
    ILogger<ViewerTaskProvisioner> logger,
    string? baseDirectory = null,
    Func<ViewerTaskKind, string>? loadTemplate = null,
    Func<string, bool>? fileExists = null,
    Func<ViewerTaskKind, byte[]>? loadTemplateBytes = null)
{
    internal const string RunValueName = "NewsViewer";
    private const string ViewerExeName = "NewsViewer.exe";

    private readonly string _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
    private readonly Func<ViewerTaskKind, string> _loadTemplate = loadTemplate ?? TaskDefinitionBuilder.LoadTemplate;
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<ViewerTaskKind, byte[]> _loadTemplateBytes = loadTemplateBytes ?? TaskDefinitionBuilder.LoadTemplateBytes;

    /// <summary>The effective mode; a non-null error is for the caller to log once.</summary>
    public ViewerTaskModeResult ResolveMode() =>
        ViewerTaskModeResolver.Resolve(config.ViewerTasks.Mode, config.ViewerTasks.Manage);

    /// <summary>Runs one pass. True = nothing further to do (success, or hands-off); false = retry later.</summary>
    public async Task<bool> RunPassAsync(CancellationToken ct)
    {
        try
        {
            if (ResolveMode().Mode != ViewerTaskMode.Manage)
            {
                logger.LogDebug("ViewerTasks management is not in Manage mode — the NewsViewer launch surface is left to packaging");
                return true;
            }

            var exe = ResolveExePath();
            if (!_fileExists(exe))
            {
                logger.LogError(
                    "NewsViewer executable not found at '{ExePath}' — the scheduled tasks are not registered; will retry. " +
                    "Set ViewerTasks:NewsViewerPath if NewsViewer is installed elsewhere", exe);
                return false;
            }

            var inputs = TaskDefinitionInputsResolver.Resolve(config, configuration, _baseDirectory);

            var inPlace = true;
            foreach (var kind in new[] { ViewerTaskKind.Launch, ViewerTaskKind.ContentArrived })
                inPlace &= await EnsureTaskAsync(kind, inputs, ct);

            // Both tasks must be confirmed before anything of the old launch surface is touched.
            if (!inPlace) return false;

            var cleaned = await RetireLegacyTasksAsync(inputs.Company, ct);
            cleaned &= RemoveRunValue();
            return cleaned;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NewsViewer task provisioning failed unexpectedly; will retry");
            return false;
        }
    }

    private string ResolveExePath() => TaskDefinitionInputsResolver.ResolveExePath(config, _baseDirectory);

    /// <summary>
    /// Writes the generated registration script to <c>ViewerTasks:ScriptPath</c> (no-op when empty).
    /// Never throws: emission must not block start or fail a cycle. Returns the path when a file exists
    /// afterwards, null when nothing was emitted.
    /// </summary>
    public string? TryEmitScript()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(config.ViewerTasks.ScriptPath)) return null;
            var path = TaskScriptEmitter.ExpandPath(config.ViewerTasks.ScriptPath);
            var inputs = TaskDefinitionInputsResolver.Resolve(config, configuration, _baseDirectory);
            var script = TaskScriptGenerator.Generate(inputs, TaskScriptEmitter.NewHeader(), _loadTemplateBytes);
            var outcome = TaskScriptEmitter.Emit(path, script);
            logger.LogInformation(
                outcome == EmitOutcome.Written
                    ? "NewsViewer task registration script written to '{Path}'"
                    : "NewsViewer task registration script at '{Path}' is up to date", path);
            return path;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the NewsViewer task registration script to '{Path}'; continuing", config.ViewerTasks.ScriptPath);
            return null;
        }
    }

    /// <summary>
    /// Verify-only pass: describes how the launch surface differs from what NewsService would register,
    /// and changes nothing. Empty = in place. Null = could not verify (already logged).
    /// </summary>
    public async Task<IReadOnlyList<string>?> VerifyAsync(CancellationToken ct)
    {
        try
        {
            var inputs = TaskDefinitionInputsResolver.Resolve(config, configuration, _baseDirectory);
            var drift = new List<string>();
            if (!_fileExists(inputs.ExePath))
                drift.Add($"NewsViewer executable not found at '{inputs.ExePath}' (ViewerTasks:NewsViewerPath)");

            foreach (var kind in new[] { ViewerTaskKind.Launch, ViewerTaskKind.ContentArrived })
            {
                BuiltTaskDefinition expected;
                try { expected = TaskDefinitionBuilder.Build(kind, _loadTemplate(kind), inputs); }
                catch (ArgumentException ex)
                {
                    drift.Add($"cannot build the expected '{TaskDefinitionBuilder.TaskName(kind, inputs.Company)}' task: {ex.Message}");
                    continue;
                }

                var query = await scheduler.QueryAsync(expected.TaskName, ct);
                if (!query.Exists) { drift.Add($"task '{expected.TaskName}' is missing"); continue; }

                // A hash match is proof; tasks registered by the generated script carry no stamp, so
                // fall back to comparing what actually matters: command, working directory, triggers.
                if (string.Equals(TaskDefinitionBuilder.ExtractHash(query.Xml), expected.Hash, StringComparison.OrdinalIgnoreCase))
                    continue;
                drift.AddRange(TaskDriftComparer.Compare(expected.TaskName, expected.Xml, query.Xml));
            }
            return drift;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NewsViewer task verification failed unexpectedly");
            return null;
        }
    }

    private async Task<bool> EnsureTaskAsync(ViewerTaskKind kind, TaskDefinitionInputs inputs, CancellationToken ct)
    {
        BuiltTaskDefinition built;
        try
        {
            built = TaskDefinitionBuilder.Build(kind, _loadTemplate(kind), inputs);
        }
        catch (ArgumentException ex)
        {
            logger.LogError(
                "Cannot build the '{TaskName}' task: {Reason} (Logging:EventLog:LogName / SourceName)",
                TaskDefinitionBuilder.TaskName(kind, inputs.Company), ex.Message);
            return false;
        }

        var query = await scheduler.QueryAsync(built.TaskName, ct);
        if (query.Exists && string.Equals(
                TaskDefinitionBuilder.ExtractHash(query.Xml), built.Hash, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("NewsViewer task '{TaskName}' unchanged", built.TaskName);
            return true;
        }

        var result = await scheduler.CreateAsync(built.TaskName, built.Xml, ct);
        if (!result.Success)
        {
            ReportFailure("register", built.TaskName, result);
            return false;
        }

        logger.LogInformation("NewsViewer task '{TaskName}' {Action}", built.TaskName, query.Exists ? "updated" : "registered");
        return true;
    }

    private async Task<bool> RetireLegacyTasksAsync(string company, CancellationToken ct)
    {
        var ok = true;
        foreach (var name in TaskDefinitionBuilder.LegacyTaskNames(company))
        {
            var query = await scheduler.QueryAsync(name, ct);
            if (!query.Exists)
            {
                logger.LogDebug("Legacy NewsViewer task '{TaskName}' not present", name);
                continue;
            }

            var result = await scheduler.DeleteAsync(name, ct);
            if (result.Success) logger.LogInformation("Removed legacy NewsViewer task '{TaskName}'", name);
            else { ReportFailure("remove", name, result); ok = false; }
        }
        return ok;
    }

    private bool RemoveRunValue()
    {
        try
        {
            var data = runKey.GetValue(RunValueName);
            if (data is null)
            {
                logger.LogDebug("HKLM Run value '{Name}' not present", RunValueName);
                return true;
            }

            // Only our own entry: the data must point at NewsViewer.exe. A same-named value for some
            // other program is not ours to remove.
            if (data.Contains(ViewerExeName, StringComparison.OrdinalIgnoreCase))
            {
                if (runKey.DeleteValue(RunValueName))
                    logger.LogInformation("Removed the legacy HKLM Run value '{Name}' (logon is a trigger of the NewsViewer Launch task now)", RunValueName);
                return true;
            }

            logger.LogDebug("HKLM Run value '{Name}' does not point at {Exe} — left alone", RunValueName, ViewerExeName);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            if (!identity.IsLocalSystem)
                logger.LogWarning("Cannot remove the HKLM Run value '{Name}' (access denied; not running as LocalSystem): {Message}", RunValueName, ex.Message);
            else
                logger.LogError(ex, "Cannot remove the HKLM Run value '{Name}'", RunValueName);
            return false;
        }
    }

    private void ReportFailure(string action, string taskName, SchtasksResult result)
    {
        var output = result.Output.Trim();
        var denied = output.Contains("denied", StringComparison.OrdinalIgnoreCase)
                     || output.Contains("0x80070005", StringComparison.OrdinalIgnoreCase);

        // On a dev box the service runs as an ordinary console process: failing to write machine-wide
        // tasks there is expected, not a fault. (A non-elevated token may not get the English wording.)
        if (!identity.IsLocalSystem && (denied || !identity.IsElevated))
        {
            logger.LogWarning(
                "Cannot {Action} NewsViewer task '{TaskName}': access denied (not running as LocalSystem or elevated); will retry. {Output}",
                action, taskName, output);
            return;
        }

        logger.LogError(
            "schtasks failed to {Action} NewsViewer task '{TaskName}' (exit code {ExitCode}): {Output}",
            action, taskName, result.ExitCode, output);
    }
}
