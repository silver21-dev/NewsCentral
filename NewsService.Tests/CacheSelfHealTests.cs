using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models.IndexFile;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// On an unchanged IndexHash the images referenced by the cached index are checked locally and only
/// missing ones are re-fetched — no index rewrite, no ContentChanged notification.
/// </summary>
public sealed class CacheSelfHealTests : IDisposable
{
    private const string Team = "cz-its";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "nsvc-heal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (SyncService Sut, FakeRepository Repo, CapturingLogger Log, CountingNotifier Notifier) NewSut()
    {
        var repo     = new FakeRepository();
        var cache    = new CacheManager(_root, JsonDefaults.Options);
        var log      = new CapturingLogger();
        var notifier = new CountingNotifier();
        var sut = new SyncService(repo, cache, lockScreen: null!, imagePublisher: null!, telemetry: null!,
                                  entra: null!, configuration: new ConfigurationBuilder().Build(),
                                  logger: log, contentChanged: notifier);
        return (sut, repo, log, notifier);
    }

    private static string Hash(byte[] b) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    private static string IndexJson(string team, params string[] images)
    {
        var index = new TeamIndexFile
        {
            TeamFolderName = team,
            TeamName       = team,
            GeneratedAt    = new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc),
            IndexHash      = "h1"
        };
        foreach (var img in images)
            index.PublishedAssignments.Add(new PublishedAssignmentIndex
            {
                Content = new ContentInfo { ImagePath = $"{team}/images/generated/{img}", ImageHash = Hash([1]) }
            });
        return JsonSerializer.Serialize(index, JsonDefaults.Options);
    }

    private static Task<bool> Sync(SyncService sut, string team = Team) =>
        sut.SyncTeamAsync(team, isDynamic: false, CancellationToken.None);

    /// <summary>Initial sync populates the cache; counters and log are then reset.</summary>
    private static async Task Seed(SyncService sut, FakeRepository repo, CapturingLogger log, CountingNotifier n,
        params string[] images)
    {
        repo.Texts[$"{Team}/index.json"] = IndexJson(Team, images);
        foreach (var i in images) repo.Images[i] = [1];
        Assert.True(await Sync(sut));
        repo.Downloads.Clear(); log.Entries.Clear(); n.Calls = 0;
    }

    private string CachePath(string img) => Path.Combine(_root, Team, "images", "generated", img);

    [Fact]
    public async Task AllImagesPresent_NoFetch_NoWarning_NoNotify()
    {
        var (sut, repo, log, n) = NewSut();
        await Seed(sut, repo, log, n, "a.png", "b.png");

        Assert.False(await Sync(sut));

        Assert.Empty(repo.Downloads);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Equal(0, n.Calls);
    }

    [Fact]
    public async Task OneMissing_AvailableInSource_OnlyThatFetched_WarningOnce_IndexUntouched_NoNotify()
    {
        var (sut, repo, log, n) = NewSut();
        await Seed(sut, repo, log, n, "a.png", "b.png");
        var indexPath = Path.Combine(_root, Team, "index.json");
        var indexWritten = File.GetLastWriteTimeUtc(indexPath);
        File.Delete(CachePath("b.png"));

        Assert.False(await Sync(sut));

        Assert.Equal(new[] { "b.png" }, repo.Downloads);
        Assert.True(File.Exists(CachePath("b.png")));
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("restored 1 image(s)", warning.Message);
        Assert.Contains("b.png", warning.Message);
        Assert.Equal(indexWritten, File.GetLastWriteTimeUtc(indexPath));
        Assert.Equal(0, n.Calls);

        log.Entries.Clear();
        await Sync(sut);   // healed: silent
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task MissingEverywhere_WarningThenDebug_ThenHealsWhenSourceReturns()
    {
        var (sut, repo, log, n) = NewSut();
        await Seed(sut, repo, log, n, "a.png");
        File.Delete(CachePath("a.png"));
        repo.Images.Remove("a.png");

        await Sync(sut);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);

        log.Entries.Clear();
        await Sync(sut);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("could not restore"));

        repo.Images["a.png"] = [1];
        log.Entries.Clear();
        await Sync(sut);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("restored 1 image(s)"));
        Assert.True(File.Exists(CachePath("a.png")));
        Assert.Equal(0, n.Calls);
    }

    [Fact]
    public async Task CancellationDuringHeal_Propagates_NotLoggedAsFailedHeal()
    {
        var (sut, repo, log, n) = NewSut();
        await Seed(sut, repo, log, n, "a.png");
        File.Delete(CachePath("a.png"));
        repo.ThrowCancelOnBytes = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sync(sut));

        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("could not restore"));
    }

    [Fact]
    public async Task TwoTeams_OneThrowing_OtherStillHeals()
    {
        var (sut, repo, log, n) = NewSut();
        await Seed(sut, repo, log, n, "a.png");
        File.Delete(CachePath("a.png"));
        repo.Texts["bad/index.json"] = "not json";   // deserialization throws for this team

        await sut.SyncAllTeamsAsync(["bad", Team], new HashSet<string>(), CancellationToken.None);

        Assert.True(File.Exists(CachePath("a.png")));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("bad"));
    }

    private sealed class CountingNotifier : IContentChangedNotifier
    {
        public int Calls { get; set; }
        public void NotifyContentChanged() => Calls++;
    }

    private sealed class FakeRepository : IRepositoryReader
    {
        public Dictionary<string, string> Texts { get; } = new();
        public Dictionary<string, byte[]> Images { get; } = new();
        public List<string> Downloads { get; } = new();
        public bool ThrowCancelOnBytes { get; set; }
        public bool IsAvailable => true;
        public string SyncSource => "Share";

        public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult(Texts.TryGetValue(relativePath, out var t) ? t : null);

        public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default)
        {
            if (ThrowCancelOnBytes) throw new OperationCanceledException();
            var name = Path.GetFileName(relativePath);
            Downloads.Add(name);
            return Task.FromResult(Images.TryGetValue(name, out var b) ? b : null);
        }
    }

    private sealed class CapturingLogger : ILogger<SyncService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
