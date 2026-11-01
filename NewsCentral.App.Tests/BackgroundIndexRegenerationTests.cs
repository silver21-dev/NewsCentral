using NewsCentral.Audit;
using NewsCentral.Models;
using NewsCentral.Security;
using NewsCentral.Services;

namespace NewsCentral.App.Tests;

/// <summary>
/// The index regeneration that follows an assignment delete or a presentation update/delete cannot fail the operation
/// that already happened, but an unreadable signing key must reach the user and the audit log, and no unsigned index
/// may be written. The key is shared by the whole environment, so there is one notice, not one per team.
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

    private const string EnvironmentWideMessage =
        "Distributed indexes are not being updated because the environment signing key could not be read.";

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

    private EnvironmentKeyService UnreadableKeyService() => new(
        () => Task.FromResult(new SecureRootResult(Path.Combine(_root, "secure"), null)),
        () => true,
        root => new DeniedStorage(root),
        _audit);

    /// <summary>
    /// What IndexGenerationService does before it writes anything: load the key, apply the gate, and only then write
    /// the index. The write is a marker file, so a test can see whether one happened.
    /// </summary>
    private static Func<Task> Generator(EnvironmentKeyService keys, string indexPath) => async () =>
    {
        var decision = SigningKeyGate.Decide(await keys.LoadAsync());
        if (decision.Action == SigningAction.Block)
            throw new SigningKeyUnreadableException(decision.BlockReason!);
        await File.WriteAllTextAsync(indexPath, "index written");
    };

    [Fact]
    public async Task UnreadableKey_ShowsOneEnvironmentWideWarning_AuditsTheReasonPerTeam_AndWritesNoIndex()
    {
        var indexPath = Path.Combine(_root, "index.json");
        await File.WriteAllTextAsync(indexPath, "old signed index");

        var regenerated = await _background.RunAsync("cz-its", Generator(UnreadableKeyService(), indexPath));

        Assert.False(regenerated);
        var notice = Assert.Single(_notices.Items);
        Assert.Equal(UserNoticeLevel.Warning, notice.Level);
        Assert.Equal(EnvironmentWideMessage, notice.Message);
        Assert.Equal("old signed index", await File.ReadAllTextAsync(indexPath));   // the old index stays

        var record = Assert.Single(_audit.Records, r => r.Event == AuditEvents.IndexRegenerationFailed);
        Assert.Equal("cz-its", record.Target);   // the audit record stays per team
        Assert.Equal(AuditOutcome.Failure, record.Outcome);
        Assert.Equal("access denied", record.Detail);
    }

    [Fact]
    public async Task ReadableOrAbsentKey_RegeneratesWithoutAWarning()
    {
        var indexPath = Path.Combine(_root, "index.json");
        var noKey = new EnvironmentKeyService(
            () => Task.FromResult(new SecureRootResult(Path.Combine(_root, "secure"), null)),
            () => true,
            audit: _audit);

        var regenerated = await _background.RunAsync("cz-its", Generator(noKey, indexPath));

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
    public async Task SeveralTeamsHittingTheSameUnreadableKey_ProduceOneNotice_AndCanBeDismissed()
    {
        var keys = UnreadableKeyService();
        var indexPath = Path.Combine(_root, "index.json");

        await _background.RunAsync("cz-its", Generator(keys, indexPath));
        await _background.RunAsync("cz-its", Generator(keys, indexPath));
        await _background.RunAsync("de-its", Generator(keys, indexPath));

        Assert.Single(_notices.Items);
        Assert.Equal(3, _audit.Records.Count(r => r.Event == AuditEvents.IndexRegenerationFailed));   // every attempt is audited

        var raised = 0;
        _notices.Changed += () => raised++;
        _notices.Dismiss(_notices.Items[0].Id);
        Assert.Empty(_notices.Items);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task AnySuccessfulGeneration_ClearsTheEnvironmentNotice()
    {
        await _background.RunAsync("cz-its", Generator(UnreadableKeyService(), Path.Combine(_root, "index.json")));
        Assert.Single(_notices.Items);

        // A publish or regeneration saved an index (what GenerateAndSaveIndexAsync reports): the key was readable.
        _background.IndexGenerated();

        Assert.Empty(_notices.Items);
    }

    [Fact]
    public async Task ALaterSuccessfulBackgroundRegeneration_ClearsTheNotice()
    {
        await _background.RunAsync("cz-its", Generator(UnreadableKeyService(), Path.Combine(_root, "index.json")));

        Assert.True(await _background.RunAsync("de-its", () => Task.CompletedTask));

        Assert.Empty(_notices.Items);
    }

    [Fact]
    public async Task AManuallyDismissedBanner_StaysGone_AndClearingAnAbsentOneIsHarmless()
    {
        await _background.RunAsync("cz-its", Generator(UnreadableKeyService(), Path.Combine(_root, "index.json")));
        _notices.Dismiss(_notices.Items[0].Id);

        _background.IndexGenerated();

        Assert.Empty(_notices.Items);
    }

    [Fact]
    public void ThePublishMessage_NamesTheReason_AndSaysNothingWasPublished()
    {
        var message = new SigningKeyUnreadableException("access denied").Message;

        Assert.Equal(
            "The environment signing key could not be read: access denied. Nothing was published and the existing index " +
            "is unchanged. Ask a system administrator to restore access to the key.",
            message);
    }
}
