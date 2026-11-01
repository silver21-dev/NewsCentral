using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models.IndexFile;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// The index is written LAST and only when every referenced image is cached: an incomplete sync
/// leaves the previous index in effect and is retried (downloading only what is missing); the
/// incomplete state is logged once per failing IndexHash, then at Debug; the write is atomic.
/// </summary>
public sealed class SyncCompletenessTests : IDisposable
{
    private const string Team = "cz-its";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "nsvc-complete-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (SyncService Sut, FakeRepository Repo, CacheManager Cache, CapturingLogger Log) NewSut()
    {
        var config = new ConfigurationBuilder().Build();
        var repo   = new FakeRepository();
        var cache  = new CacheManager(_root, JsonDefaults.Options);
        var log    = new CapturingLogger();
        var sut = new SyncService(repo, cache, lockScreen: null!, imagePublisher: null!, telemetry: null!,
                                  entra: null!, configuration: config, logger: log, contentChanged: new NullContentChangedNotifier());
        return (sut, repo, cache, log);
    }

    private static string IndexJson(string hash, params string[] images)
    {
        var index = new TeamIndexFile
        {
            TeamFolderName = Team,
            TeamName       = "CZ ITS",
            GeneratedAt    = new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc),
            IndexHash      = hash
        };
        foreach (var img in images)
            index.PublishedAssignments.Add(new PublishedAssignmentIndex
            {
                Content = new ContentInfo { ImagePath = $"{Team}/images/generated/{img}", ImageHash = Hash(img == "a.png" ? [1] : [2]) }
            });
        return JsonSerializer.Serialize(index, JsonDefaults.Options);
    }

    private static string Hash(byte[] b) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    private Task<bool> Sync(SyncService sut) => sut.SyncTeamAsync(Team, CancellationToken.None);

    private async Task<string?> CachedHash(CacheManager cache) =>
        (await cache.ReadJsonAsync<TeamIndexFile>($"{Team}/index.json"))?.IndexHash;

    [Fact]
    public async Task OneImageFails_IndexNotWritten_PreviousIndexKept_NoChangeReported()
    {
        var (sut, repo, cache, _) = NewSut();
        repo.Text = IndexJson("old", "a.png");
        repo.Images["a.png"] = [1];
        Assert.True(await Sync(sut));
        Assert.Equal("old", await CachedHash(cache));

        repo.Text = IndexJson("new", "a.png", "b.png");   // b.png is not in the repository
        Assert.False(await Sync(sut));

        Assert.Equal("old", await CachedHash(cache));
    }

    [Fact]
    public async Task SameHashNextCycle_ImageNowAvailable_OnlyMissingDownloaded_IndexWritten()
    {
        var (sut, repo, cache, _) = NewSut();
        repo.Text = IndexJson("new", "a.png", "b.png");
        repo.Images["a.png"] = [1];
        Assert.False(await Sync(sut));
        Assert.Equal(new[] { "a.png", "b.png" }, repo.Downloads);   // a cached, b failed

        repo.Downloads.Clear();
        repo.Images["b.png"] = [2];
        Assert.True(await Sync(sut));

        Assert.Equal(new[] { "b.png" }, repo.Downloads);   // a.png not downloaded again
        Assert.Equal("new", await CachedHash(cache));
        Assert.True(cache.FileExists($"{Team}/images/generated/b.png"));
    }

    [Fact]
    public async Task SameHashFailingTwice_ErrorOnce_ThenDebug()
    {
        var (sut, repo, _, log) = NewSut();
        repo.Text = IndexJson("new", "a.png");

        await Sync(sut);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("a.png", log.Entries.Single(e => e.Level == LogLevel.Error).Message);
        Assert.Contains(Team, log.Entries.Single(e => e.Level == LogLevel.Error).Message);

        log.Entries.Clear();
        await Sync(sut);

        Assert.DoesNotContain(log.Entries, e => e.Level is LogLevel.Error or LogLevel.Warning or LogLevel.Information);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("still incomplete"));
    }

    [Fact]
    public async Task NewHashAfterFailure_ErrorsAgain_AndCompletionLogsInformation()
    {
        var (sut, repo, _, log) = NewSut();
        repo.Text = IndexJson("h1", "a.png");
        await Sync(sut);

        repo.Text = IndexJson("h2", "a.png");   // a different hash failing is a new event
        log.Entries.Clear();
        await Sync(sut);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error);

        repo.Images["a.png"] = [1];
        log.Entries.Clear();
        Assert.True(await Sync(sut));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("sync complete"));
    }

    [Fact]
    public async Task IndexWrite_IsAtomic_NoTempFileLeft()
    {
        var (sut, repo, _, _) = NewSut();
        repo.Text = IndexJson("h1", "a.png");
        repo.Images["a.png"] = [1];

        Assert.True(await Sync(sut));

        Assert.Empty(Directory.GetFiles(Path.Combine(_root, Team), "*.tmp"));
        Assert.True(File.Exists(Path.Combine(_root, Team, "index.json")));
    }

    private sealed class FakeRepository : IRepositoryReader
    {
        public string? Text { get; set; }
        public Dictionary<string, byte[]> Images { get; } = new();
        public List<string> Downloads { get; } = new();
        public bool IsAvailable => true;
        public string SyncSource => "Share";

        public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult(Text);

        public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default)
        {
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
