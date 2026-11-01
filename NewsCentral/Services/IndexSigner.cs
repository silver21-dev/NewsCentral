using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;

namespace NewsCentral.Services;

/// <summary>
/// Signs a team index with the environment's CURRENT key and proves that clients will accept it. Pure, so the rule
/// "sign with Current, KeyId = fingerprint of Current's public key" is tested without the authoring app.
/// </summary>
public static class IndexSigner
{
    public static void Sign(TeamIndexFile index, string teamFolderName, SigningKeyPair current, EcdsaSignatureService ecdsa)
    {
        // KeyId is part of the signed payload, so it must be set before signing.
        index.KeyId = KeyFingerprint.Of(current.PublicKey);
        index.Signature = ecdsa.Sign(index, current.PrivateKey!);

        // Simulate client deserialization before any distribution to catch
        // canonical/persisted-form drift (e.g., SmartDateTimeConverter changes).
        var written = JsonSerializer.Serialize(index, JsonConfiguration.GetIndexJsonOptions());
        var clientReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var reloaded = JsonSerializer.Deserialize<TeamIndexFile>(written, clientReadOpts)!;
        if (SignatureGate.Verify(reloaded, teamFolderName, new[] { current.PublicKey }) != VerifyResult.Valid)
            throw new InvalidOperationException(
                $"Self-verification failed for {teamFolderName}: the published index would be " +
                $"rejected by clients (canonical/persisted-form mismatch). Publish aborted.");
    }
}
