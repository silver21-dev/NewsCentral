using System.Security.Cryptography;

namespace NewsCentral.Security;

/// <summary>
/// Compares image bytes to <c>ContentInfo.ImageHash</c> in the form NewsCentral's
/// <c>IndexGenerationService</c> emits: <c>sha256:{hex}</c> (hex case-insensitive here; the bare hex
/// form NewsViewer also tolerates is accepted too).
/// </summary>
public static class ImageHashVerifier
{
    private const string Prefix = "sha256:";

    /// <summary>False with a reason when the hash is missing or not a SHA-256 hex digest.</summary>
    public static bool TryParse(string? imageHash, out byte[] expected, out string reason)
    {
        expected = [];
        if (string.IsNullOrWhiteSpace(imageHash))
        {
            reason = "ImageHash is missing or empty";
            return false;
        }

        var hex = imageHash.Trim();
        if (hex.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) hex = hex[Prefix.Length..];

        if (hex.Length != SHA256.HashSizeInBytes * 2 || !hex.All(Uri.IsHexDigit))
        {
            reason = "ImageHash is not a SHA-256 hex digest";
            return false;
        }

        expected = Convert.FromHexString(hex);
        reason = "ok";
        return true;
    }

    public static bool Matches(byte[] data, byte[] expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(data), expected);

    /// <summary>Compares an already-computed SHA-256 digest to the expected one.</summary>
    public static bool DigestMatches(ReadOnlySpan<byte> actualDigest, byte[] expected) =>
        CryptographicOperations.FixedTimeEquals(actualDigest, expected);
}
