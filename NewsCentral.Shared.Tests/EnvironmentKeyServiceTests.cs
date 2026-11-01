using NewsCentral.Audit;
using NewsCentral.Models;
using NewsCentral.Security;

namespace NewsCentral.Shared.Tests;

public sealed class EnvironmentKeyServiceTests : SecureStoreTestBase
{
    private sealed class CapturingAudit : IAuditLog
    {
        public List<AuditRecord> Records { get; } = new();
        public bool WriteFailing => false;
        public event Action? StatusChanged { add { } remove { } }
        public Task RecordAsync(AuditRecord record) { Records.Add(record); return Task.CompletedTask; }
    }

    private static readonly DateTime Now = new(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc);

    private readonly CapturingAudit _audit = new();
    private bool _admin = true;

    private EnvironmentKeyService Service(Func<Task<SecureRootResult>>? root = null) => new(
        root ?? (() => Task.FromResult(new SecureRootResult(SecurePath, null))),
        () => _admin,
        audit: _audit,
        utcNow: () => Now);

    private static string? Id(SigningKeyPair? p) => KeyFingerprint.Of(p?.PublicKey);

    private async Task<EnvironmentSigningKeys> Load(EnvironmentKeyService svc) => (await svc.LoadAsync()).Keys!;

    /// <summary>Current only.</summary>
    private async Task<EnvironmentKeyService> WithCurrent()
    {
        var svc = Service();
        await svc.GenerateAsync(null);
        return svc;
    }

    /// <summary>Current + Next.</summary>
    private async Task<EnvironmentKeyService> WithNext()
    {
        var svc = await WithCurrent();
        await svc.PrepareNextAsync(Id((await Load(svc)).Current));
        return svc;
    }

    /// <summary>Current + PreviousPublicKey (after an activation).</summary>
    private async Task<EnvironmentKeyService> WithPrevious()
    {
        var svc = await WithNext();
        var cur = await Load(svc);
        await svc.ActivateNextAsync(Id(cur.Current), Id(cur.Next));
        return svc;
    }

    // ── success paths and state transitions ─────────────────────────────────

    [Fact]
    public async Task Generate_CreatesCurrent_WithCreatedAt()
    {
        var svc = Service();

        var keys = await svc.GenerateAsync(null);

        Assert.NotNull(keys.Current!.PrivateKey);
        Assert.NotNull(keys.Current.PublicKey);
        Assert.Equal(Now, keys.Current.CreatedAt);
        Assert.Null(keys.Next);
        Assert.Null(keys.PreviousPublicKey);
        var onDisk = await Load(svc);
        Assert.Equal(keys.Current.PublicKey, onDisk.Current!.PublicKey);
        Assert.Equal(Now, onDisk.Current.CreatedAt);
    }

    [Fact]
    public async Task PrepareNext_AddsNext_AndKeepsCurrent()
    {
        var svc = await WithCurrent();
        var before = await Load(svc);

        var keys = await svc.PrepareNextAsync(Id(before.Current));

        Assert.Equal(before.Current!.PublicKey, keys.Current!.PublicKey);
        Assert.NotNull(keys.Next!.PrivateKey);
        Assert.NotEqual(keys.Current.PublicKey, keys.Next.PublicKey);
        Assert.Equal(Now, keys.Next.CreatedAt);
        Assert.Equal(keys.Next.PublicKey, (await Load(svc)).Next!.PublicKey);
    }

    [Fact]
    public async Task DiscardNext_ClearsNext_AndKeepsCurrent()
    {
        var svc = await WithNext();
        var before = await Load(svc);

        var keys = await svc.DiscardNextAsync(Id(before.Current), Id(before.Next));

        Assert.Null(keys.Next);
        Assert.Equal(before.Current!.PublicKey, keys.Current!.PublicKey);
        Assert.Null((await Load(svc)).Next);
    }

    [Fact]
    public async Task ActivateNext_MovesCurrentToPrevious_NextToCurrent()
    {
        var svc = await WithNext();
        var before = await Load(svc);

        var keys = await svc.ActivateNextAsync(Id(before.Current), Id(before.Next));

        Assert.Equal(before.Next!.PublicKey, keys.Current!.PublicKey);
        Assert.Equal(before.Next.PrivateKey, keys.Current.PrivateKey);
        Assert.Equal(before.Current!.PublicKey, keys.PreviousPublicKey);
        Assert.Equal(before.Current.CreatedAt, keys.PreviousCreatedAt);
        Assert.Null(keys.Next);
        var onDisk = await Load(svc);
        Assert.Equal(before.Current.PublicKey, onDisk.PreviousPublicKey);
        Assert.Equal(before.Next.PublicKey, onDisk.Current!.PublicKey);
    }

    [Fact]
    public async Task RetirePrevious_ClearsPrevious_AndKeepsCurrent()
    {
        var svc = await WithPrevious();
        var before = await Load(svc);

        var keys = await svc.RetirePreviousAsync(Id(before.Current));

        Assert.Null(keys.PreviousPublicKey);
        Assert.Null(keys.PreviousCreatedAt);
        Assert.Equal(before.Current!.PublicKey, keys.Current!.PublicKey);
        var onDisk = await Load(svc);
        Assert.Null(onDisk.PreviousPublicKey);
        Assert.Equal(before.Current.PublicKey, onDisk.Current!.PublicKey);
    }

    [Fact]
    public async Task FullRotation_CanBeRepeated()
    {
        var svc = await WithPrevious();
        await svc.RetirePreviousAsync(Id((await Load(svc)).Current));
        await svc.PrepareNextAsync(Id((await Load(svc)).Current));
        var cur = await Load(svc);
        await svc.ActivateNextAsync(Id(cur.Current), Id(cur.Next));

        var keys = await Load(svc);
        Assert.NotNull(keys.Current);
        Assert.NotNull(keys.PreviousPublicKey);
        Assert.Null(keys.Next);
    }

    // ── precondition refusals ───────────────────────────────────────────────

    [Fact]
    public async Task Generate_Refuses_WhenACurrentKeyExists()
    {
        var svc = await WithCurrent();
        var before = await Load(svc);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GenerateAsync(Id(before.Current)));

        Assert.Equal(before.Current!.PrivateKey, (await Load(svc)).Current!.PrivateKey);   // not replaced
    }

    [Fact]
    public async Task PrepareNext_Refuses_WithoutCurrent_WhenNextExists_AndWhilePreviousIsSet()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().PrepareNextAsync(null));   // no current

        var withNext = await WithNext();
        var nextId = Id((await Load(withNext)).Current);
        await Assert.ThrowsAsync<InvalidOperationException>(() => withNext.PrepareNextAsync(nextId));   // next already there
    }

    [Fact]
    public async Task PrepareNext_Refuses_WhilePreviousIsSet()
    {
        var svc = await WithPrevious();
        var before = await Load(svc);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PrepareNextAsync(Id(before.Current)));

        Assert.Contains("previous", ex.Message);
        Assert.Null((await Load(svc)).Next);
    }

    [Fact]
    public async Task DiscardNext_Refuses_WithoutNext()
    {
        var svc = await WithCurrent();

        var id = Id((await Load(svc)).Current);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DiscardNextAsync(id, null));
    }

    [Fact]
    public async Task ActivateNext_Refuses_WithoutNext()
    {
        var svc = await WithCurrent();
        var before = await Load(svc);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ActivateNextAsync(Id(before.Current), null));

        Assert.Equal(before.Current!.PublicKey, (await Load(svc)).Current!.PublicKey);
        Assert.Null((await Load(svc)).PreviousPublicKey);
    }

    [Fact]
    public async Task ActivateNext_Refuses_WhilePreviousIsSet()
    {
        // A previous key still exists: activating again would silently drop it from the trusted set.
        var svc = await WithPrevious();
        var keys = await Load(svc);
        keys.Next = new SigningKeyPair { PrivateKey = "x", PublicKey = Convert.ToBase64String(new byte[] { 1, 2, 3 }), CreatedAt = Now };
        await new EnvironmentKeyStore(new LocalSecureStorage(SecurePath)).SaveAsync(keys);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ActivateNextAsync(Id(keys.Current), Id(keys.Next)));

        Assert.Contains("previous", ex.Message);
        var after = await Load(svc);
        Assert.Equal(keys.PreviousPublicKey, after.PreviousPublicKey);
        Assert.Equal(keys.Current!.PublicKey, after.Current!.PublicKey);
        Assert.NotNull(after.Next);
    }

    [Fact]
    public async Task RetirePrevious_Refuses_WithoutPrevious()
    {
        var svc = await WithCurrent();

        var id = Id((await Load(svc)).Current);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RetirePreviousAsync(id));
    }

    // ── unreadable file, stale page, authorization ──────────────────────────

    [Fact]
    public async Task Activate_WithAStaleNextKeyId_IsRefused_AndWritesNothing()
    {
        // The page showed Next A; meanwhile someone discarded it and prepared Next B.
        var svc = await WithNext();
        var shown = await Load(svc);
        await svc.DiscardNextAsync(Id(shown.Current), Id(shown.Next));
        await svc.PrepareNextAsync(Id(shown.Current));
        var before = File.ReadAllBytes(KeyFilePath);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ActivateNextAsync(Id(shown.Current), Id(shown.Next)));

        Assert.Contains("changed by someone else", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(KeyFilePath));
        var after = await Load(svc);
        Assert.Null(after.PreviousPublicKey);
        Assert.Equal(shown.Current!.PublicKey, after.Current!.PublicKey);
    }

    [Fact]
    public async Task Discard_WithAStaleNextKeyId_IsRefused_AndWritesNothing()
    {
        var svc = await WithNext();
        var shown = await Load(svc);
        await svc.DiscardNextAsync(Id(shown.Current), Id(shown.Next));
        await svc.PrepareNextAsync(Id(shown.Current));
        var before = File.ReadAllBytes(KeyFilePath);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DiscardNextAsync(Id(shown.Current), Id(shown.Next)));

        Assert.Contains("changed by someone else", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(KeyFilePath));
        Assert.NotNull((await Load(svc)).Next);
    }

    [Fact]
    public async Task UnreadableFile_RefusesEveryAction_AndWritesNothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFilePath)!);
        File.WriteAllText(KeyFilePath, "{ corrupt");
        var svc = Service();
        var actions = new Func<string?, Task<EnvironmentSigningKeys>>[]
        {
            svc.GenerateAsync, svc.PrepareNextAsync, c => svc.DiscardNextAsync(c, null), c => svc.ActivateNextAsync(c, null), svc.RetirePreviousAsync
        };

        foreach (var action in actions)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => action(null));
            Assert.Contains("not replaced", ex.Message);
        }

        Assert.Equal("{ corrupt", File.ReadAllText(KeyFilePath));
        Assert.Equal(new[] { KeyFilePath }, Directory.GetFiles(Path.GetDirectoryName(KeyFilePath)!));   // not even a temp file
    }

    [Fact]
    public async Task UnavailableSecurePath_RefusesTheAction()
    {
        var svc = Service(() => Task.FromResult(new SecureRootResult(null, "settings invalid")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GenerateAsync(null));

        Assert.Contains("settings invalid", ex.Message);
        Assert.Equal(KeyLoadOutcome.Unreadable, (await svc.LoadAsync()).Outcome);
    }

    [Fact]
    public async Task StaleExpectedKeyId_RefusesEveryAction_AndChangesNothing()
    {
        var svc = await WithNext();
        var before = File.ReadAllBytes(KeyFilePath);
        var actions = new Func<string?, Task<EnvironmentSigningKeys>>[]
        {
            svc.GenerateAsync, svc.PrepareNextAsync, c => svc.DiscardNextAsync(c, null), c => svc.ActivateNextAsync(c, null), svc.RetirePreviousAsync
        };

        foreach (var action in actions)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => action("0123456789abcdef"));
            Assert.Contains("changed by someone else", ex.Message);
        }

        Assert.Equal(before, File.ReadAllBytes(KeyFilePath));
    }

    [Fact]
    public async Task Generate_WithAnExpectedIdButNoKeyOnDisk_IsStale()
    {
        // The page showed a key, someone removed the file: do not silently generate a different one.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().GenerateAsync("0123456789abcdef"));

        Assert.Contains("changed by someone else", ex.Message);
        Assert.False(File.Exists(KeyFilePath));
    }

    [Fact]
    public async Task NonSystemAdmin_IsRefusedForEveryAction_AndNothingIsWrittenOrAudited()
    {
        _admin = false;
        var svc = Service();
        var actions = new Func<string?, Task<EnvironmentSigningKeys>>[]
        {
            svc.GenerateAsync, svc.PrepareNextAsync, c => svc.DiscardNextAsync(c, null), c => svc.ActivateNextAsync(c, null), svc.RetirePreviousAsync
        };

        foreach (var action in actions)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => action(null));

        Assert.False(File.Exists(KeyFilePath));
        Assert.Empty(_audit.Records);
    }

    // ── audit: fingerprints only ────────────────────────────────────────────

    [Fact]
    public async Task EveryAction_IsAudited_WithFingerprintsOnly()
    {
        var svc = Service();
        var seen = new List<EnvironmentSigningKeys>();

        await svc.GenerateAsync(null);
        var k1 = await Load(svc); seen.Add(k1);
        await svc.PrepareNextAsync(Id(k1.Current));
        var k2 = await Load(svc); seen.Add(k2);
        await svc.DiscardNextAsync(Id(k2.Current), Id(k2.Next));
        await svc.PrepareNextAsync(Id(k1.Current));
        var k3 = await Load(svc); seen.Add(k3);
        await svc.ActivateNextAsync(Id(k3.Current), Id(k3.Next));
        await svc.RetirePreviousAsync(Id(k3.Next));

        Assert.Equal(
            new[] { "KeyGenerated", "KeyPrepared", "KeyDiscarded", "KeyPrepared", "KeyRotated", "KeyRetired" },
            _audit.Records.Select(r => r.Event).ToArray());
        Assert.All(_audit.Records, r =>
        {
            Assert.Equal(AuditOutcome.Success, r.Outcome);
            Assert.Equal("environment", r.Target);
        });

        var all = string.Join("\n", _audit.Records.Select(r => $"{r.Event}|{r.Target}|{r.Detail}"));
        foreach (var k in seen.SelectMany(s => new[] { s.Current, s.Next }).Where(p => p != null))
        {
            Assert.DoesNotContain(k!.PrivateKey!, all);
            Assert.DoesNotContain(k.PublicKey!, all);
        }

        Assert.Equal($"fingerprint={Id(k1.Current)}", _audit.Records[0].Detail);
        Assert.Equal($"current={Id(k1.Current)}; next={Id(k2.Next)}", _audit.Records[1].Detail);
        Assert.Equal($"discarded={Id(k2.Next)}", _audit.Records[2].Detail);
        Assert.Equal($"current={Id(k3.Next)}; previous={Id(k1.Current)}", _audit.Records[4].Detail);
        Assert.StartsWith("retired=", _audit.Records[5].Detail);
    }

    [Fact]
    public async Task ARefusedAction_IsAuditedAsFailure_WithoutKeyMaterial()
    {
        var svc = await WithCurrent();
        var keys = await Load(svc);
        _audit.Records.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ActivateNextAsync(Id(keys.Current), null));

        var record = Assert.Single(_audit.Records);
        Assert.Equal("KeyRotated", record.Event);
        Assert.Equal(AuditOutcome.Failure, record.Outcome);
        Assert.DoesNotContain(keys.Current!.PrivateKey!, record.Detail);
        Assert.DoesNotContain(keys.Current.PublicKey!, record.Detail);
    }

    // ── GpoTarget ───────────────────────────────────────────────────────────

    [Fact]
    public void GpoTarget_NoKeys_IsEmpty()
    {
        Assert.Equal((null, null), EnvironmentKeyService.GpoTarget(new EnvironmentSigningKeys()));
    }

    [Fact]
    public void GpoTarget_CurrentOnly_IsCurrentAndRemovePrevious()
    {
        var k = new EnvironmentSigningKeys { Current = Pair("c") };

        Assert.Equal((k.Current.PublicKey, null), EnvironmentKeyService.GpoTarget(k));
    }

    [Fact]
    public void GpoTarget_CurrentAndNext_TrustsNextFirstAndCurrentAsPrevious()
    {
        var k = new EnvironmentSigningKeys { Current = Pair("c"), Next = Pair("n") };

        Assert.Equal((k.Next.PublicKey, k.Current.PublicKey), EnvironmentKeyService.GpoTarget(k));
    }

    [Fact]
    public void GpoTarget_CurrentAndPrevious_IsCurrentAndPrevious_AndNeedsNoChangeAfterActivation()
    {
        var before = new EnvironmentSigningKeys { Current = Pair("c"), Next = Pair("n") };
        var gpoBefore = EnvironmentKeyService.GpoTarget(before);

        var after = new EnvironmentSigningKeys { Current = before.Next, PreviousPublicKey = before.Current!.PublicKey };

        Assert.Equal((after.Current.PublicKey, "cHViLWM="), EnvironmentKeyService.GpoTarget(after));
        Assert.Equal(gpoBefore, EnvironmentKeyService.GpoTarget(after));   // the GPO already holds exactly these two keys
    }
}
