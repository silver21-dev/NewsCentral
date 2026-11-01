using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewsCentral.Security;

/// <summary>
/// ECDSA P-256 / SHA-256 signing and verification for <see cref="ISignable"/> entities.
/// Stateless — key material is passed per call. Signature encoding: IEEE P1363
/// (fixed 64-byte r‖s), Base64. Thread-safe: ECDsa instances are created per call.
/// </summary>
public sealed class EcdsaSignatureService
{
    /// <summary>
    /// Signs <paramref name="entity"/> with the supplied PKCS#8 private key and returns
    /// the Base64 IEEE P1363 signature. The entity's Signature property is not modified.
    /// </summary>
    public string Sign<T>(T entity, string privateKeyBase64Pkcs8) where T : ISignable
    {
        var payloadBytes = Encoding.UTF8.GetBytes(Canonicalize(entity));

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64Pkcs8), out _);
        var sig = ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(sig);
    }

    /// <summary>
    /// Verifies the ECDSA signature stored in <paramref name="entity"/> against each
    /// supplied public key in order (supporting rotation windows).
    /// <list type="bullet">
    ///   <item>No non-empty keys supplied → <see cref="VerifyResult.Disabled"/></item>
    ///   <item><see cref="ISignable.Signature"/> is null → <see cref="VerifyResult.Unsigned"/></item>
    ///   <item>Signature verifies against any key → <see cref="VerifyResult.Valid"/></item>
    ///   <item>Signature present, all keys fail → <see cref="VerifyResult.Invalid"/></item>
    /// </list>
    /// An exception during a single key attempt is treated as that key failing — only
    /// <see cref="VerifyResult.Invalid"/> is returned after every supplied key is exhausted.
    /// </summary>
    public VerifyResult Verify<T>(T entity, params string?[] publicKeysSpkiBase64) where T : ISignable
    {
        var effectiveKeys = publicKeysSpkiBase64.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        if (effectiveKeys.Length == 0) return VerifyResult.Disabled;
        if (entity.Signature is null) return VerifyResult.Unsigned;

        byte[] sigBytes;
        try { sigBytes = Convert.FromBase64String(entity.Signature); }
        catch { return VerifyResult.Invalid; }

        var payloadBytes = Encoding.UTF8.GetBytes(Canonicalize(entity));

        foreach (var spki in effectiveKeys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki!), out _);
                if (ecdsa.VerifyData(payloadBytes, sigBytes, HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    return VerifyResult.Valid;
            }
            catch { /* that key attempt failed; try next */ }
        }

        return VerifyResult.Invalid;
    }

    // Temporarily clears Signature so it is not part of the signed payload. Mirrors
    // HmacService.Canonicalize exactly.
    private static string Canonicalize<T>(T entity) where T : ISignable
    {
        var savedSig = entity.Signature;
        entity.Signature = null;

        try { return JsonSerializer.Serialize(entity, CanonicalOptions); }
        finally { entity.Signature = savedSig; }
    }

    // Non-indented, camelCase, enum-as-string — must match across all components.
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}
