using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Security;
using NewsService.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers the field bug this change fixes: Worker.ExecuteAsync used to gate the entire poll cycle
/// on the STATIC team list, skipping SyncService.RunCycleAsync entirely whenever it was empty. That
/// broke a machine configured purely with dynamic (Entra-resolved) teams, since Entra resolution
/// itself runs INSIDE RunCycleAsync and would then never execute. The guard is gone; Worker must
/// now run the cycle unconditionally, every poll interval, regardless of the static team count.
///
/// This exercises the real Worker/BackgroundService lifecycle (StartAsync/StopAsync) rather than
/// calling ExecuteAsync directly, since it is a protected override with no other public seam.
/// status.json is written unconditionally at the end of every RunCycleAsync cycle, so its presence
/// is direct, black-box proof the cycle actually ran — the pre-fix guard would have skipped it
/// entirely for an empty static team list.
/// </summary>
public sealed class WorkerZeroTeamsTests : IDisposable
{
    private readonly string _cacheRoot =
        Path.Combine(Path.GetTempPath(), "nsvc-worker-cache-" + Guid.NewGuid().ToString("N"));
    private readonly string _publishRoot =
        Path.Combine(Path.GetTempPath(), "nsvc-worker-publish-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_publishRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task EmptyStaticTeamList_WorkerStillRunsThePollCycle()
    {
        var cache = new CacheManager(_cacheRoot, JsonDefaults.Options);
        var svcConfig = new ServiceConfiguration
        {
            Service  = new ServiceSection { PollIntervalSeconds = 60, CacheRootPath = _cacheRoot },
            Delivery = new DeliverySection { PublishedImagePath = _publishRoot }
        };
        var repo      = new NoopRepository();
        var publisher = new ImagePublisher(svcConfig, new CapturingLogger<ImagePublisher>());
        var telemetry = new TelemetryUploader(
            cache, svcConfig, new HmacService(new HmacOptions()), new CapturingLogger<TelemetryUploader>());

        // No "teams" section at all — the static list Worker reads via TeamConfigurationReader is
        // empty, which is exactly the condition that used to skip the cycle entirely.
        var configuration = new ConfigurationBuilder().Build();

        var syncService = new SyncService(
            repository: repo, cache: cache, lockScreen: new NoopPersonalization(),
            imagePublisher: publisher, telemetry: telemetry, entra: null!,
            configuration: configuration, logger: new CapturingLogger<SyncService>(), contentChanged: new NullContentChangedNotifier());

        var worker = new Worker(new CapturingLogger<Worker>(), syncService, configuration, svcConfig);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var statusPath = Path.Combine(_cacheRoot, "status.json");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(statusPath) && DateTime.UtcNow < deadline)
                await Task.Delay(25);

            Assert.True(File.Exists(statusPath),
                "status.json was never written — the poll cycle did not run for an empty static team list.");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class NoopRepository : IRepositoryReader
    {
        public bool IsAvailable => false;
        public string SyncSource => "None";
        public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
    }

    private sealed class NoopPersonalization : IPersonalizationService
    {
        public bool SetLockScreen(string imagePath) => true;
        public string? GetCurrentLockScreenPath() => null;
        public void ClearLockScreen() { }
        public bool SetWallpaper(string imagePath) => true;
        public string? GetCurrentWallpaperPath() => null;
        public void ClearWallpaper() { }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
