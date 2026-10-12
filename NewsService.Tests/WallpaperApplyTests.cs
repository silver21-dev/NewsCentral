using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Mirrors LockScreenApplyTests.cs's coverage of the three-state dispatch, but for the wallpaper
/// surface — SyncService.ApplyIntendedWallpaper and the WallpaperEnabled gate in
/// ApplyWallpaperAsync. Both surfaces share the same generalized dispatch core
/// (SyncService.ApplyIntendedSurface), so this file exists to prove the wallpaper entry points
/// wire into it correctly, not to re-derive the dispatch logic itself.
/// </summary>
public sealed class WallpaperApplyTests
{
    private const string WinnerPath = @"C:\cache\cz-its\images\generated\wall-1.jpg";

    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    private static IConfiguration ConfigWith(string key, string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    private static SyncService NewSut(
        FakePersonalization fake, CapturingLogger<SyncService> log, IConfiguration? configuration = null) =>
        // Only lockScreen (a FakePersonalization) + logger + configuration are exercised here;
        // the rest are unused.
        new(repository: null!, cache: null!, lockScreen: fake, imagePublisher: null!, telemetry: null!,
            entra: null!, configuration: configuration ?? EmptyConfiguration, logger: log,
            contentChanged: new NullContentChangedNotifier());

    // ── Write branch (intended non-null) ────────────────────────────────────────

    [Fact]
    public void Winner_CurrentDiffers_AppliesWinnerPath()
    {
        var fake = new FakePersonalization { WallpaperCurrent = @"C:\something\else.jpg" };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedWallpaper(WinnerPath, "presentation pres-1, team cz-its");

        Assert.Equal(new[] { WinnerPath }, fake.WallpaperSetCalls);
        Assert.Empty(fake.WallpaperClearCalls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public void Winner_CurrentMatches_DoesNotApply()
    {
        var fake = new FakePersonalization { WallpaperCurrent = WinnerPath };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedWallpaper(WinnerPath, "presentation pres-1, team cz-its");

        Assert.Empty(fake.WallpaperSetCalls);
        Assert.Empty(fake.WallpaperClearCalls);
    }

    // ── Teardown branch (intended null) ─────────────────────────────────────────

    [Fact]
    public void NoWinner_NoDefault_LiveUnderPublishRoot_IsCleared()
    {
        const string publishRoot = @"C:\Windows\Web\NewsCentral";
        var livePath = Path.Combine(publishRoot, "wallpaper-abc123def4567890.jpg");
        var fake = new FakePersonalization { WallpaperCurrent = livePath };
        var log  = new CapturingLogger<SyncService>();
        var config = ConfigWith("Delivery:PublishedImagePath", publishRoot);

        NewSut(fake, log, config).ApplyIntendedWallpaper(null, string.Empty);

        Assert.Equal(new[] { livePath }, fake.WallpaperClearCalls);
        Assert.Empty(fake.WallpaperSetCalls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("cleared"));
    }

    [Fact]
    public void NoWinner_NoDefault_LiveOutsidePublishRoot_IsNotCleared()
    {
        // THE critical case: a value NewsService did not write must never be touched.
        const string publishRoot = @"C:\Windows\Web\NewsCentral";
        const string foreignPath = @"C:\Windows\Web\Screen\corporate-wallpaper.jpg";
        var fake = new FakePersonalization { WallpaperCurrent = foreignPath };
        var log  = new CapturingLogger<SyncService>();
        var config = ConfigWith("Delivery:PublishedImagePath", publishRoot);

        NewSut(fake, log, config).ApplyIntendedWallpaper(null, string.Empty);

        Assert.Empty(fake.WallpaperClearCalls);
        Assert.Empty(fake.WallpaperSetCalls);
        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("not one NewsService published"));
    }

    [Fact]
    public void NoWinner_NoDefault_LiveAbsent_NoOp()
    {
        var fake = new FakePersonalization { WallpaperCurrent = null };
        var log  = new CapturingLogger<SyncService>();

        var ex = Record.Exception(() => NewSut(fake, log).ApplyIntendedWallpaper(null, string.Empty));

        Assert.Null(ex);
        Assert.Empty(fake.WallpaperSetCalls);
        Assert.Empty(fake.WallpaperClearCalls);
    }

    [Fact]
    public void OwnershipTest_NormalizesTrailingSeparatorCasingAndDotDot_StillClears()
    {
        var fake = new FakePersonalization
        {
            WallpaperCurrent = @"c:\windows\web\newscentral\wallpaper-abc123def4567890.jpg"
        };
        var log = new CapturingLogger<SyncService>();
        var config = ConfigWith("Delivery:PublishedImagePath", @"C:\WINDOWS\Web\Other\..\NewsCentral\");

        NewSut(fake, log, config).ApplyIntendedWallpaper(null, string.Empty);

        Assert.Single(fake.WallpaperClearCalls);
        Assert.Empty(fake.WallpaperSetCalls);
    }

    [Fact]
    public void NoWinner_NoDefault_PublishRootInvalid_LiveNotCleared()
    {
        var fake = new FakePersonalization { WallpaperCurrent = @"C:\whatever\is\set.jpg" };
        var log  = new CapturingLogger<SyncService>();
        var config = ConfigWith("Delivery:PublishedImagePath", "C:\\Invalid\0Path");

        var ex = Record.Exception(() =>
            NewSut(fake, log, config).ApplyIntendedWallpaper(null, string.Empty));

        Assert.Null(ex);
        Assert.Empty(fake.WallpaperClearCalls);
    }

    // ── WallpaperEnabled gate (ApplyWallpaperAsync) ─────────────────────────────

    [Fact]
    public async Task WallpaperDisabled_NothingHappens_NoReadWriteClearOrSweep()
    {
        var fake = new FakePersonalization
        {
            WallpaperCurrent = @"C:\Windows\Web\NewsCentral\wallpaper-abc.jpg"
        };
        var log  = new CapturingLogger<SyncService>();
        var config = ConfigWith("Delivery:WallpaperEnabled", "false");

        var ex = await Record.ExceptionAsync(
            () => NewSut(fake, log, config).ApplyWallpaperAsync(Array.Empty<string>()));

        Assert.Null(ex);
        Assert.Equal(0, fake.WallpaperGetCurrentCallCount);
        Assert.Empty(fake.WallpaperSetCalls);
        Assert.Empty(fake.WallpaperClearCalls);
        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("WallpaperEnabled = false"));
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    /// <summary>
    /// Fakes both IPersonalizationService surfaces. Lock-screen members are never exercised in
    /// this file (they throw if called by mistake) — only the wallpaper members are live.
    /// </summary>
    private sealed class FakePersonalization : IPersonalizationService
    {
        public string? WallpaperCurrent { get; set; }
        public bool WallpaperSetResult { get; set; } = true;
        public List<string> WallpaperSetCalls { get; } = new();
        public List<string?> WallpaperClearCalls { get; } = new();
        public int WallpaperGetCurrentCallCount { get; private set; }

        public bool SetWallpaper(string imagePath)
        {
            WallpaperSetCalls.Add(imagePath);
            if (WallpaperSetResult) WallpaperCurrent = imagePath;
            return WallpaperSetResult;
        }

        public string? GetCurrentWallpaperPath()
        {
            WallpaperGetCurrentCallCount++;
            return WallpaperCurrent;
        }

        public void ClearWallpaper()
        {
            WallpaperClearCalls.Add(WallpaperCurrent);
            WallpaperCurrent = null;
        }

        public bool SetLockScreen(string imagePath) => throw new NotSupportedException();
        public string? GetCurrentLockScreenPath() => throw new NotSupportedException();
        public void ClearLockScreen() => throw new NotSupportedException();
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
