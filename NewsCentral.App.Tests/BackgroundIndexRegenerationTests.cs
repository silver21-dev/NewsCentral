using NewsCentral.Audit;
using NewsCentral.Models;
using NewsCentral.Security;
using NewsCentral.Services;

namespace NewsCentral.App.Tests;

/// <summary>
/// The index regeneration that follows an assignment delete or a presentation update/delete cannot fail the operation
/// that already happened, but an unreadable signing key must reach the user and the audit log, and no unsigned index
/// may be written.
/// </summary>
public sealed class BackgroundIndexRegenerationTests : IDisposable
{
    private sealed class CapturingAudit : IAuditLog
    {
        public List<AuditRecord> Records { get; } = new();
        public bool WriteFailing => false;
        public event Action? StatusChanged { add { } remove { } }
        public Task RecordAsync(AuditRecord record) { Records.Add(record); return Task.CompletedTask; }
    }

    private sealed class DeniedStorage : ISecureStorage
    {
        public DeniedStorage(string root) => Root = root;
        public string Root { get; }
        public Task<byte[]?> ReadBytesAsync(string relativePath) => throw new UnauthorizedAccessException();
        public Task WriteBytesAsync(string relativePath, byte[] content) => throw new UnauthorizedAccessException();
        public Task DeleteAsync(string relativePath) => throw new UnauthorizedAccessException();
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-bgindex-" + Guid.NewGuid().ToString("N"));
    private readonly UserNotices _notices = new();
    private readonly CapturingAudit _audit = new();
    private readonly BackgroundIndexRegeneration _background;

    public BackgroundIndexRegenerationTests()
    {
        Directory.CreateDirectory(_root);
        _background = new BackgroundIndexRegeneration(_notices, _audit);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private TeamKeyService UnreadableKeyService() => new(
        () => Path.Combine(_root, "data"),
        () => Task.FromResult(new SecureRootResult(Path.Combine(_root, "secure"), null)),
        root => new DeniedStorage(root),
        _audit);

    /// <summary>
    /// What IndexGenerationService does before it writes anything: load the key, apply the gate, and only then write
    /// the index. The write is a marker file, so a test can see whether one happened.
    /// </summary>
    private Func<Task> Generator(string team, TeamKeyService keys, string indexPath) => async () =>
    {
        var decision = SigningKeyGate.Decide(await keys.LoadAsync(team));
        if (decision.Action == SigningAction.Block)
            throw new SigningKeyUnreadableException(team, decision.BlockReason!);
        await File.WriteAllTextAsync(indexPath, "index written");
    };

    [Fact]
    public async Task UnreadableKey_ShowsAWarningNamingTheTeam_AuditsTheReason_AndWritesNoIndex()
    {
        var indexPath = Path.Combine(_root, "index.json");
        await File.WriteAllTextAsync(indexPath, "old signed index");

        var regenerated = await _background.RunAsync("cz-its", Generator("cz-its", UnreadableKeyService(), indexPath));

        Assert.False(regenerated);
        var notice = Assert.Single(_notices.Items);
        Assert.Equal(UserNoticeLevel.Warning, notice.Level);
        Assert.Equal(
            "The distributed index for cz-its was not updated because its signing key could not be read.",
            notice.Message);
        Assert.Equal("old signed index", await File.ReadAllTextAsync(indexPath));   // the old index stays

        var record = Assert.Single(_audit.Records, r => r.Event == AuditEvents.IndexRegenerationFailed);
        Assert.Equal("cz-its", record.Target);
        Assert.Equal(AuditOutcome.Failure, record.Outcome);
        Assert.Equal("access denied", record.Detail);
    }

    [Fact]
    public async Task ReadableOrAbsentKey_RegeneratesWithoutAWarning()
    {
        var indexPath = Path.Combine(_root, "index.json");
        var noKey = new TeamKeyService(
            () => Path.Combine(_root, "data"),
            () => Task.FromResult(new SecureRootResult(Path.Combine(_root, "secure"), null)),
            audit: _audit);

        var regenerated = await _background.RunAsync("cz-its", Generator("cz-its", noKey, indexPath));

        Assert.True(regenerated);
        Assert.Empty(_notices.Items);
        Assert.DoesNotContain(_audit.Records, r => r.Event == AuditEvents.IndexRegenerationFailed);
    }

    [Fact]
    public async Task OtherFailures_StayQuiet_AsBefore()
    {
        var regenerated = await _background.RunAsync("cz-its", () => throw new IOException("share offline"));

        Assert.False(regenerated);
        Assert.Empty(_notices.Items);
        Assert.Empty(_audit.Records);
    }

    [Fact]
    public async Task TheSameWarningTwice_IsOneNotice_AndCanBeDismissed()
    {
        var keys = UnreadableKeyService();
        var indexPath = Path.Combine(_root, "index.json");

        await _background.RunAsync("cz-its", Generator("cz-its", keys, indexPath));
        await _background.RunAsync("cz-its", Generator("cz-its", keys, indexPath));
        await _background.RunAsync("de-its", Generator("de-its", keys, indexPath));

        Assert.Equal(2, _notices.Items.Count);
        Assert.Equal(3, _audit.Records.Count(r => r.Event == AuditEvents.IndexRegenerationFailed));   // every attempt is audited

        var raised = 0;
        _notices.Changed += () => raised++;
        _notices.Dismiss(_notices.Items[0].Id);
        Assert.Single(_notices.Items);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task ALaterSuccessForTheSameTeam_ClearsTheBanner_ButAnotherTeamsSuccessDoesNot()
    {
        var keys = UnreadableKeyService();
        var indexPath = Path.Combine(_root, "index.json");
        await _background.RunAsync("cz-its", Generator("cz-its", keys, indexPath));
        await _background.RunAsync("de-its", Generator("de-its", keys, indexPath));
        Assert.Equal(2, _notices.Items.Count);

        // A successful generation for another team (what GenerateAndSaveIndexAsync reports after a save).
        _background.IndexGenerated("sk-team");
        await _background.RunAsync("sk-team", () => Task.CompletedTask);
        Assert.Equal(2, _notices.Items.Count);

        // A publish for cz-its saved its index: only the cz-its banner goes.
        _background.IndexGenerated("cz-its");
        var remaining = Assert.Single(_notices.Items);
        Assert.Contains("de-its", remaining.Message);

        // A later successful background regeneration clears the other one too.
        Assert.True(await _background.RunAsync("de-its", () => Task.CompletedTask));
        Assert.Empty(_notices.Items);
    }

    [Fact]
    public async Task AManuallyDismissedBanner_StaysGone_AndClearingAnAbsentOneIsHarmless()
    {
        await _background.RunAsync("cz-its", Generator("cz-its", UnreadableKeyService(), Path.Combine(_root, "index.json")));
        _notices.Dismiss(_notices.Items[0].Id);

        _background.IndexGenerated("cz-its");

        Assert.Empty(_notices.Items);
    }

    [Fact]
    public void ThePublishMessage_NamesTheTeamAndTheReason_AndSaysNothingWasPublished()
    {
        var message = new SigningKeyUnreadableException("cz-its", "access denied").Message;

        Assert.StartsWith("Signing key for cz-its could not be read: access denied.", message);
        Assert.Contains("Nothing was published", message);
    }
}
