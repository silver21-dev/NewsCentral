using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models.IndexFile;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Self-heal hashes every cached image (docs/anti-tamper.md §8): a file replaced in place is logged,
/// deleted and re-downloaded through the verified path; with the source unreachable the slot stays empty.
/// </summary>
public sealed class CacheIntegrityHealTests : IDisposable
{
    private const string Team = "cz-its";
    private static readonly byte[] Good = [1, 2, 3, 4];
    private static readonly byte[] Evil = [6, 6, 6];

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "nsvc-integrity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private (SyncService Sut, FakeRepository Repo, CapturingLogger Log) NewSut()
    {
        var repo = new FakeRepository();
        var log  = new CapturingLogger();
        var sut  = new SyncService(repo, new CacheManager(_root, JsonDefaults.Options), lockScreen: null!,
                                   imagePublisher: null!, telemetry: null!, entra: null!,
                                   configuration: new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:RequireSignedIndex"] = "false" }).Build(), logger: log,
                                   contentChanged: new NullContentChangedNotifier());
        return (sut, repo, log);
    }

    private string CachePath => Path.Combine(_root, Team, "images", "generated", "a.png");

    private static string IndexJson()
    {
        var index = new TeamIndexFile
        {
            TeamFolderName = Team, TeamName = Team, IndexHash = "h1",
            GeneratedAt = new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc)
        };
        index.PublishedAssignments.Add(new PublishedAssignmentIndex
        {
            AssignmentId = "asg-1",
            Content = new ContentInfo { ImagePath = $"{Team}/images/generated/a.png", ImageHash = "sha256:" + Hex(Good) }
        });
        return JsonSerializer.Serialize(index, JsonDefaults.Options);
    }

    private Task<bool> Sync(SyncService sut) => sut.SyncTeamAsync(Team, CancellationToken.None);

    private async Task Seed(SyncService sut, FakeRepository repo, CapturingLogger log)
    {
        repo.Text = IndexJson();
        repo.Images["a.png"] = Good;
        Assert.True(await Sync(sut));
        repo.Downloads.Clear(); log.Entries.Clear();
    }

    [Fact]
    public async Task TamperedFile_Detected_Deleted_RestoredWithMatchingBytes()
    {
        var (sut, repo, log) = NewSut();
        await Seed(sut, repo, log);
        await File.WriteAllBytesAsync(CachePath, Evil);

        Assert.False(await Sync(sut));   // unchanged index: heal only, no change reported

        Assert.Equal(Good, await File.ReadAllBytesAsync(CachePath));
        Assert.Equal(new[] { "a.png" }, repo.Downloads);
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(Team, error.Message);
        Assert.Contains("asg-1", error.Message);
        Assert.Contains("images/generated/a.png", error.Message);
        Assert.Contains(Hex(Good), error.Message);   // expected
        Assert.Contains(Hex(Evil), error.Message);   // actual
    }

    [Fact]
    public async Task TamperedFile_SourceUnreachable_FileAbsent_ErrorLogged()
    {
        var (sut, repo, log) = NewSut();
        await Seed(sut, repo, log);
        await File.WriteAllBytesAsync(CachePath, Evil);
        repo.Images.Clear();   // nothing to download

        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);

        Assert.False(File.Exists(CachePath));
        Assert.False(File.Exists(CachePath + ".hash"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(Hex(Evil)));
    }

    [Fact]
    public async Task TamperedFile_RedownloadFailsVerification_SlotLeftEmpty()
    {
        var (sut, repo, log) = NewSut();
        await Seed(sut, repo, log);
        await File.WriteAllBytesAsync(CachePath, Evil);
        repo.Images["a.png"] = [7, 7];   // source also serves wrong bytes

        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);

        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task HealthyCache_NoDownloads_NoLogsAboveDebug()
    {
        var (sut, repo, log) = NewSut();
        await Seed(sut, repo, log);

        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);
        Assert.False(await Sync(sut));

        Assert.Empty(repo.Downloads);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(Good, await File.ReadAllBytesAsync(CachePath));
    }

    [Fact]
    public async Task RepeatedMismatch_ErrorOnce_ThenDebug_NewContentErrorsAgain()
    {
        var (sut, repo, log) = NewSut();
        await Seed(sut, repo, log);
        repo.Images.Clear();

        await File.WriteAllBytesAsync(CachePath, Evil);
        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("does not match ImageHash"));

        log.Entries.Clear();
        await File.WriteAllBytesAsync(CachePath, Evil);            // same tampered content again
        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("does not match ImageHash"));

        log.Entries.Clear();
        await File.WriteAllBytesAsync(CachePath, [9]);              // different tampered content
        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("does not match ImageHash"));
    }

    [Fact]
    public async Task AbsentFile_StillRestoredThroughVerifiedPath()
    {
        var (sut, repo, log) = NewSut();
        await Seed(sut, repo, log);
        File.Delete(CachePath);

        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);

        Assert.Equal(Good, await File.ReadAllBytesAsync(CachePath));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("does not match ImageHash"));
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
