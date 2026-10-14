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

    public const string ProgramFilesToken = "%ProgramFiles%";

    /// <summary>
    /// The canonical install path with the <c>%ProgramFiles%</c> token left unexpanded, so the machine
    /// that registers the task expands it, not the machine that generated the script.
    /// </summary>
    public static string CanonicalViewerPath() =>
        ProgramFilesToken + @"\" + SolutionConstants.Company + @"\NewsCentral\NewsViewer\" + ViewerExeName;

    /// <summary>Expands environment variables (the stored task command may carry %ProgramFiles%).</summary>
    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);

    public static string ResolveExePath(ServiceConfiguration config, string baseDirectory)
    {
        // An explicitly configured path is used literally. The default sibling path is tokenised when it
        // sits under Program Files, matching what the generated script registers for the canonical layout.
        var configured = config.ViewerTasks.NewsViewerPath;
        return string.IsNullOrWhiteSpace(configured)
            ? TokeniseProgramFiles(Path.GetFullPath(Path.Combine(baseDirectory, "..", "NewsViewer", ViewerExeName)))
            : configured;
    }

    internal static string TokeniseProgramFiles(string fullPath)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0
            && fullPath.StartsWith(programFiles + @"\", StringComparison.OrdinalIgnoreCase))
            return ProgramFilesToken + fullPath[programFiles.Length..];
        return fullPath;
    }

    private static string Setting(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
