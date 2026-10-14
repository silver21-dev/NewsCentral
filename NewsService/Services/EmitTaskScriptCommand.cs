using NewsCentral.Configuration;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// <c>NewsService.exe --emit-task-script &lt;path&gt; [--newsviewer-path exe]</c>: the BUILD-time step that
/// writes the registration script and exits, before any host exists - so it works where the service is
/// not installed and the only configuration is the shipped appsettings.json (event log and source).
/// The registry overlay and appsettings.Development.json are deliberately NOT read. The script is a build
/// artifact shipped in the package; no running service writes it.
/// <c>--newsviewer-path</c> overrides the NewsViewer.exe the tasks launch on the TARGET machine; the default is
/// %ProgramFiles%\{Company}\NewsCentral\NewsViewer\NewsViewer.exe, never ViewerTasks:NewsViewerPath.
/// </summary>
internal static class EmitTaskScriptCommand
{
    public const string Switch = "--emit-task-script";
    private const string ViewerPathSwitch = "--newsviewer-path";

    public const string Usage =
        $"Usage: NewsService.exe {Switch} <path> [{ViewerPathSwitch} <NewsViewer.exe>]";

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeBuildOutput(string path) =>
        path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\source\repos\", StringComparison.OrdinalIgnoreCase);

    /// <returns>0 on success; non-zero with the reason on <paramref name="stderr"/> on failure.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? baseDirectory = null)
    {
        try
        {
            baseDirectory ??= AppContext.BaseDirectory;

            string? pathArg = null, viewerPath = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(args[i], ViewerPathSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    viewerPath = args[++i];
                else if (!args[i].StartsWith("--", StringComparison.Ordinal) && pathArg is null)
                    pathArg = args[i];
                else
                {
                    stderr.WriteLine($"Unexpected argument '{args[i]}'. {Usage}");
                    return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(pathArg))
            {
                stderr.WriteLine(Usage);
                return 2;
            }

            // Deterministic inputs: appsettings.json only. No registry overlay and no
            // appsettings.Development.json - the build machine's configuration describes the build
            // machine, not the targets the script is shipped to.
            var settingsFile = Path.Combine(baseDirectory, "appsettings.json");
            var configuration = new ConfigurationBuilder()
                .SetBasePath(baseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            // The NewsViewer path never comes from configuration: the override, else the canonical install path.
            var viewerSource = !string.IsNullOrWhiteSpace(viewerPath)
                ? "--newsviewer-path"
                : @"default %ProgramFiles%\{Company}\NewsCentral\NewsViewer\NewsViewer.exe";
            var exePath = !string.IsNullOrWhiteSpace(viewerPath) ? viewerPath : TaskDefinitionInputsResolver.CanonicalViewerPath();
            var config = new ServiceConfiguration { ViewerTasks = { NewsViewerPath = exePath } };
            if (LooksLikeBuildOutput(exePath))
                stderr.WriteLine($"Warning: NewsViewer path '{exePath}' looks like a build output; it is baked into a script shipped to target machines.");

            var target = Environment.ExpandEnvironmentVariables(pathArg.Trim());
            var inputs = TaskDefinitionInputsResolver.Resolve(config, configuration, baseDirectory);
            var script = TaskScriptGenerator.Generate(inputs, TaskScriptEmitter.NewHeader(), TaskDefinitionBuilder.LoadTemplateBytes);
            TaskScriptEmitter.Write(target, script);
            stdout.WriteLine($"Wrote: {target}");
            stdout.WriteLine($"  Company:          {inputs.Company} (compiled in)");
            stdout.WriteLine($"  NewsViewer:       {inputs.ExePath} (source: {viewerSource})");
            stdout.WriteLine($"  Event log/source: {inputs.EventLogName} / {inputs.EventSourceName} (source: {settingsFile}; registry and appsettings.Development.json are not consulted)");
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"{Switch} failed: {ex.Message}");
            return 1;
        }
    }
}
