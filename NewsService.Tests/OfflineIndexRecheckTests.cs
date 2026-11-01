using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// While the repository is unreachable the cached index is re-verified against the keys trusted now. A failure is
/// logged through the rejection throttle but NOT enforced: self-heal still runs. Time is faked.
/// </summary>
public sealed class OfflineIndexRecheckTests : IDisposable
{
    private const string Team = "cz-its";
    private const string Suffix = "repository unreachable, cached content still in use";
    private static readonly byte[] Good = [1, 2, 3, 4];
    private static readonly EcdsaSignatureService Ecdsa = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nsvc-offline-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private (SyncService Sut, CapturingLogger Log, FakeRepository Repo) NewSut(string? publicKey, bool requireSigned)
    {
        var settings = new Dictionary<string, string?> { ["Signing:RequireSignedIndex"] = requireSigned.ToString() };
        if (publicKey is not null) settings["Signing:PublicKey"] = publicKey;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var repo = new FakeRepository();
        var log  = new CapturingLogger();
        var sut  = new SyncService(repo, new CacheManager(_root, JsonDefaults.Options), lockScreen: null!,
                                   imagePublisher: null!, telemetry: null!, entra: null!, configuration: config,
                                   logger: log, contentChanged: new NullContentChangedNotifier(), timeProvider: _time);
        return (sut, log, repo);
    }

    private static TeamIndexFile NewIndex()
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
        return index;
    }

    private static string Signed(string privateKey, string publicKey)
    {
        var index = NewIndex();
        index.KeyId     = KeyFingerprint.Of(publicKey);
        index.Signature = Ecdsa.Sign(index, privateKey);
        return JsonSerializer.Serialize(index, JsonDefaults.Options);
    }

    private static string Unsigned() => JsonSerializer.Serialize(NewIndex(), JsonDefaults.Options);

    /// <summary>Caches the index (as an online cycle would have), then makes the repository unreachable.</summary>
    private async Task Seed(SyncService sut, FakeRepository repo, CapturingLogger log, string indexJson)
    {
        // Seed the cache directly: the key trusted at the time may be one that is no longer configured.
        var cache = new CacheManager(_root, JsonDefaults.Options);
        await cache.WriteTextAtomicAsync($"{Team}/index.json", indexJson);
        repo.Images["a.png"] = Good;
        log.Entries.Clear();
    }

    private string CachePath => Path.Combine(_root, Team, "images", "generated", "a.png");

    private static int Errors(CapturingLogger log) =>
        log.Entries.Count(e => e.Level == LogLevel.Error && e.Message.Contains(Suffix, StringComparison.Ordinal));

    private static int Debugs(CapturingLogger log) =>
        log.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains(Suffix, StringComparison.Ordinal));

    [Fact]
    public async Task CachedIndex_SignedWithAKeyNoLongerConfigured_LogsOneError_AndSelfHealStillRuns()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var (_, current) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo) = NewSut(current, requireSigned: true);
        await Seed(sut, repo, log, Signed(priv, pub));   // cached under a key the registry no longer trusts

        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);

        Assert.Equal(1, Errors(log));
        // Not enforced: the missing image was still restored by self-heal.
        Assert.Equal(Good, await File.ReadAllBytesAsync(CachePath));
        Assert.Contains("a.png", repo.Downloads);
    }

    [Fact]
    public async Task SecondOfflineCycleWithinTheHour_IsDebugOnly()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var (_, current) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo) = NewSut(current, requireSigned: true);
        await Seed(sut, repo, log, Signed(priv, pub));
        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);

        log.Entries.Clear();
        _time.Advance(TimeSpan.FromMinutes(5));
        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);

        Assert.Equal(0, Errors(log));
        Assert.Equal(1, Debugs(log));
    }

    [Fact]
    public async Task ValidCachedIndex_LogsNoError()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo) = NewSut(pub, requireSigned: true);
        await Seed(sut, repo, log, Signed(priv, pub));

        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);

        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
        Assert.Equal(0, Debugs(log));
    }

    [Fact]
    public async Task UnsignedCachedIndex_WithSigningRequired_LogsError()
    {
        var (_, pub) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo) = NewSut(pub, requireSigned: true);
        await Seed(sut, repo, log, Unsigned());

        await sut.HealCachedTeamsAsync([Team], CancellationToken.None);

        Assert.Equal(1, Errors(log));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 19, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeRepository : IRepositoryReader
    {
        public Dictionary<string, byte[]> Images { get; } = new();
        public List<string> Downloads { get; } = new();
        public bool IsAvailable => true;
        public string SyncSource => "Share";

        public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

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
