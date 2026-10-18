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

    /// <summary>
    /// Whether statistics can run for the current environment. Resolving needs the (async, cached)
    /// environment settings, so these two values are refreshed by <see cref="RefreshAvailabilityAsync"/>
    /// (and by every <see cref="RunAsync"/>) rather than computed on read; false until first refreshed.
    /// </summary>
    public bool IsAvailable { get; private set; }

    public string? UnavailableReason { get; private set; } = "Not checked yet.";

    /// <summary>The last run recorded for the current environment (read from _run.json), or null.</summary>
    public StatsRunSummary? LastRun => _engine.LoadLastRun(_environment.DataPath);

    public async Task RefreshAvailabilityAsync()
    {
        var resolution = StatisticsRootResolver.Resolve(await _settings.GetAsync(), _environment.DataPath);
        IsAvailable = resolution.DistRoot is not null;
        UnavailableReason = resolution.NotAvailableReason;
    }

    /// <summary>
    /// One presentation's aggregate, read on a background thread. A missing file is null; a corrupt or
    /// unreadable one is also null (the engine logs it) — this never throws to the UI.
    /// </summary>
    public async Task<PresentationStats?> LoadAsync(string sourceTeam, string presentationId)
    {
        var dataPath = _environment.DataPath;
        try
        {
            return await Task.Run(() => _engine.Load(dataPath, sourceTeam, presentationId));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[PosterStatistics] Could not load statistics for {sourceTeam}/{presentationId}: {ex.Message}");
            return null;
        }
    }

    public async Task<StatsRunSummary> RunAsync(IProgress<StatsRunProgress>? progress, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var dataPath = _environment.DataPath;

        var resolution = StatisticsRootResolver.Resolve(await _settings.GetAsync(), dataPath);
        IsAvailable = resolution.DistRoot is not null;
        UnavailableReason = resolution.NotAvailableReason;
        if (resolution.DistRoot is null)
            return Summary(now, StatsRunOutcome.NotAvailable, resolution.NotAvailableReason);

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return Summary(now, StatsRunOutcome.Failed, "already running");

        try
        {
            return await Task.Run(() => _engine.RunAsync(resolution.DistRoot, dataPath, progress, ct), CancellationToken.None);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static StatsRunSummary Summary(DateTime now, StatsRunOutcome outcome, string? message) =>
        new() { StartedUtc = now, FinishedUtc = now, Outcome = outcome, Message = message };
}
