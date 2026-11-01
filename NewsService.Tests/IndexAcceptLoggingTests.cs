using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers the "log on change" rule for index acceptance in SyncService.SyncTeamAsync: rejection
/// always logs Error; acceptance logs at its natural level (Unsigned → Warning, otherwise
/// Information) only when the index changed or the verification result changed since the previous
/// cycle for that team, and at Debug otherwise. Uses a fake IRepositoryReader, a real CacheManager
/// on a temp directory, in-memory configuration, and the CapturingLogger pattern from
/// LockScreenApplyTests. Verification behaviour itself is unchanged and covered elsewhere.
/// </summary>
public sealed class IndexAcceptLoggingTests : IDisposable
{
    private const string Team = "cz-its";

    private static readonly EcdsaSignatureService Ecdsa = new();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "nsvc-accept-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private (SyncService Sut, CapturingLogger<SyncService> Log, FakeRepository Repo, CacheManager Cache)
        NewSut(string publicKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signing:PublicKey"] = publicKey,
                ["Signing:RequireSignedIndex"] = "false"
            })
            .Build();

        var repo  = new FakeRepository();
        var cache = new CacheManager(_root, JsonDefaults.Options);
        var log   = new CapturingLogger<SyncService>();
        // Only repository, cache, configuration, and logger are exercised by SyncTeamAsync.
        var sut = new SyncService(repo, cache, lockScreen: null!, imagePublisher: null!, telemetry: null!,
                                  entra: null!, configuration: config, logger: log,
                                  contentChanged: new NullContentChangedNotifier());
        return (sut, log, repo, cache);
    }

    private static TeamIndexFile NewIndex(string hash) => new()
    {
        TeamFolderName = Team,
        TeamName       = "CZ ITS",
        GeneratedAt    = new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc),  // whole-second
        IndexHash      = hash
    };

    private static string SignedJson(string hash, string privateKey, string publicKey)
    {
        var index = NewIndex(hash);
        index.KeyId     = KeyFingerprint.Of(publicKey);
        index.Signature = Ecdsa.Sign(index, privateKey);
        return JsonSerializer.Serialize(index, JsonDefaults.Options);
    }

    private static string UnsignedJson(string hash) =>
        JsonSerializer.Serialize(NewIndex(hash), JsonDefaults.Options);

    private static bool IsAccept(string message) => message.Contains("index accepted");
    private static bool IsReject(string message) => message.Contains("index rejected");

    private Task Sync(SyncService sut) => sut.SyncTeamAsync(Team, CancellationToken.None);

    // ── Facts ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidUnchanged_SecondCycle_AcceptIsDebug_NotInformation()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo, cache) = NewSut(pub);
        repo.Text = SignedJson("h1", priv, pub);
        await cache.WriteTextAsync($"{Team}/index.json", repo.Text);   // cache already current

        // First cycle: result first seen for this team → natural level (Information).
        await Sync(sut);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && IsAccept(e.Message));

        // Second cycle, nothing changed: accept line demoted to Debug; nothing above Debug at all.
        log.Entries.Clear();
        await Sync(sut);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && IsAccept(e.Message));
        Assert.DoesNotContain(log.Entries,
            e => e.Level is LogLevel.Information or LogLevel.Warning or LogLevel.Error);
    }

    [Fact]
    public async Task ValidChanged_AcceptIsInformation()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo, cache) = NewSut(pub);
        repo.Text = SignedJson("h1", priv, pub);
        await cache.WriteTextAsync($"{Team}/index.json", repo.Text);

        await Sync(sut);            // settle: result now known, index unchanged
        log.Entries.Clear();

        repo.Text = SignedJson("h2", priv, pub);   // index CHANGED, result still Valid
        await Sync(sut);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && IsAccept(e.Message));
        // The changed index was actually synced to cache.
        var cached = await cache.ReadJsonAsync<TeamIndexFile>($"{Team}/index.json");
        Assert.Equal("h2", cached?.IndexHash);
    }

    [Fact]
    public async Task UnsignedUnchanged_WarningFirstCycle_DebugSecondCycle()
    {
        var (_, pub) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo, cache) = NewSut(pub);
        repo.Text = UnsignedJson("h1");                                // key configured, no signature
        await cache.WriteTextAsync($"{Team}/index.json", repo.Text);

        // First cycle: Unsigned first seen → Warning (the state is reported at least once).
        await Sync(sut);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && IsAccept(e.Message));

        // Second cycle, same result, same index → Debug; no Warning repeated.
        log.Entries.Clear();
        await Sync(sut);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && IsAccept(e.Message));
        Assert.DoesNotContain(log.Entries,
            e => e.Level is LogLevel.Information or LogLevel.Warning or LogLevel.Error);
    }

    [Fact]
    public async Task Rejected_LogsError_ThenDebugWhileUnchanged_AndNeverCaches()
    {
        var (priv, _) = SigningKeyTool.GenerateKeyPair();
        var (_, otherPub) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo, _) = NewSut(otherPub);       // wrong key → Invalid → rejected
        repo.Text = SignedJson("h1", priv, otherPub);

        await Sync(sut);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && IsReject(e.Message));

        // Second cycle: identical outcome - still reported (never silent), but at Debug until the hourly
        // repeat or a changed reason (see RejectionLogThrottleTests).
        log.Entries.Clear();
        await Sync(sut);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && IsReject(e.Message));
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);

        Assert.DoesNotContain(log.Entries, e => IsAccept(e.Message));
        Assert.False(File.Exists(Path.Combine(_root, Team, "index.json")));   // content never trusted
    }

    [Fact]
    public async Task ResultChanges_IndexUnchanged_LogsAtNaturalLevel()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo, cache) = NewSut(pub);
        repo.Text = UnsignedJson("h1");
        await cache.WriteTextAsync($"{Team}/index.json", repo.Text);   // hash stays h1 throughout

        await Sync(sut);            // Unsigned recorded (Warning)
        log.Entries.Clear();

        repo.Text = SignedJson("h1", priv, pub);   // same IndexHash, but now validly signed
        await Sync(sut);

        // Index unchanged, but Unsigned → Valid is a state change → Information, not Debug.
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && IsAccept(e.Message));
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeRepository : IRepositoryReader
    {
        public string? Text { get; set; }
        public bool IsAvailable => true;
        public string SyncSource => "Share";

        public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult(Text);

        public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
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
