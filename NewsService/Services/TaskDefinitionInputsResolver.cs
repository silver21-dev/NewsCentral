using NewsCentral.Configuration;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>The resolved task inputs plus where the NewsViewer path came from.</summary>
internal sealed record ResolvedTaskInputs(TaskDefinitionInputs Inputs, string ExePathSource);

/// <summary>
/// The ONE place the NewsViewer executable path (and the event log/source) are resolved. The
/// in-process verifier and the <c>--emit-task-script</c> generator both call <see cref="Resolve"/> -
/// there is no other way to obtain the path, so they cannot disagree. Precedence: an explicit override
/// (<c>--newsviewer-path</c>), else <c>ViewerTasks:NewsViewerPath</c>, else the deployed layout
/// <see cref="CanonicalViewerPath"/>. There is deliberately no relative-to-the-service default: on a
/// packaged install the service lives in a sibling folder, so such a default never named the viewer.
/// </summary>
internal static class TaskDefinitionInputsResolver
{
    private const string ViewerExeName = "NewsViewer.exe";
    public const string ProgramFilesToken = "%ProgramFiles%";

    public const string OverrideSource = "--newsviewer-path";
    public const string ConfigSource = "ViewerTasks:NewsViewerPath";
    public const string DefaultSource = @"default %ProgramFiles%\{Company}\NewsCentral\NewsViewer\NewsViewer.exe";

    /// <param name="viewerPathOverride">Explicit path that wins over configuration (emit mode); null = none.</param>
    public static ResolvedTaskInputs Resolve(
        ServiceConfiguration config, IConfiguration configuration, string? viewerPathOverride = null)
    {
        string path, source;
        if (!string.IsNullOrWhiteSpace(viewerPathOverride)) { path = viewerPathOverride; source = OverrideSource; }
        else if (!string.IsNullOrWhiteSpace(config.ViewerTasks.NewsViewerPath)) { path = config.ViewerTasks.NewsViewerPath; source = ConfigSource; }
        else { path = CanonicalViewerPath(); source = DefaultSource; }

        return new ResolvedTaskInputs(
            new TaskDefinitionInputs(SolutionConstants.Company, path,
                Setting(configuration, "Logging:EventLog:LogName", "Application"),
                Setting(configuration, "Logging:EventLog:SourceName", "NewsService")),
            source);
    }

    /// <summary>
    /// The deployed layout with the <c>%ProgramFiles%</c> token left unexpanded, so the machine that
    /// registers the task expands it, not the machine that generated the script.
    /// </summary>
    public static string CanonicalViewerPath() =>
        ProgramFilesToken + @"\" + SolutionConstants.Company + @"\NewsCentral\NewsViewer\" + ViewerExeName;

    /// <summary>Expands environment variables (the stored task command may carry %ProgramFiles%).</summary>
    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);

    private static string Setting(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
