using NewsCentral.Models.IndexFile;

namespace NewsCentral.Security;

/// <summary>
/// Centralizes the accept/reject decision for ECDSA index verification results.
/// Default behavior (requireSignedIndex = false) mirrors the existing phased-rollout
/// pass-through: only Invalid is rejected. Set requireSignedIndex = true to go
/// fail-closed: Unsigned and Disabled are also rejected. A signed index with a missing or
/// unknown KeyId, or signed for another team, is rejected regardless of requireSignedIndex.
/// </summary>
public static class SignatureGate
{
    private static readonly EcdsaSignatureService Ecdsa = new();

    /// <summary>
    /// Verifies a team's <paramref name="index"/> against the configured <paramref name="publicKeys"/>
    /// (one key pair per environment; the previous key is kept for rotation). Order:
    /// <list type="number">
    ///   <item>No non-empty keys → <see cref="VerifyResult.Disabled"/>.</item>
    ///   <item><c>Signature</c> null → <see cref="VerifyResult.Unsigned"/>.</item>
    ///   <item><see cref="TeamIndexFile.KeyId"/> null/empty → <see cref="VerifyResult.KeyIdMissing"/>.</item>
    ///   <item>KeyId matches no configured key's <see cref="KeyFingerprint.Of"/> → <see cref="VerifyResult.KeyIdUnknown"/>.</item>
    ///   <item>Verify against the matching key ONLY → <see cref="VerifyResult.Invalid"/> or continue.</item>
    ///   <item><c>TeamFolderName</c> differs from <paramref name="expectedTeamFolder"/> (OrdinalIgnoreCase)
    ///         → <see cref="VerifyResult.TeamMismatch"/>, so an index signed for team A cannot be
    ///         accepted in team B's folder.</item>
    ///   <item>Otherwise <see cref="VerifyResult.Valid"/>.</item>
    /// </list>
    /// </summary>
    public static VerifyResult Verify(
        TeamIndexFile index, string expectedTeamFolder, IReadOnlyList<string?> publicKeys)
    {
        var keys = (publicKeys ?? Array.Empty<string?>()).Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        if (keys.Length == 0) return VerifyResult.Disabled;
        if (index.Signature is null) return VerifyResult.Unsigned;
        if (string.IsNullOrEmpty(index.KeyId)) return VerifyResult.KeyIdMissing;

        var matching = keys.Where(k => string.Equals(KeyFingerprint.Of(k), index.KeyId, StringComparison.Ordinal)).ToArray();
        if (matching.Length == 0) return VerifyResult.KeyIdUnknown;

        if (Ecdsa.Verify(index, matching) != VerifyResult.Valid) return VerifyResult.Invalid;

        return string.Equals(index.TeamFolderName, expectedTeamFolder, StringComparison.OrdinalIgnoreCase)
            ? VerifyResult.Valid
            : VerifyResult.TeamMismatch;
    }

    /// <summary>
    /// Returns true when the team's content must be rejected.
    /// <paramref name="reason"/> is always set to a human-readable explanation suitable
    /// for logging in both accept and reject paths. <paramref name="index"/> supplies the
    /// KeyId / TeamFolderName quoted in the KeyIdUnknown and TeamMismatch reasons.
    /// </summary>
    public static bool ShouldReject(
        VerifyResult result, bool requireSignedIndex, out string reason, TeamIndexFile? index = null)
    {
        switch (result)
        {
            case VerifyResult.Valid:
                reason = "signature valid";
                return false;

            case VerifyResult.Invalid:
                reason = "signature invalid";
                return true;

            case VerifyResult.KeyIdMissing:
                reason = "signed without a key id";
                return true;

            case VerifyResult.KeyIdUnknown:
                reason = $"signed with unknown key {index?.KeyId}";
                return true;

            case VerifyResult.TeamMismatch:
                reason = $"signed for team {index?.TeamFolderName}";
                return true;

            case VerifyResult.Unsigned:
                if (requireSignedIndex)
                {
                    reason = "unsigned, signing required";
                    return true;
                }
                reason = "unsigned (accepted)";
                return false;

            case VerifyResult.Disabled:
                if (requireSignedIndex)
                {
                    reason = "no public key, signing required";
                    return true;
                }
                reason = "no public key configured (accepted)";
                return false;

            default:
                reason = $"unknown verification result {result}";
                return true;
        }
    }
}
