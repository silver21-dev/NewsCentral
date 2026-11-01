using System.Text.Json;
using NewsCentral.Audit;
using NewsCentral.Models;
using NewsCentral.Security;

namespace NewsCentral.Shared.Tests;

/// <summary>Key events written to the real audit log carry public-key fingerprints only - never any key material.</summary>
public sealed class KeyAuditTests : SecureStoreTestBase
{
    private string AuditRoot => Path.Combine(Root, "audit");

    private EnvironmentKeyService Service(Func<string, ISecureStorage>? factory = null)
    {
        var settings = new AuditSettings(true, AuditRoot, false, "env");
        var audit = new AuditLog(() => Task.FromResult(settings), new AuditContext { AppUser = "admin" }, "ws01", "jdoe");
        return new EnvironmentKeyService(
            () => Task.FromResult(new SecureRootResult(SecurePath, null)), () => true, factory, audit);
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
    public async Task Lifecycle_IsRecordedInTheAuditFile_FingerprintOnly()
    {
        var svc = Service();
        var generated = await svc.GenerateAsync(null);
        var id = KeyFingerprint.Of(generated.Current!.PublicKey);
        var prepared = await svc.PrepareNextAsync(id);
        var rotated = await svc.ActivateNextAsync(id, KeyFingerprint.Of(prepared.Next!.PublicKey));
        await svc.RetirePreviousAsync(KeyFingerprint.Of(rotated.Current!.PublicKey));

        var events = Events();
        Assert.Equal(new[] { "KeyGenerated", "KeyPrepared", "KeyRotated", "KeyRetired" },
            events.Select(e => e.GetProperty("event").GetString()).ToArray());
        Assert.All(events, e => Assert.Equal("Success", e.GetProperty("outcome").GetString()));
        Assert.Equal($"fingerprint={id}", events[0].GetProperty("detail").GetString());

        var raw = RawAudit();
        foreach (var key in new[] { generated.Current, prepared.Next })
        {
            Assert.DoesNotContain(key!.PrivateKey!, raw);
            Assert.DoesNotContain(key.PublicKey!, raw);   // the fingerprint, not even the public key
        }
    }

    [Fact]
    public async Task RefusedAction_OverAnUnreadableKey_IsRecordedAsFailure()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFilePath)!);
        File.WriteAllText(KeyFilePath, "{ corrupt");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().GenerateAsync(null));

        var e = Assert.Single(Events());
        Assert.Equal("KeyGenerated", e.GetProperty("event").GetString());
        Assert.Equal("Failure", e.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task FailedWrite_IsRecordedAsFailure_AndLeavesNoKeyFile()
    {
        var svc = Service(root => new FaultyStorage(new LocalSecureStorage(root)) { WriteFault = new UnauthorizedAccessException() });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GenerateAsync(null));

        var e = Assert.Single(Events());
        Assert.Equal("Failure", e.GetProperty("outcome").GetString());
        Assert.False(File.Exists(KeyFilePath));
    }
}
