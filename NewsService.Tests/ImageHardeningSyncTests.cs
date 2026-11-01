using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models.IndexFile;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// ImagePath / ImageHash hardening (docs/anti-tamper.md §8): one bad path rejects the whole index
/// (nothing written, nothing published); a missing / mismatched hash rejects only that image and
/// keeps the previous file; a cached file is a hit only when its SHA-256 matches.
/// </summary>
public sealed class ImageHardeningSyncTests : IDisposable
{
    private const string Team = "cz-its";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "nsvc-harden-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (SyncService Sut, FakeRepository Repo, CacheManager Cache, CapturingLogger Log) NewSut()
    {
        var repo  = new FakeRepository();
        var cache = new CacheManager(_root, JsonDefaults.Options);
        var log   = new CapturingLogger();
        var sut = new SyncService(repo, cache, lockScreen: null!, imagePublisher: null!, telemetry: null!,
                                  entra: null!, configuration: new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:RequireSignedIndex"] = "false" }).Build(),
                                  logger: log, contentChanged: new NullContentChangedNotifier());
        return (sut, repo, cache, log);
    }

    private static string Hash(byte[] b) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string Path_(string name) => $"{Team}/images/generated/{name}";

    private static string IndexJson(string indexHash, params (string Path, string Hash)[] images)
    {
        var index = new TeamIndexFile
        {
            TeamFolderName = Team,
            TeamName       = "CZ ITS",
            GeneratedAt    = new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc),
            IndexHash      = indexHash
        };
        foreach (var (path, hash) in images)
            index.PublishedAssignments.Add(new PublishedAssignmentIndex
            {
                AssignmentId = "asg-" + Path.GetFileName(path),
                Content      = new ContentInfo { ImagePath = path, ImageHash = hash }
            });
        return JsonSerializer.Serialize(index, JsonDefaults.Options);
    }

    private Task<bool> Sync(SyncService sut) => sut.SyncTeamAsync(Team, CancellationToken.None);

    private string CacheFile(string name) => Path.Combine(_root, Team, "images", "generated", name);

    private async Task<string?> CachedIndexHash(CacheManager cache) =>
        (await cache.ReadJsonAsync<TeamIndexFile>($"{Team}/index.json"))?.IndexHash;

    // ── Index-level path rejection ────────────────────────────────────────────

    [Theory]
    [InlineData("cz-its/images/../../../Windows/Temp/x.png")]
    [InlineData(@"\\localhost\C$\Windows\Temp\x.png")]
    [InlineData(@"C:\Windows\Temp\x.png")]
    [InlineData("other/images/generated/x.png")]
    [InlineData("")]
    public async Task OneBadPath_RejectsWholeIndex_NothingWritten_NoRepositoryReads(string badPath)
    {
        var (sut, repo, cache, log) = NewSut();
        var good = new byte[] { 1 };
        repo.Text = IndexJson("h1", (Path_("good.png"), Hash(good)), (badPath, Hash(good)));
        repo.Images["good.png"] = good;

        Assert.False(await Sync(sut));

        Assert.False(File.Exists(CacheFile("good.png")));          // the valid entry is not synced either
        Assert.Null(await CachedIndexHash(cache));
        Assert.Empty(repo.Downloads);
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("asg-", error.Message);                     // names the entry
    }

    [Fact]
    public async Task BadPath_PreviousCachedIndexAndImagesKept()
    {
        var (sut, repo, cache, _) = NewSut();
        var bytes = new byte[] { 1 };
        repo.Text = IndexJson("old", (Path_("a.png"), Hash(bytes)));
        repo.Images["a.png"] = bytes;
        Assert.True(await Sync(sut));

        repo.Text = IndexJson("new", (Path_("a.png"), Hash(bytes)), ("cz-its/images/../../x.png", Hash(bytes)));
        Assert.False(await Sync(sut));

        Assert.Equal("old", await CachedIndexHash(cache));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(CacheFile("a.png")));
    }

    [Fact]
    public async Task BadPath_RejectedEvenWhenIndexHashUnchanged()
    {
        var (sut, repo, cache, log) = NewSut();
        var bytes = new byte[] { 1 };
        repo.Text = IndexJson("same", (Path_("a.png"), Hash(bytes)));
        repo.Images["a.png"] = bytes;
        Assert.True(await Sync(sut));
        repo.Downloads.Clear(); log.Entries.Clear();

        repo.Text = IndexJson("same", (Path_("a.png"), Hash(bytes)), ("cz-its/images/../../x.png", Hash(bytes)));
        File.Delete(CacheFile("a.png"));                           // would otherwise trigger self-heal

        Assert.False(await Sync(sut));

        Assert.Empty(repo.Downloads);
        Assert.False(File.Exists(CacheFile("a.png")));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task SourceContainmentFailure_RejectsWholeIndex()
    {
        var (sut, repo, cache, log) = NewSut();
        repo.SourceRejects = true;
        repo.Text = IndexJson("h1", (Path_("a.png"), Hash([1])));
        repo.Images["a.png"] = [1];

        Assert.False(await Sync(sut));

        Assert.Empty(repo.Downloads);
        Assert.Null(await CachedIndexHash(cache));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("source path"));
    }

    [Fact]
    public void CacheContainment_CraftedPathResolvingOutsideTeamRoot_Rejected()
    {
        var cache = new CacheManager(_root, JsonDefaults.Options);

        Assert.True(cache.TryValidateImagePath(Team, Path_("a.png"), out _));
        // Passes none of the shape rules, but proves the containment function itself stands alone.
        Assert.False(cache.TryValidateImagePath(Team, $"{Team}/../other/images/generated/a.png", out _));
        Assert.False(cache.TryValidateImagePath(Team, $"{Team}/../../x.png", out _));
    }

    // ── Hash verification ─────────────────────────────────────────────────────

    [Fact]
    public async Task HashMatch_Writes()
    {
        var (sut, repo, cache, _) = NewSut();
        var bytes = new byte[] { 1, 2, 3 };
        repo.Text = IndexJson("h1", (Path_("a.png"), Hash(bytes)));
        repo.Images["a.png"] = bytes;

        Assert.True(await Sync(sut));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(CacheFile("a.png")));
        Assert.Equal("h1", await CachedIndexHash(cache));
    }

    [Fact]
    public async Task HashMismatch_DoesNotWrite_KeepsPreviousFile_IndexNotUpdated()
    {
        var (sut, repo, cache, log) = NewSut();
        var v1 = new byte[] { 1 };
        repo.Text = IndexJson("old", (Path_("a.png"), Hash(v1)));
        repo.Images["a.png"] = v1;
        Assert.True(await Sync(sut));

        var v2 = new byte[] { 2 };
        repo.Text = IndexJson("new", (Path_("a.png"), Hash(v2)));
        repo.Images["a.png"] = [9, 9, 9];                          // bytes do not match the new ImageHash
        log.Entries.Clear();

        Assert.False(await Sync(sut));

        Assert.Equal(v1, await File.ReadAllBytesAsync(CacheFile("a.png")));
        Assert.Equal("old", await CachedIndexHash(cache));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("do not match ImageHash"));
    }

    [Fact]
    public async Task HashMismatch_FreshImage_NothingWritten()
    {
        var (sut, repo, _, _) = NewSut();
        repo.Text = IndexJson("h1", (Path_("a.png"), Hash([1])));
        repo.Images["a.png"] = [2];

        Assert.False(await Sync(sut));

        Assert.False(File.Exists(CacheFile("a.png")));
        Assert.False(File.Exists(CacheFile("a.png") + ".hash"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256:abc")]
    public async Task MissingOrMalformedHash_RejectsThatEntryOnly_OthersStillSync(string badHash)
    {
        var (sut, repo, cache, log) = NewSut();
        var prev = new byte[] { 5 };
        // previous good state for the entry that is about to lose its hash
        repo.Text = IndexJson("old", (Path_("a.png"), Hash(prev)));
        repo.Images["a.png"] = prev;
        Assert.True(await Sync(sut));

        repo.Text = IndexJson("new", (Path_("a.png"), badHash), (Path_("b.png"), Hash([2])));
        repo.Images["a.png"] = [6];
        repo.Images["b.png"] = [2];
        repo.Downloads.Clear(); log.Entries.Clear();

        Assert.False(await Sync(sut));

        Assert.Equal(prev, await File.ReadAllBytesAsync(CacheFile("a.png")));   // previous file kept
        Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(CacheFile("b.png")));   // other entry synced
        Assert.DoesNotContain("a.png", repo.Downloads);                                     // never fetched
        Assert.Equal("old", await CachedIndexHash(cache));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("a.png") && e.Message.Contains("ImageHash"));
    }

    [Fact]
    public async Task CachedFileWithWrongHash_IsRedownloaded()
    {
        var (sut, repo, cache, _) = NewSut();
        var good = new byte[] { 1, 2 };
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile("a.png"))!);
        await File.WriteAllBytesAsync(CacheFile("a.png"), [66, 66]);             // tampered / corrupt
        await File.WriteAllTextAsync(CacheFile("a.png") + ".hash", Hash(good));    // sidecar lies; must not be trusted
        repo.Text = IndexJson("h1", (Path_("a.png"), Hash(good)));
        repo.Images["a.png"] = good;

        Assert.True(await Sync(sut));

        Assert.Equal(new[] { "a.png" }, repo.Downloads);
        Assert.Equal(good, await File.ReadAllBytesAsync(CacheFile("a.png")));
    }

    [Fact]
    public async Task CachedFileWithMatchingHash_IsAHit_NoDownload()
    {
        var (sut, repo, _, _) = NewSut();
        var good = new byte[] { 1, 2 };
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile("a.png"))!);
        await File.WriteAllBytesAsync(CacheFile("a.png"), good);
        repo.Text = IndexJson("h1", (Path_("a.png"), Hash(good)));

        Assert.True(await Sync(sut));

        Assert.Empty(repo.Downloads);
    }

    [Fact]
    public async Task UpperCaseHex_Matches()
    {
        var (sut, repo, _, _) = NewSut();
        var bytes = new byte[] { 7 };
        repo.Text = IndexJson("h1", (Path_("a.png"), "sha256:" + Convert.ToHexString(SHA256.HashData(bytes))));
        repo.Images["a.png"] = bytes;

        Assert.True(await Sync(sut));
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeRepository : IRepositoryReader
    {
        public string? Text { get; set; }
        public bool SourceRejects { get; set; }
        public Dictionary<string, byte[]> Images { get; } = new();
        public List<string> Downloads { get; } = new();
        public bool IsAvailable => true;
        public string SyncSource => "Share";

        public bool TryValidateImagePath(string teamFolder, string imagePath, out string reason)
        {
            reason = SourceRejects ? "rejected by fake source" : "ok";
            return !SourceRejects;
        }

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
