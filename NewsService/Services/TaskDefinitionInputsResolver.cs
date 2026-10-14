using NewsCentral.Configuration;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// The one place the task parameters are resolved from configuration. The in-process provisioner
/// and the script generator both call it, so the registered tasks and the generated script cannot
/// disagree about the exe path, event log or source.
/// </summary>
internal static class TaskDefinitionInputsResolver
{
    private const string ViewerExeName = "NewsViewer.exe";

    public static TaskDefinitionInputs Resolve(
        ServiceConfiguration config, IConfiguration configuration, string baseDirectory) =>
        new(SolutionConstants.Company,
            ResolveExePath(config, baseDirectory),
            Setting(configuration, "Logging:EventLog:LogName", "Application"),
            Setting(configuration, "Logging:EventLog:SourceName", "NewsService"));

    public static string ResolveExePath(ServiceConfiguration config, string baseDirectory)
    {
        var configured = config.ViewerTasks.NewsViewerPath;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(baseDirectory, "..", "NewsViewer", ViewerExeName))
            : configured;
    }

    private static string Setting(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
