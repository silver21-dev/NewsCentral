using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using NewsService.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers SyncService.RunCycleAsync once Worker no longer gates the cycle on the static team
/// list: a dynamic-only machine (zero static teams, resolved-teams.json supplying the rest) must
/// still sync and apply content, and a genuinely empty effective set (no static, no dynamic) must
/// still run display-surface teardown, status.json, and telemetry retention — none of which
/// happened at all before this fix, since Worker previously never called RunCycleAsync in either
/// case. Also covers the change-gated "no teams" warning.
///
/// entra is passed null in every fixture: RefreshEntraTeamsAsync's own catch-all logs and
/// continues on any failure from it, which is unrelated to what this file covers and keeps a
/// pre-seeded resolved-teams.json from being pruned by the real Entra engine (which would prune
/// it immediately, without grace, since no attribute scheme or group instance is configured — see
/// EntraResolvedTeamsMerger.Merge; Entra resolution logic itself is out of scope for this change).
/// </summary>
public sealed class ZeroEffectiveTeamsCycleTests : IDisposable
{
    private const string Team = "cz-its";

    private readonly string _cacheRoot =
        Path.Combine(Path.GetTempPath(), "nsvc-zeroteams-cache-" + Guid.NewGuid().ToString("N"));
    private readonly string _publishRoot =
        Path.Combine(Path.GetTempPath(), "nsvc-zeroteams-publish-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_publishRoot, recursive: true); } catch { /* best effort */ }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private (SyncService Sut, FakePersonalization Fake, CapturingLogger<SyncService> Log, CacheManager Cache, FakeRepository Repo)
        NewSut()
    {
        var cache = new CacheManager(_cacheRoot, JsonDefaults.Options);
        var svcConfig = new ServiceConfiguration
        {
            Repository = new RepositorySection { SharePath = string.Empty },   // upload skipped; sweep still runs
            Delivery   = new DeliverySection { PublishedImagePath = _publishRoot },
            Telemetry  = new TelemetrySection { UploadEnabled = true }
        };
        var publisher = new ImagePublisher(svcConfig, new CapturingLogger<ImagePublisher>());
        var fake      = new FakePersonalization();
        var log       = new CapturingLogger<SyncService>();
        var repo      = new FakeRepository();
        var telemetry = new TelemetryUploader(
            cache, svcConfig, new HmacService(new HmacOptions()), new CapturingLogger<TelemetryUploader>());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:PublishedImagePath"] = _publishRoot
            })
            .Build();

        var sut = new SyncService(
            repository: repo, cache: cache, lockScreen: fake, imagePublisher: publisher,
            telemetry: telemetry, entra: null!, configuration: configuration, logger: log, contentChanged: new NullContentChangedNotifier());

        return (sut, fake, log, cache, repo);
    }

    private void WriteResolved(params string[] folders)
    {
        var file = new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams = folders.Select(f => new ResolvedTeamEntry
            {
                TeamFolderName = f, LastConfirmedUtc = DateTime.UtcNow
            }).ToList()
        };
        Directory.CreateDirectory(_cacheRoot);
        File.WriteAllText(Path.Combine(_cacheRoot, "resolved-teams.json"),
            JsonSerializer.Serialize(file, JsonDefaults.Options));
    }

    private static PublishedAssignmentIndex NewAssignment(
        string presentationId, string imagePath, string imageHash, bool isLogonScreen) => new()
    {
        AssignmentId             = Guid.NewGuid().ToString(),
        PresentationId           = presentationId,
        PresentationLastModified = DateTime.Now,
        ScheduleStart            = DateTime.Now.AddDays(-1),
        ScheduleEnd              = DateTime.Now.AddDays(1),
        DaysOfWeek               = "1,2,3,4,5,6,7",
        SourceTeamFolderName     = Team,
        DisplayTypes             = new DisplayTypeInfo { IsLogonScreen = isLogonScreen },
        Content                  = new ContentInfo { ImagePath = imagePath, ImageHash = imageHash }
    };

    private string CreateOldUploadFile(string name)
    {
        var uploads = Path.Combine(_cacheRoot, "uploads");
        Directory.CreateDirectory(uploads);
        var path = Path.Combine(uploads, name);
        File.WriteAllText(path, "{}");   // parses as SessionTelemetry; HMAC disabled -> pass-through
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-(TelemetryDefaults.RetentionDays + 5)));
        return path;
    }

    // ── Facts ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DynamicOnlyMachine_ZeroStaticTeams_SyncsAndAppliesDynamicTeamContent()
    {
        var (sut, fake, _, cache, repo) = NewSut();
        WriteResolved(Team);   // the only source of this team is resolved-teams.json

        var imageBytes = "lock-image-bytes"u8.ToArray();
        var imageHash  = "sha256:" + Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
        var imageRelPath = $"{Team}/images/generated/lock.jpg";

        var index = new TeamIndexFile
        {
            TeamFolderName      = Team,
            IndexHash           = "h1",
            PublishedAssignments = [NewAssignment("pres-1", imageRelPath, imageHash, isLogonScreen: true)]
        };
        repo.Texts[$"{Team}/index.json"] = JsonSerializer.Serialize(index, JsonDefaults.Options);
        repo.Bytes[imageRelPath] = imageBytes;

        await sut.RunCycleAsync(Array.Empty<string>(), CancellationToken.None);   // zero STATIC teams

        // The dynamic team's content was actually synced into the cache.
        var cachedIndex = await cache.ReadJsonAsync<TeamIndexFile>($"{Team}/index.json");
        Assert.Equal("h1", cachedIndex?.IndexHash);
        Assert.True(cache.FileExists(imageRelPath));

        // ...and applied: the lock screen was set from the published (protected-folder) copy.
        Assert.Single(fake.SetCalls);
        Assert.Contains(_publishRoot, fake.SetCalls[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ZeroEffectiveTeams_TeardownRuns_CspValueInsidePublishRoot_IsCleared()
    {
        var (sut, fake, _, _, _) = NewSut();
        var owned = Path.Combine(_publishRoot, "lockscreen-abc123def4567890.jpg");
        fake.Current = owned;

        await sut.RunCycleAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(new[] { owned }, fake.ClearCalls);
        Assert.Null(fake.Current);
    }

    [Fact]
    public async Task ZeroEffectiveTeams_StatusJsonStillWritten_NotOnline()
    {
        var (sut, _, _, cache, repo) = NewSut();
        repo.Available = true;   // reachable, but there is nothing to sync

        await sut.RunCycleAsync(Array.Empty<string>(), CancellationToken.None);

        var statusPath = Path.Combine(cache.Root, "status.json");
        Assert.True(File.Exists(statusPath));
        var status = JsonSerializer.Deserialize<NewsService.Models.StatusFile>(
            await File.ReadAllTextAsync(statusPath), JsonDefaults.Options);
        Assert.False(status!.IsOnline);
        Assert.Equal("None", status.SyncSource);
    }

    [Fact]
    public async Task ZeroEffectiveTeams_TelemetryRetentionSweepStillRuns()
    {
        var (sut, _, _, _, _) = NewSut();
        var oldFile = CreateOldUploadFile("session-old.json");

        await sut.RunCycleAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.False(File.Exists(oldFile));
    }

    [Fact]
    public async Task ZeroEffectiveTeams_CspValueOutsidePublishRoot_IsLeftUntouched()
    {
        var (sut, fake, _, _, _) = NewSut();
        var foreign = Path.Combine(Path.GetTempPath(), "nsvc-foreign-" + Guid.NewGuid().ToString("N"), "corporate.jpg");
        fake.Current = foreign;

        await sut.RunCycleAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Empty(fake.ClearCalls);
        Assert.Empty(fake.SetCalls);
        Assert.Equal(foreign, fake.Current);
    }

    [Fact]
    public async Task EmptySetWarning_IsChangeGated_WarnOnEntry_DebugWhilePersisting_WarnAgainAfterReturn()
    {
        var (sut, _, log, _, repo) = NewSut();
        static bool IsEmptyTeamsLine(string m) => m.Contains("No teams to sync");

        // 1) First cycle with nothing configured -> Warning.
        await sut.RunCycleAsync(Array.Empty<string>(), CancellationToken.None);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && IsEmptyTeamsLine(e.Message));

        // 2) Still nothing configured -> demoted to Debug, no repeated Warning.
        log.Entries.Clear();
        await sut.RunCycleAsync(Array.Empty<string>(), CancellationToken.None);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && IsEmptyTeamsLine(e.Message));
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning && IsEmptyTeamsLine(e.Message));

        // 3) A team appears (repo has no content for it, which is irrelevant to this gate) -> no
        //    "no teams" line at all, at any level.
        log.Entries.Clear();
        await sut.RunCycleAsync(new[] { Team }, CancellationToken.None);
        Assert.DoesNotContain(log.Entries, e => IsEmptyTeamsLine(e.Message));

        // 4) The team disappears again -> Warning fires again, not Debug.
        log.Entries.Clear();
        await sut.RunCycleAsync(Array.Empty<string>(), CancellationToken.None);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && IsEmptyTeamsLine(e.Message));
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeRepository : IRepositoryReader
    {
        public bool Available { get; set; } = true;
        public bool IsAvailable => Available;
        public string SyncSource => "Share";
        public Dictionary<string, string> Texts { get; } = new();
        public Dictionary<string, byte[]> Bytes { get; } = new();

        public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult(Texts.TryGetValue(relativePath, out var v) ? v : null);

        public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult(Bytes.TryGetValue(relativePath, out var v) ? v : null);
    }

    private sealed class FakePersonalization : IPersonalizationService
    {
        public string? Current { get; set; }
        public bool SetResult { get; set; } = true;
        public List<string> SetCalls { get; } = new();
        public List<string?> ClearCalls { get; } = new();

        public bool SetLockScreen(string imagePath)
        {
            SetCalls.Add(imagePath);
            if (SetResult) Current = imagePath;
            return SetResult;
        }

        public string? GetCurrentLockScreenPath() => Current;

        public void ClearLockScreen()
        {
            ClearCalls.Add(Current);
            Current = null;
        }

        // Wallpaper members exist only to satisfy IPersonalizationService — this file exercises
        // the lock-screen surface only; Wallpaper*Enabled defaults to true but no assignment ever
        // has IsWallpaper set here, so ApplyWallpaperAsync always resolves winner == null and, with
        // no CSP wallpaper value ever set by these tests, hits the silent "already unset" no-op.
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
