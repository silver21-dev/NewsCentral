using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Index rejections (signature and path hardening) are never silenced, but a persistent misconfiguration must
/// not log an Error every poll: Error on the first occurrence, on a changed reason, and again once per hour
/// while unchanged; Debug in between; state cleared when the team's index is accepted. Time is faked.
/// </summary>
public sealed class RejectionLogThrottleTests : IDisposable
{
    private const string Team = "cz-its";
    private const string BadPath = "other/images/generated/x.png";   // outside the team's folder

    private static readonly EcdsaSignatureService Ecdsa = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nsvc-throttle-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private (SyncService Sut, CapturingLogger<SyncService> Log, FakeRepository Repo) NewSut(
        string? publicKey, bool requireSigned)
    {
        var settings = new Dictionary<string, string?> { ["Signing:RequireSignedIndex"] = requireSigned.ToString() };
        if (publicKey is not null) settings["Signing:PublicKey"] = publicKey;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var repo = new FakeRepository();
        var log  = new CapturingLogger<SyncService>();
        var sut = new SyncService(repo, new CacheManager(_root, JsonDefaults.Options), lockScreen: null!,
                                  imagePublisher: null!, telemetry: null!, entra: null!, configuration: config,
                                  logger: log, contentChanged: new NullContentChangedNotifier(), timeProvider: _time);
        return (sut, log, repo);
    }

    private static TeamIndexFile NewIndex(string hash) => new()
    {
        TeamFolderName = Team,
        TeamName       = "CZ ITS",
        GeneratedAt    = new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc),
        IndexHash      = hash
    };

    private static void AddBadPath(TeamIndexFile index) =>
        index.PublishedAssignments.Add(new PublishedAssignmentIndex
        {
            Content = new ContentInfo { ImagePath = BadPath, ImageHash = "sha256:" + new string('a', 64) }
        });

    private static string Signed(TeamIndexFile index, string privateKey, string publicKey)
    {
        index.KeyId     = KeyFingerprint.Of(publicKey);
        index.Signature = Ecdsa.Sign(index, privateKey);
        return JsonSerializer.Serialize(index, JsonDefaults.Options);
    }

    private static string Signed(string hash, string privateKey, string publicKey) =>
        Signed(NewIndex(hash), privateKey, publicKey);

    private static string Unsigned(string hash) => JsonSerializer.Serialize(NewIndex(hash), JsonDefaults.Options);

    private Task Sync(SyncService sut) => sut.SyncTeamAsync(Team, CancellationToken.None);

    private static bool IsReject(string m) => m.Contains("index rejected", StringComparison.Ordinal);

    private static int Errors(CapturingLogger<SyncService> log) =>
        log.Entries.Count(e => e.Level == LogLevel.Error && IsReject(e.Message));

    private static int Debugs(CapturingLogger<SyncService> log) =>
        log.Entries.Count(e => e.Level == LogLevel.Debug && IsReject(e.Message));

    // ── Signature rejection ───────────────────────────────────────────────────

    [Fact]
    public async Task SameReason_ErrorFirst_DebugUntilSixtyMinutes_ThenErrorAgain()
    {
        var (priv, _) = SigningKeyTool.GenerateKeyPair();
        var (_, otherPub) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo) = NewSut(otherPub, requireSigned: true);
        repo.Text = Signed("h1", priv, otherPub);               // KeyId matches the configured key, signature wrong

        await Sync(sut);
        Assert.Equal(1, Errors(log));                            // first rejection -> Error

        log.Entries.Clear();
        _time.Advance(TimeSpan.FromSeconds(20));
        await Sync(sut);
        Assert.Equal(0, Errors(log));
        Assert.Equal(1, Debugs(log));                            // +20 s -> Debug

        log.Entries.Clear();
        _time.Advance(TimeSpan.FromMinutes(59) - TimeSpan.FromSeconds(20));
        await Sync(sut);
        Assert.Equal(0, Errors(log));                            // +59 min -> still Debug
        Assert.Equal(1, Debugs(log));

        log.Entries.Clear();
        _time.Advance(TimeSpan.FromMinutes(1));
        await Sync(sut);
        Assert.Equal(1, Errors(log));                            // +60 min -> Error again
    }

    [Fact]
    public async Task HourlyRepeat_RestartsTheWindow()
    {
        var (priv, _) = SigningKeyTool.GenerateKeyPair();
        var (_, otherPub) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo) = NewSut(otherPub, requireSigned: true);
        repo.Text = Signed("h1", priv, otherPub);

        await Sync(sut);
        _time.Advance(TimeSpan.FromMinutes(60));
        await Sync(sut);                                         // second Error; the window restarts here

        log.Entries.Clear();
        _time.Advance(TimeSpan.FromMinutes(30));
        await Sync(sut);
        Assert.Equal(0, Errors(log));
    }

    [Fact]
    public async Task DifferentReason_LogsErrorImmediately()
    {
        var (priv, _) = SigningKeyTool.GenerateKeyPair();
        var (_, otherPub) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo) = NewSut(otherPub, requireSigned: true);
        repo.Text = Signed("h1", priv, otherPub);
        await Sync(sut);                                         // "signature invalid"

        log.Entries.Clear();
        _time.Advance(TimeSpan.FromSeconds(20));
        repo.Text = Unsigned("h1");                              // now "unsigned, signing required"
        await Sync(sut);

        Assert.Equal(1, Errors(log));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("unsigned, signing required"));
    }

    [Fact]
    public async Task AcceptedThenRejectedAgain_LogsErrorImmediately()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var (priv2, pub2) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo) = NewSut(pub, requireSigned: true);

        repo.Text = Signed("h1", priv2, pub2);                   // unknown key -> rejected
        await Sync(sut);
        Assert.Equal(1, Errors(log));

        _time.Advance(TimeSpan.FromSeconds(20));
        repo.Text = Signed("h1", priv, pub);                     // accepted -> state cleared
        await Sync(sut);

        log.Entries.Clear();
        _time.Advance(TimeSpan.FromSeconds(20));
        repo.Text = Signed("h1", priv2, pub2);                   // the same rejection again, 40 s after the first
        await Sync(sut);

        Assert.Equal(1, Errors(log));
    }

    // ── Path-hardening rejection ──────────────────────────────────────────────

    [Fact]
    public async Task PathRejection_FollowsTheSameRule_AndKeepsItsSuffix()
    {
        var (sut, log, repo) = NewSut(publicKey: null, requireSigned: false);
        var index = NewIndex("h1");
        AddBadPath(index);
        repo.Text = JsonSerializer.Serialize(index, JsonDefaults.Options);

        await Sync(sut);
        Assert.Equal(1, Errors(log));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.EndsWith("; previous cached state kept"));

        log.Entries.Clear();
        _time.Advance(TimeSpan.FromSeconds(20));
        await Sync(sut);
        Assert.Equal(0, Errors(log));
        Assert.Equal(1, Debugs(log));

        log.Entries.Clear();
        _time.Advance(TimeSpan.FromMinutes(59));
        await Sync(sut);
        Assert.Equal(0, Errors(log));

        log.Entries.Clear();
        _time.Advance(TimeSpan.FromMinutes(1));
        await Sync(sut);
        Assert.Equal(1, Errors(log));
    }

    [Fact]
    public async Task SignatureRejectionThenPathRejection_IsADifferentReason()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var (sut, log, repo) = NewSut(pub, requireSigned: true);
        repo.Text = Unsigned("h1");                              // unsigned, signing required
        await Sync(sut);

        log.Entries.Clear();
        _time.Advance(TimeSpan.FromSeconds(20));
        var index = NewIndex("h2");
        AddBadPath(index);
        repo.Text = Signed(index, priv, pub);                    // valid signature, bad path
        await Sync(sut);

        Assert.Equal(1, Errors(log));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("previous cached state kept"));
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 19, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

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

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
