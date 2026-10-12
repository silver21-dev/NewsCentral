using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers SyncService.ApplyIntendedLockScreen and the LockScreenEnabled gate in
/// ApplyLockScreenAsync — the three-state, registry-gated, stateless apply step. The live
/// PersonalizationCSP value (surfaced via ILockScreenService.GetCurrentLockScreenPath) is the
/// single source of truth: write when the intended image differs from it, clear it when there is
/// no intended image AND the live value is one NewsService itself published (inside
/// Delivery:PublishedImagePath), and otherwise leave it alone. A failed write/clear is never
/// recorded as applied. A fake ILockScreenService stands in for the registry.
/// </summary>
public sealed class LockScreenApplyTests
{
    private const string WinnerPath  = @"C:\cache\cz-its\images\generated\pres-1.jpg";
    private const string DefaultPath = @"C:\img\default.jpg";

    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    private static IConfiguration ConfigWith(string key, string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    private static SyncService NewSut(
        FakeLockScreen fake, CapturingLogger<SyncService> log, IConfiguration? configuration = null) =>
        // Only lockScreen + logger + configuration (Delivery:LockScreenEnabled /
        // Delivery:PublishedImagePath) are exercised here; the rest are unused.
        new(repository: null!, cache: null!, lockScreen: fake, imagePublisher: null!, telemetry: null!,
            entra: null!, configuration: configuration ?? EmptyConfiguration, logger: log,
            contentChanged: new NullContentChangedNotifier());

    // ── Write branch (intended non-null) ────────────────────────────────────────

    [Fact]
    public void WinnerActive_CurrentDiffers_AppliesWinnerPath()
    {
        var fake = new FakeLockScreen { Current = @"C:\something\else.jpg" };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(WinnerPath, "presentation pres-1, team cz-its");

        Assert.Equal(new[] { WinnerPath }, fake.SetCalls);
        Assert.Empty(fake.ClearCalls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public void WinnerActive_CurrentMatches_DoesNotApply()
    {
        var fake = new FakeLockScreen { Current = WinnerPath };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(WinnerPath, "presentation pres-1, team cz-its");

        Assert.Empty(fake.SetCalls);
        Assert.Empty(fake.ClearCalls);
    }

    [Fact]
    public void WinnerActive_CurrentMatchesCaseAndSeparators_DoesNotApply()
    {
        // Normalization: same target reached via a non-canonical current value still matches.
        var fake = new FakeLockScreen { Current = @"C:\CACHE\cz-its\images\generated\..\generated\pres-1.JPG" };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(WinnerPath, "presentation pres-1, team cz-its");

        Assert.Empty(fake.SetCalls);
        Assert.Empty(fake.ClearCalls);
    }

    [Fact]
    public void NoWinner_DefaultExists_CurrentDiffers_AppliesDefault()
    {
        var fake = new FakeLockScreen { Current = null };   // CSP value absent
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(DefaultPath, "default");

        Assert.Equal(new[] { DefaultPath }, fake.SetCalls);
        Assert.Empty(fake.ClearCalls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public void NoWinner_DefaultExists_CurrentMatches_DoesNotApply()
    {
        var fake = new FakeLockScreen { Current = DefaultPath };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(DefaultPath, "default");

        Assert.Empty(fake.SetCalls);
        Assert.Empty(fake.ClearCalls);
    }

    [Fact]
    public void SetReturnsFalse_LogsError_NoThrow_NothingRecorded()
    {
        var fake = new FakeLockScreen { Current = @"C:\old.jpg", SetResult = false };
        var log  = new CapturingLogger<SyncService>();

        var ex = Record.Exception(() =>
            NewSut(fake, log).ApplyIntendedLockScreen(WinnerPath, "presentation pres-1, team cz-its"));

        Assert.Null(ex);                                                  // no throw
        Assert.Equal(new[] { WinnerPath }, fake.SetCalls);               // attempted
        Assert.Empty(fake.ClearCalls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);    // error logged
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Information); // not claimed applied
    }

    // ── Teardown branch (intended null) — the M2 addition ───────────────────────

    [Fact]
    public void NoWinner_NoDefault_LiveUnderPublishRoot_IsCleared()
    {
        const string publishRoot = @"C:\Windows\Web\NewsCentral";
        var livePath = Path.Combine(publishRoot, "lockscreen-abc123def4567890.jpg");
        var fake = new FakeLockScreen { Current = livePath };
        var log  = new CapturingLogger<SyncService>();
        var config = ConfigWith("Delivery:PublishedImagePath", publishRoot);

        NewSut(fake, log, config).ApplyIntendedLockScreen(null, string.Empty);

        Assert.Equal(new[] { livePath }, fake.ClearCalls);
        Assert.Empty(fake.SetCalls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("cleared"));
    }

    [Fact]
    public void NoWinner_NoDefault_LiveOutsidePublishRoot_IsNotCleared()
    {
        // THE critical case: a value NewsService did not write (GPO, Intune, a manual admin
        // change) must never be touched, even when there is no active content to replace it with.
        const string publishRoot = @"C:\Windows\Web\NewsCentral";
        const string foreignPath = @"C:\Windows\Web\Wallpaper\corporate-default.jpg";
        var fake = new FakeLockScreen { Current = foreignPath };
        var log  = new CapturingLogger<SyncService>();
        var config = ConfigWith("Delivery:PublishedImagePath", publishRoot);

        NewSut(fake, log, config).ApplyIntendedLockScreen(null, string.Empty);

        Assert.Empty(fake.ClearCalls);
        Assert.Empty(fake.SetCalls);
        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("not one NewsService published"));
    }

    [Fact]
    public void NoWinner_NoDefault_LiveAbsent_NoOp()
    {
        var fake = new FakeLockScreen { Current = null };
        var log  = new CapturingLogger<SyncService>();

        var ex = Record.Exception(() => NewSut(fake, log).ApplyIntendedLockScreen(null, string.Empty));

        Assert.Null(ex);
        Assert.Empty(fake.SetCalls);
        Assert.Empty(fake.ClearCalls);
    }

    [Fact]
    public void NoWinner_NoDefault_PublishRootUnconfigured_LiveNotCleared()
    {
        // No Delivery:PublishedImagePath in configuration at all — the ownership test must fail
        // CLOSED (treat every live value as foreign) rather than guess, so nothing is ever cleared.
        var fake = new FakeLockScreen { Current = @"C:\whatever\is\set.jpg" };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(null, string.Empty);

        Assert.Empty(fake.SetCalls);
        Assert.Empty(fake.ClearCalls);
    }

    [Fact]
    public void NoWinner_NoDefault_PublishRootExplicitlyEmpty_LiveNotCleared()
    {
        var fake = new FakeLockScreen { Current = @"C:\whatever\is\set.jpg" };
        var log  = new CapturingLogger<SyncService>();
        var config = ConfigWith("Delivery:PublishedImagePath", "");

        NewSut(fake, log, config).ApplyIntendedLockScreen(null, string.Empty);

        Assert.Empty(fake.ClearCalls);
    }

    [Fact]
    public void NoWinner_NoDefault_PublishRootInvalid_LiveNotCleared()
    {
        var fake = new FakeLockScreen { Current = @"C:\whatever\is\set.jpg" };
        var log  = new CapturingLogger<SyncService>();
        // An embedded NUL makes Path.GetFullPath throw — the ownership test must catch that and
        // fail closed (never clear), not let the exception escape and abort the cycle.
        var config = ConfigWith("Delivery:PublishedImagePath", "C:\\Invalid\0Path");

        var ex = Record.Exception(() =>
            NewSut(fake, log, config).ApplyIntendedLockScreen(null, string.Empty));

        Assert.Null(ex);
        Assert.Empty(fake.ClearCalls);
    }

    [Fact]
    public void OwnershipTest_NormalizesTrailingSeparatorCasingAndDotDot_StillClears()
    {
        var fake = new FakeLockScreen { Current = @"c:\windows\web\newscentral\lockscreen-abc123def4567890.jpg" };
        var log  = new CapturingLogger<SyncService>();
        // Deliberately messy: mixed case, a redundant '..' segment, and a trailing separator —
        // none of it may defeat the fully-normalized comparison.
        var config = ConfigWith("Delivery:PublishedImagePath", @"C:\WINDOWS\Web\Other\..\NewsCentral\");

        NewSut(fake, log, config).ApplyIntendedLockScreen(null, string.Empty);

        Assert.Single(fake.ClearCalls);
        Assert.Empty(fake.SetCalls);
    }

    // ── LockScreenEnabled gate (ApplyLockScreenAsync) ───────────────────────────

    [Fact]
    public async Task LockScreenDisabled_NothingHappens_NoReadWriteClearOrSweep()
    {
        var fake = new FakeLockScreen { Current = @"C:\Windows\Web\NewsCentral\lockscreen-abc.jpg" };
        var log  = new CapturingLogger<SyncService>();
        var config = ConfigWith("Delivery:LockScreenEnabled", "false");

        var ex = await Record.ExceptionAsync(
            () => NewSut(fake, log, config).ApplyLockScreenAsync(Array.Empty<string>()));

        Assert.Null(ex);
        Assert.Equal(0, fake.GetCurrentCallCount);   // no CSP read at all
        Assert.Empty(fake.SetCalls);
        Assert.Empty(fake.ClearCalls);
        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("LockScreenEnabled = false"));
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeLockScreen : IPersonalizationService
    {
        public string? Current { get; set; }
        public bool SetResult { get; set; } = true;
        public List<string> SetCalls { get; } = new();
        public List<string?> ClearCalls { get; } = new();
        public int GetCurrentCallCount { get; private set; }

        public bool SetLockScreen(string imagePath)
        {
            SetCalls.Add(imagePath);
            if (SetResult) Current = imagePath;
            return SetResult;
        }

        public string? GetCurrentLockScreenPath()
        {
            GetCurrentCallCount++;
            return Current;
        }

        public void ClearLockScreen()
        {
            ClearCalls.Add(Current);
            Current = null;
        }

        // Wallpaper members exist only to satisfy IPersonalizationService — this file covers
        // lock-screen behaviour exclusively; wallpaper behaviour is covered by
        // WallpaperApplyTests.cs's own fake. Never called by any test in this file.
        public bool SetWallpaper(string imagePath) => throw new NotSupportedException();
        public string? GetCurrentWallpaperPath() => throw new NotSupportedException();
        public void ClearWallpaper() => throw new NotSupportedException();
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
