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
    string? baseDirectory = null,
    Func<ViewerTaskKind, string>? loadTemplate = null,
    Func<string, bool>? fileExists = null)
{
    internal const string ObsoleteManageKey = "ViewerTasks:Manage";

    private readonly string _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
    private readonly Func<ViewerTaskKind, string> _loadTemplate = loadTemplate ?? TaskDefinitionBuilder.LoadTemplate;
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

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
    /// Describes how the launch surface differs from the expected definitions. Empty = in place.
    /// Null = could not verify (already logged).
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

                // The registration script stamps each task with the hash of its whole definition, so a
                // stamp covers settings the field comparison cannot see (principal, time limit, ...).
                // Stamp matches -> pass. Stamp differs -> drift. No stamp (hand-made or legacy task)
                // -> compare the fields that matter.
                var stamp = TaskDefinitionBuilder.ExtractHash(query.Xml);
                if (stamp is null)
                {
                    drift.AddRange(TaskDriftComparer.Compare(expected.TaskName, expected.Xml, query.Xml));
                    continue;
                }
                if (string.Equals(stamp, expected.Hash, StringComparison.OrdinalIgnoreCase)) continue;

                drift.Add($"task '{expected.TaskName}': the registered definition differs from the expected one (nc-hash mismatch)");
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
}
