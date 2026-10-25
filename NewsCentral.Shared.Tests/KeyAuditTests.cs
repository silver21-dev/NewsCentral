using System.Text.Json;
using NewsCentral.Audit;
using NewsCentral.Models;
using NewsCentral.Security;

namespace NewsCentral.Shared.Tests;

/// <summary>Key events carry the public-key fingerprint only - never any key material.</summary>
public sealed class KeyAuditTests : SecureStoreTestBase
{
    private string AuditRoot => Path.Combine(Root, "audit");

    private TeamKeyService Service(Func<string, ISecureStorage>? factory = null)
    {
        var settings = new AuditSettings(true, AuditRoot, false, "env");
        var audit = new AuditLog(() => Task.FromResult(settings), new AuditContext { AppUser = "admin" }, "ws01", "jdoe");
        return new TeamKeyService(() => DataPath, () => Task.FromResult(new SecureRootResult(SecurePath, null)), factory, audit);
    }

    private List<JsonElement> Events() =>
        Directory.Exists(AuditRoot)
            ? Directory.GetFiles(AuditRoot, "*.jsonl", SearchOption.AllDirectories)
                .SelectMany(File.ReadAllLines).Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList()
            : new();

    private string RawAudit() =>
        string.Join("\n", Directory.GetFiles(AuditRoot, "*.jsonl", SearchOption.AllDirectories).SelectMany(File.ReadAllLines));

    [Fact]
    public async Task FirstKey_IsKeyGenerated_ReplacingItIsKeyRotated_FingerprintOnly()
    {
        var svc = Service();

        await svc.SaveAsync("cz-its", Keys("one"));
        await svc.SaveAsync("cz-its", Keys("two"));

        var events = Events();
        Assert.Equal(new[] { "KeyGenerated", "KeyRotated" }, events.Select(e => e.GetProperty("event").GetString()).ToArray());
        Assert.Equal("cz-its", events[0].GetProperty("target").GetString());
        Assert.Equal($"fingerprint={TeamKeyStore.Fingerprint(Keys("one"))}", events[0].GetProperty("detail").GetString());

        var raw = RawAudit();
        Assert.DoesNotContain("priv-one", raw);
        Assert.DoesNotContain("priv-two", raw);
        Assert.DoesNotContain("pub-one", raw);   // the fingerprint, not even the public key
    }

    [Fact]
    public async Task Migration_IsRecordedAsKeyMigrated_WithFingerprint()
    {
        WriteLegacy("cz-its", Keys("a"));

        await Service().LoadAsync("cz-its");

        var e = Assert.Single(Events());
        Assert.Equal("KeyMigrated", e.GetProperty("event").GetString());
        Assert.Equal("Success", e.GetProperty("outcome").GetString());
        Assert.Equal($"fingerprint={TeamKeyStore.Fingerprint(Keys("a"))}", e.GetProperty("detail").GetString());
        Assert.DoesNotContain("priv-a", RawAudit());
    }

    [Fact]
    public async Task FailedMigration_IsRecordedAsFailure()
    {
        WriteLegacy("cz-its", Keys("a"));
        var svc = Service(root => new FaultyStorage(new LocalSecureStorage(root)) { WriteFault = new UnauthorizedAccessException() });

        await svc.LoadAsync("cz-its");

        var e = Assert.Single(Events());
        Assert.Equal("KeyMigrated", e.GetProperty("event").GetString());
        Assert.Equal("Failure", e.GetProperty("outcome").GetString());
        Assert.DoesNotContain("priv-a", RawAudit());
    }

    [Fact]
    public async Task RefusedSave_OverAnUnreadableKey_IsRecordedAsFailure()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(NewPath("cz-its"))!);
        File.WriteAllText(NewPath("cz-its"), "{ corrupt");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().SaveAsync("cz-its", Keys("n")));

        var e = Assert.Single(Events());
        Assert.Equal("Failure", e.GetProperty("outcome").GetString());
    }
}
