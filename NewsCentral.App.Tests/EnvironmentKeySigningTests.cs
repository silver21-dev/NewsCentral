using NewsCentral.Models;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using NewsCentral.Services;

namespace NewsCentral.App.Tests;

/// <summary>Index signing uses the environment's Current key only, and "Regenerate all" stops on an unreadable key.</summary>
public sealed class EnvironmentKeySigningTests
{
    private static SigningKeyPair NewPair()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        return new SigningKeyPair { PrivateKey = priv, PublicKey = pub, CreatedAt = DateTime.UtcNow };
    }

    private static TeamIndexFile NewIndex(string team) => new()
    {
        TeamFolderName = team,
        GeneratedAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void Index_IsSignedWithCurrent_AndKeyIdIsTheFingerprintOfCurrent()
    {
        var current = NewPair();
        var next = NewPair();
        var decision = SigningKeyGate.Decide(SigningKeyLoadResult.Loaded(new EnvironmentSigningKeys { Current = current, Next = next }));
        var index = NewIndex("cz-its");

        IndexSigner.Sign(index, "cz-its", decision.Key!, new EcdsaSignatureService());

        Assert.Equal(KeyFingerprint.Of(current.PublicKey), index.KeyId);
        Assert.NotEqual(KeyFingerprint.Of(next.PublicKey), index.KeyId);
        Assert.Equal(VerifyResult.Valid, SignatureGate.Verify(index, "cz-its", new[] { current.PublicKey }));
        // Clients that only trust Next (not yet the case during a rotation, but the rule) cannot verify it: Next never signed.
        Assert.Equal(VerifyResult.KeyIdUnknown, SignatureGate.Verify(index, "cz-its", new[] { next.PublicKey }));
        // During preparation the GPO holds Next as PublicKey and Current as PublicKeyPrevious: both are fine.
        Assert.Equal(VerifyResult.Valid, SignatureGate.Verify(index, "cz-its", new[] { next.PublicKey, current.PublicKey }));
    }

    [Fact]
    public void AfterActivation_TheFormerNextSigns_AndTheGpoHeldThePairAlready()
    {
        var oldCurrent = NewPair();
        var next = NewPair();
        var gpo = EnvironmentKeyService.GpoTarget(new EnvironmentSigningKeys { Current = oldCurrent, Next = next });

        var activated = new EnvironmentSigningKeys { Current = next, PreviousPublicKey = oldCurrent.PublicKey };
        var index = NewIndex("cz-its");
        IndexSigner.Sign(index, "cz-its", SigningKeyGate.Decide(SigningKeyLoadResult.Loaded(activated)).Key!, new EcdsaSignatureService());

        Assert.Equal(KeyFingerprint.Of(next.PublicKey), index.KeyId);
        Assert.Equal(VerifyResult.Valid, SignatureGate.Verify(index, "cz-its", new[] { gpo.PublicKey, gpo.PublicKeyPrevious }));
    }

    // ── Regenerate all teams ────────────────────────────────────────────────

    private static IndexSaveResult Ok() => new() { DistributionSucceeded = true };

    [Fact]
    public async Task RegenerateAll_StopsAtTheFirstUnreadableKey_AndListsTheTeamsNotProcessed()
    {
        var tried = new List<string>();
        var teams = new[] { "a-team", "b-team", "c-team", "d-team" };

        var result = await IndexRegeneration.RegenerateAllAsync(teams, team =>
        {
            tried.Add(team);
            return team == "b-team" ? throw new SigningKeyUnreadableException("access denied") : Task.FromResult(Ok());
        });

        Assert.Equal(new[] { "a-team", "b-team" }, tried);   // c-team and d-team were not even tried
        Assert.Equal(1, result.Regenerated);
        Assert.Equal("access denied", result.KeyUnreadableReason);
        Assert.Equal(new[] { "b-team", "c-team", "d-team" }, result.NotProcessed);
        Assert.Empty(result.Failures);   // reported once through KeyUnreadableReason, not per team
    }

    [Fact]
    public async Task RegenerateAll_OtherFailuresDoNotStopTheRun()
    {
        var result = await IndexRegeneration.RegenerateAllAsync(new[] { "a-team", "b-team", "c-team" }, team =>
            team == "b-team" ? throw new IOException("share offline") : Task.FromResult(Ok()));

        Assert.Equal(2, result.Regenerated);
        Assert.Single(result.Failures);
        Assert.Contains("b-team", result.Failures[0]);
        Assert.Null(result.KeyUnreadableReason);
        Assert.Empty(result.NotProcessed);
    }

    [Fact]
    public async Task RegenerateAll_ReportsDistributionFailures()
    {
        var result = await IndexRegeneration.RegenerateAllAsync(new[] { "a-team" },
            _ => Task.FromResult(new IndexSaveResult { DistributionSucceeded = false, DistributionError = "blob offline" }));

        Assert.Equal(1, result.Regenerated);
        Assert.Equal("a-team: blob offline", Assert.Single(result.DistributionFailures));
    }
}
