using NewsCentral.Statistics;

namespace NewsCentral.Services;

/// <summary>
/// Thin app wrapper over <see cref="PosterStatisticsEngine"/>: resolves the current environment's local
/// distribution root and DataPath, then runs one pass. Returns NotAvailable (touching nothing) for Azure
/// Blob distribution, disabled distribution, or unreadable environment settings. Allows one run at a time
/// within this process; the engine's .lock covers other processes.
/// </summary>
public sealed class PosterStatisticsService
{
    private readonly EnvironmentSettingsService _settings;
    private readonly EnvironmentContext _environment;
    private readonly PosterStatisticsEngine _engine =
        new(log: m => System.Diagnostics.Debug.WriteLine($"[PosterStatistics] {m}"));

    private int _running;

    public PosterStatisticsService(EnvironmentSettingsService settings, EnvironmentContext environment)
    {
        _settings = settings;
        _environment = environment;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public async Task<StatsRunSummary> RunAsync(IProgress<StatsRunProgress>? progress, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var dataPath = _environment.DataPath;

        var resolution = StatisticsRootResolver.Resolve(await _settings.GetAsync(), dataPath);
        if (resolution.DistRoot is null)
            return Summary(now, StatsRunOutcome.NotAvailable, resolution.NotAvailableReason);

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return Summary(now, StatsRunOutcome.Failed, "already running in this application");

        try
        {
            return await _engine.RunAsync(resolution.DistRoot, dataPath, progress, ct);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public PresentationStats? Load(string teamId, string presentationId) =>
        _engine.Load(_environment.DataPath, teamId, presentationId);

    public StatsRunSummary? LoadLastRun() => _engine.LoadLastRun(_environment.DataPath);

    private static StatsRunSummary Summary(DateTime now, StatsRunOutcome outcome, string? message) =>
        new() { StartedUtc = now, FinishedUtc = now, Outcome = outcome, Message = message };
}
