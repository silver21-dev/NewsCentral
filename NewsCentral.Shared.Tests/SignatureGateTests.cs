using NewsCentral.Security;
using NewsCentral.Models.IndexFile;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class SignatureGateTests
{
    // Full matrix: VerifyResult × requireSignedIndex {false, true}
    // Valid    → never rejects
    // Invalid  → always rejects
    // Unsigned → rejects iff requireSigned
    // Disabled → rejects iff requireSigned

    [Theory]
    [InlineData(VerifyResult.Valid,    false, false)]
    [InlineData(VerifyResult.Valid,    true,  false)]
    [InlineData(VerifyResult.Invalid,  false, true)]
    [InlineData(VerifyResult.Invalid,  true,  true)]
    [InlineData(VerifyResult.Unsigned, false, false)]
    [InlineData(VerifyResult.Unsigned, true,  true)]
    [InlineData(VerifyResult.Disabled, false, false)]
    [InlineData(VerifyResult.Disabled, true,  true)]
    public void ShouldReject_Matrix(VerifyResult result, bool requireSigned, bool expectReject)
    {
        var rejected = SignatureGate.ShouldReject(result, requireSigned, out var reason);

        Assert.Equal(expectReject, rejected);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void Valid_ReasonContainsValid()
    {
        SignatureGate.ShouldReject(VerifyResult.Valid, true, out var reason);
        Assert.Contains("valid", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_ReasonContainsInvalid()
    {
        SignatureGate.ShouldReject(VerifyResult.Invalid, false, out var reason);
        Assert.Contains("invalid", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsigned_AcceptedReason_WhenNotRequired()
    {
        SignatureGate.ShouldReject(VerifyResult.Unsigned, false, out var reason);
        Assert.Contains("accepted", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsigned_RequiredReason_WhenRequired()
    {
        SignatureGate.ShouldReject(VerifyResult.Unsigned, true, out var reason);
        Assert.Contains("required", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disabled_AcceptedReason_WhenNotRequired()
    {
        SignatureGate.ShouldReject(VerifyResult.Disabled, false, out var reason);
        Assert.Contains("accepted", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disabled_RequiredReason_WhenRequired()
    {
        SignatureGate.ShouldReject(VerifyResult.Disabled, true, out var reason);
        Assert.Contains("required", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Verify: KeyId selects the key, TeamFolderName is bound ─────────────────

    private static readonly EcdsaSignatureService Ecdsa = new();

    private static TeamIndexFile Signed(string team, string privateKey, string publicKey)
    {
        var index = new TeamIndexFile
        {
            TeamFolderName = team,
            TeamName       = "Team",
            GeneratedAt    = new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc),
            IndexHash      = "h1",
            KeyId          = KeyFingerprint.Of(publicKey)
        };
        index.Signature = Ecdsa.Sign(index, privateKey);
        return index;
    }

    [Fact]
    public void Verify_NoKeys_Disabled()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        Assert.Equal(VerifyResult.Disabled, SignatureGate.Verify(Signed("a", priv, pub), "a", Array.Empty<string?>()));
        Assert.Equal(VerifyResult.Disabled, SignatureGate.Verify(Signed("a", priv, pub), "a", new string?[] { null, " " }));
    }

    [Fact]
    public void Verify_NoSignature_Unsigned()
    {
        var (_, pub) = SigningKeyTool.GenerateKeyPair();
        var index = new TeamIndexFile { TeamFolderName = "a" };
        Assert.Equal(VerifyResult.Unsigned, SignatureGate.Verify(index, "a", new[] { pub }));
    }

    [Fact]
    public void Verify_SignedWithoutKeyId_KeyIdMissing()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = Signed("a", priv, pub);
        index.KeyId = null;
        Assert.Equal(VerifyResult.KeyIdMissing, SignatureGate.Verify(index, "a", new[] { pub }));
        index.KeyId = "";
        Assert.Equal(VerifyResult.KeyIdMissing, SignatureGate.Verify(index, "a", new[] { pub }));
    }

    [Fact]
    public void Verify_KeyIdOfUnconfiguredKey_KeyIdUnknown()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var (_, otherPub) = SigningKeyTool.GenerateKeyPair();
        Assert.Equal(VerifyResult.KeyIdUnknown, SignatureGate.Verify(Signed("a", priv, pub), "a", new[] { otherPub }));
    }

    [Fact]
    public void Verify_CurrentAndPreviousKey_BothValid()
    {
        var (privOld, pubOld) = SigningKeyTool.GenerateKeyPair();
        var (privNew, pubNew) = SigningKeyTool.GenerateKeyPair();
        var keys = new[] { pubNew, pubOld };

        Assert.Equal(VerifyResult.Valid, SignatureGate.Verify(Signed("a", privNew, pubNew), "a", keys));
        Assert.Equal(VerifyResult.Valid, SignatureGate.Verify(Signed("a", privOld, pubOld), "a", keys));
    }

    [Fact]
    public void Verify_TamperedPayload_WithCorrectKeyId_Invalid()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = Signed("a", priv, pub);
        index.IndexHash = "tampered";
        Assert.Equal(VerifyResult.Invalid, SignatureGate.Verify(index, "a", new[] { pub }));
    }

    [Fact]
    public void Verify_KeyIdIsSigned_SwappingItInvalidatesTheSignature()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var (_, otherPub) = SigningKeyTool.GenerateKeyPair();
        var index = Signed("a", priv, pub);
        index.KeyId = KeyFingerprint.Of(otherPub);
        // Both keys configured: the (wrong) KeyId selects otherPub, whose verification fails.
        Assert.Equal(VerifyResult.Invalid, SignatureGate.Verify(index, "a", new[] { pub, otherPub }));
    }

    [Fact]
    public void Verify_ValidSignatureForAnotherTeam_TeamMismatch()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        Assert.Equal(VerifyResult.TeamMismatch, SignatureGate.Verify(Signed("team-a", priv, pub), "team-b", new[] { pub }));
    }

    [Fact]
    public void Verify_FolderCaseDifferenceOnly_Valid()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        Assert.Equal(VerifyResult.Valid, SignatureGate.Verify(Signed("CZ-ITS", priv, pub), "cz-its", new[] { pub }));
    }

    [Theory]
    [InlineData(VerifyResult.KeyIdMissing, false)]
    [InlineData(VerifyResult.KeyIdMissing, true)]
    [InlineData(VerifyResult.KeyIdUnknown, false)]
    [InlineData(VerifyResult.KeyIdUnknown, true)]
    [InlineData(VerifyResult.TeamMismatch, false)]
    [InlineData(VerifyResult.TeamMismatch, true)]
    public void ShouldReject_NewResults_AlwaysReject(VerifyResult result, bool requireSigned)
    {
        Assert.True(SignatureGate.ShouldReject(result, requireSigned, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void ShouldReject_Reasons_NameTheKeyIdAndTheTeam()
    {
        var index = new TeamIndexFile { TeamFolderName = "team-a", KeyId = "0123456789abcdef" };

        SignatureGate.ShouldReject(VerifyResult.KeyIdMissing, false, out var missing, index);
        SignatureGate.ShouldReject(VerifyResult.KeyIdUnknown, false, out var unknown, index);
        SignatureGate.ShouldReject(VerifyResult.TeamMismatch, false, out var mismatch, index);

        Assert.Equal("signed without a key id", missing);
        Assert.Equal("signed with unknown key 0123456789abcdef", unknown);
        Assert.Equal("signed for team team-a", mismatch);
    }
}
