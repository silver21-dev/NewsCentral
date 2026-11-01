using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models.IndexFile;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// ContentChanged is coalesced to one notification per sync cycle, raised after ALL teams finish
/// and only when at least one team produced an accepted, complete change.
/// </summary>
public sealed class ContentChangedCycleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "nsvc-cycle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (SyncService Sut, RecordingNotifier Notifier, FakeRepository Repo, List<string> Events) NewSut()
    {
        var events   = new List<string>();
        var repo     = new FakeRepository(events);
        var notifier = new RecordingNotifier(events);
        var sut = new SyncService(repo, new CacheManager(_root, JsonDefaults.Options), lockScreen: null!,
                                  imagePublisher: null!, telemetry: null!, entra: null!,
                                  configuration: new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:RequireSignedIndex"] = "false" }).Build(),
                                  logger: new NullLogger(), contentChanged: notifier);
        return (sut, notifier, repo, events);
    }

    private static string IndexJson(string team, string hash, string? image)
    {
        var index = new TeamIndexFile
        {
            TeamFolderName = team,
            TeamName       = team,
            GeneratedAt    = new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc),
            IndexHash      = hash
        };
        if (image is not null)
            index.PublishedAssignments.Add(new PublishedAssignmentIndex
            {
                Content = new ContentInfo { ImagePath = $"{team}/images/generated/{image}", ImageHash = Hash([1]) }
            });
        return JsonSerializer.Serialize(index, JsonDefaults.Options);
    }

    private static string Hash(byte[] b) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    private static Task Cycle(SyncService sut, params string[] teams) =>
        sut.SyncAllTeamsAsync(teams, CancellationToken.None);

    [Fact]
    public async Task TwoTeamsChange_NotifierCalledOnce_AfterLastTeamFinished()
    {
        var (sut, notifier, repo, events) = NewSut();
        repo.Texts["a/index.json"] = IndexJson("a", "h1", "x.png");
        repo.Texts["b/index.json"] = IndexJson("b", "h1", "y.png");
        repo.Images["x.png"] = [1];
        repo.Images["y.png"] = [1];

        await Cycle(sut, "a", "b");

        Assert.Equal(1, notifier.Calls);
        Assert.Equal("notify", events[^1]);                  // after every download
        Assert.Contains("download y.png", events);
    }

    [Fact]
    public async Task NoTeamChanges_NoCalls()
    {
        var (sut, notifier, repo, _) = NewSut();
        repo.Texts["a/index.json"] = IndexJson("a", "h1", null);
        await Cycle(sut, "a");
        Assert.Equal(1, notifier.Calls);

        await Cycle(sut, "a");
        await Cycle(sut, "a");

        Assert.Equal(1, notifier.Calls);   // only the initial change
    }

    [Fact]
    public async Task OneCompleteOneIncomplete_OneCall()
    {
        var (sut, notifier, repo, _) = NewSut();
        repo.Texts["a/index.json"] = IndexJson("a", "h1", "x.png");
        repo.Texts["b/index.json"] = IndexJson("b", "h1", "missing.png");
        repo.Images["x.png"] = [1];

        await Cycle(sut, "a", "b");

        Assert.Equal(1, notifier.Calls);
    }

    [Fact]
    public async Task AllTeamsIncomplete_NoCalls()
    {
        var (sut, notifier, repo, _) = NewSut();
        repo.Texts["a/index.json"] = IndexJson("a", "h1", "missing1.png");
        repo.Texts["b/index.json"] = IndexJson("b", "h1", "missing2.png");

        await Cycle(sut, "a", "b");

        Assert.Equal(0, notifier.Calls);
    }

    private sealed class RecordingNotifier(List<string> events) : IContentChangedNotifier
    {
        public int Calls { get; private set; }
        public void NotifyContentChanged() { Calls++; events.Add("notify"); }
    }

    private sealed class FakeRepository(List<string> events) : IRepositoryReader
    {
        public Dictionary<string, string> Texts { get; } = new();
        public Dictionary<string, byte[]> Images { get; } = new();
        public bool IsAvailable => true;
        public string SyncSource => "Share";

        public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult(Texts.TryGetValue(relativePath, out var t) ? t : null);

        public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default)
        {
            var name = Path.GetFileName(relativePath);
            events.Add("download " + name);
            return Task.FromResult(Images.TryGetValue(name, out var b) ? b : null);
        }
    }

    private sealed class NullLogger : ILogger<SyncService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }
    }
}
