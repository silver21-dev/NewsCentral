using NewsCentral.Configuration;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// <c>NewsService.exe --emit-task-script &lt;path&gt; [--newsviewer-path exe]</c>: the BUILD-time step that
/// writes the registration script and exits, before any host exists - so it works where the service is
/// not installed and the configuration is only appsettings.json (plus the registry overlay, when
/// present). The script is a build artifact shipped in the package; no running service writes it.
/// <c>--newsviewer-path</c> sets the NewsViewer.exe the tasks will launch on the TARGET machine (it
/// overrides ViewerTasks:NewsViewerPath, including a registry value on the build machine); without it
/// the build layout would be baked in.
/// </summary>
internal static class EmitTaskScriptCommand
{
    public const string Switch = "--emit-task-script";
    private const string ViewerPathSwitch = "--newsviewer-path";

    public const string Usage =
        $"Usage: NewsService.exe {Switch} <path> [{ViewerPathSwitch} <NewsViewer.exe>]";

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    /// <returns>0 on success; non-zero with the reason on <paramref name="stderr"/> on failure.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? baseDirectory = null, bool useRegistry = true)
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

            var builder = new ConfigurationBuilder()
                .SetBasePath(baseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.Development.json", optional: true);
            if (useRegistry) builder.AddRegistryOverrides(SolutionConstants.Company, SolutionConstants.SolutionName, "NewsService");
            var configuration = builder.Build();

            var config = configuration.Get<ServiceConfiguration>() ?? new ServiceConfiguration();
            if (!string.IsNullOrWhiteSpace(viewerPath)) config.ViewerTasks.NewsViewerPath = viewerPath;

            var target = Environment.ExpandEnvironmentVariables(pathArg.Trim());
            var inputs = TaskDefinitionInputsResolver.Resolve(config, configuration, baseDirectory);
            var script = TaskScriptGenerator.Generate(inputs, TaskScriptEmitter.NewHeader(), TaskDefinitionBuilder.LoadTemplateBytes);
            TaskScriptEmitter.Write(target, script);
            stdout.WriteLine($"Wrote: {target} (Company '{inputs.Company}', NewsViewer '{inputs.ExePath}')");
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"{Switch} failed: {ex.Message}");
            return 1;
        }
    }
}
