using NewsCentral.Configuration;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// <c>NewsService.exe --emit-task-script [path] [--newsviewer-path exe]</c>: writes the registration
/// script and exits, before any host exists - so it works where the service is not installed and the
/// configuration is only appsettings.json (plus the registry overlay, when present).
/// <c>--newsviewer-path</c> sets the NewsViewer.exe the tasks will launch on the TARGET machine
/// (it overrides ViewerTasks:NewsViewerPath); without it a packaging machine would bake in its own
/// build-output layout.
/// </summary>
internal static class EmitTaskScriptCommand
{
    public const string Switch = "--emit-task-script";
    private const string ViewerPathSwitch = "--newsviewer-path";

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
                    stderr.WriteLine($"Unexpected argument '{args[i]}'. Usage: NewsService.exe {Switch} [<path>] [{ViewerPathSwitch} <NewsViewer.exe>]");
                    return 2;
                }
            }

            var builder = new ConfigurationBuilder()
                .SetBasePath(baseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.Development.json", optional: true);
            if (useRegistry) builder.AddRegistryOverrides(SolutionConstants.Company, SolutionConstants.SolutionName, "NewsService");
            var configuration = builder.Build();

            var config = configuration.Get<ServiceConfiguration>() ?? new ServiceConfiguration();
            if (!string.IsNullOrWhiteSpace(viewerPath)) config.ViewerTasks.NewsViewerPath = viewerPath;

            var target = !string.IsNullOrWhiteSpace(pathArg) ? pathArg : config.ViewerTasks.ScriptPath;
            if (string.IsNullOrWhiteSpace(target))
            {
                stderr.WriteLine("No output path: pass one after " + Switch + " or set ViewerTasks:ScriptPath.");
                return 1;
            }
            target = TaskScriptEmitter.ExpandPath(target);

            var inputs = TaskDefinitionInputsResolver.Resolve(config, configuration, baseDirectory);
            var script = TaskScriptGenerator.Generate(inputs, TaskScriptEmitter.NewHeader(), TaskDefinitionBuilder.LoadTemplateBytes);
            var outcome = TaskScriptEmitter.Emit(target, script);
            stdout.WriteLine($"{(outcome == EmitOutcome.Written ? "Wrote" : "Unchanged")}: {target} (Company '{inputs.Company}', NewsViewer '{inputs.ExePath}')");
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"{Switch} failed: {ex.Message}");
            return 1;
        }
    }
}
