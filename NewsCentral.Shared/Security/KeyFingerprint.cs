using System.Security.Cryptography;

namespace NewsCentral.Security;

/// <summary>
/// Short identifier of a public key: the first 16 lowercase hex chars of SHA-256 over the decoded SPKI bytes.
/// Used as <c>TeamIndexFile.KeyId</c> (signed) to select the verification key, and in audit lines. Never derived
/// from private material.
/// </summary>
public static class KeyFingerprint
{
    /// <summary>Null for null, empty or invalid Base64 input. The input is trimmed first.</summary>
    public static string? Of(string? publicKeySpkiBase64)
    {
        var trimmed = publicKeySpkiBase64?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        byte[] spki;
        try { spki = Convert.FromBase64String(trimmed); }
        catch (FormatException) { return null; }

        return Convert.ToHexString(SHA256.HashData(spki))[..16].ToLowerInvariant();
    }
}
