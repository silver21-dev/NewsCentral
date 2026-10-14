using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Read-only check of the NewsViewer launch surface against the definitions the installer is
/// expected to have registered (the generated script, docs/packaging.md). It never creates,
/// modifies or deletes anything. Never throws.
/// </summary>
internal sealed class ViewerTaskVerifier(
    ServiceConfiguration config,
    IConfiguration configuration,
    ITaskQueryClient scheduler,
    ILogger<ViewerTaskVerifier> logger,
    Func<ViewerTaskKind, string>? loadTemplate = null,
    Func<string, bool>? fileExists = null)
{
    internal const string ObsoleteManageKey = "ViewerTasks:Manage";

    private readonly Func<ViewerTaskKind, string> _loadTemplate = loadTemplate ?? TaskDefinitionBuilder.LoadTemplate;
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

    /// <summary>What this verifier expects: the shared resolver's answer, and where the NewsViewer path came from.</summary>
    internal ResolvedTaskInputs ResolveInputs() => TaskDefinitionInputsResolver.Resolve(config, configuration);

    /// <summary>The effective mode; a non-null error is for the caller to log once.</summary>
    public ViewerTaskModeResult ResolveMode() => ViewerTaskModeResolver.Resolve(config.ViewerTasks.Mode);

    /// <summary>
    /// One Warning when the obsolete <c>ViewerTasks:Manage</c> key is still configured (dev and pilot
    /// machines have it in the registry). The key is ignored; <c>ViewerTasks:Mode</c> replaces it.
    /// </summary>
    public void WarnIfObsoleteManageKey()
    {
        if (configuration[ObsoleteManageKey] is null) return;
        logger.LogWarning(
            "{Key} is obsolete and ignored: NewsService no longer creates or repairs the NewsViewer scheduled tasks. " +
            "Remove the value; ViewerTasks:Mode (VerifyOnly | Off) replaces it", ObsoleteManageKey);
    }

    /// <summary>
    /// Describes how the launch surface differs from the expected definitions (empty drift = in place) and
    /// which check each task matched on.
    /// Null = could not verify (already logged).
    /// </summary>
    public async Task<VerifyReport?> VerifyAsync(CancellationToken ct)
    {
        try
        {
            var resolved = ResolveInputs();
            var inputs = resolved.Inputs;
            var drift = new List<string>();
            var paths = new List<string>();

            // A missing NewsViewer payload is not drift of the launch surface (the tasks can be registered
            // correctly before the payload lands): it is its own notice, outside the drift verdict.
            string? exeNotice = null;
            var expandedExe = TaskDefinitionInputsResolver.Expand(inputs.ExePath);
            if (!_fileExists(expandedExe))
                exeNotice = $"NewsViewer executable not found at '{expandedExe}' (source: {resolved.ExePathSource})";

            foreach (var kind in new[] { ViewerTaskKind.Launch, ViewerTaskKind.ContentArrived })
            {
                BuiltTaskDefinition expected;
                try { expected = TaskDefinitionBuilder.Build(kind, _loadTemplate(kind), inputs); }
                catch (ArgumentException ex)
                {
                    drift.Add($"cannot build the expected '{TaskDefinitionBuilder.TaskName(kind, inputs.Company)}' task: {ex.Message}");
                    continue;
                }

                var label = kind == ViewerTaskKind.Launch ? "Launch" : "Content Arrived";
                var query = await scheduler.QueryAsync(expected.TaskName, ct);
                if (!query.Exists) { drift.Add($"task '{expected.TaskName}' is missing"); paths.Add($"{label}: missing"); continue; }

                // The registration script stamps each task with the hash of its whole definition, so a
                // stamp covers settings the field comparison cannot see (principal, time limit, ...).
                // Stamp matches -> pass. Stamp differs -> drift. No stamp (hand-made or legacy task)
                // -> compare the fields that matter.
                var stamp = TaskDefinitionBuilder.ExtractHash(query.Xml);
                if (stamp is null)
                {
                    var fieldDrift = TaskDriftComparer.Compare(expected.TaskName, expected.Xml, query.Xml);
                    drift.AddRange(fieldDrift);
                    paths.Add($"{label}: {(fieldDrift.Count == 0 ? "matched by field comparison" : "field comparison found drift")} (no nc-hash stamp)");
                    continue;
                }
                if (string.Equals(stamp, expected.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add($"{label}: matched by nc-hash stamp");
                    continue;
                }

                drift.Add($"task '{expected.TaskName}': the registered definition differs from the expected one (nc-hash mismatch)");
                drift.AddRange(TaskDriftComparer.Compare(expected.TaskName, expected.Xml, query.Xml));
                paths.Add($"{label}: nc-hash mismatch");
            }
            return new VerifyReport(drift, paths, exeNotice);
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
}

/// <summary>Drift found, plus per task the path the verdict was reached on (nc-hash stamp or field comparison).</summary>
internal sealed record VerifyReport(IReadOnlyList<string> Drift, IReadOnlyList<string> Paths, string? ExecutableNotice = null);

/// <summary>
/// Turns verification reports into log lines: log on change, stay quiet otherwise. The state compared
/// includes the match path, so a task moving from stamped to unstamped is reported once. An unstamped
/// task is legitimate (hand-made or older package), so it never becomes a warning by itself.
/// </summary>
internal sealed class VerifyReporter(ILogger logger)
{
    private string? _reported;
    private string? _reportedNotice;
    private bool _noticeEverReported;

    public void Report(VerifyReport report)
    {
        // The NewsViewer-not-found notice is gated on its own state and is not part of the drift state.
        if (report.ExecutableNotice != _reportedNotice && (report.ExecutableNotice is not null || _noticeEverReported))
        {
            if (report.ExecutableNotice is not null)
            {
                logger.LogWarning("{Notice}. This is not launch-surface drift; the tasks cannot start NewsViewer until it is installed", report.ExecutableNotice);
                _noticeEverReported = true;
            }
            else
            {
                logger.LogInformation("NewsViewer executable is now present");
            }
            _reportedNotice = report.ExecutableNotice;
        }

        var state = string.Join("\n", report.Drift) + "\n--\n" + string.Join("\n", report.Paths);
        if (state == _reported) return;
        _reported = state;

        var paths = string.Join("; ", report.Paths);
        if (report.Drift.Count == 0)
            logger.LogInformation("NewsViewer scheduled tasks verified: both are in place ({Paths})", paths);
        else
            logger.LogWarning(
                "NewsViewer launch surface has drifted; NewsService does not repair it. " +
                "Re-run the registration script shipped in the package: {Drift} ({Paths})",
                string.Join("; ", report.Drift), paths);
    }
}
