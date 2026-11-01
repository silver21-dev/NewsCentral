using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewsCentral.Security;

public enum VerifyResult
{
    /// <summary>No key configured — verification skipped.</summary>
    Disabled,
    /// <summary>Key configured but content carries no signature — suspect but not rejected.</summary>
    Unsigned,
    /// <summary>Signature present and matches.</summary>
    Valid,
    /// <summary>Signature present but does not match — content may have been tampered with.</summary>
    Invalid,
    /// <summary>Signed index carries no KeyId, so no verification key can be selected.</summary>
    KeyIdMissing,
    /// <summary>The index KeyId matches none of the configured public keys.</summary>
    KeyIdUnknown,
    /// <summary>Signature is valid but the index was signed for a different team folder.</summary>
    TeamMismatch
}

/// <summary>
/// HMAC-SHA256 signing and verification for cross-component data transfer objects.
/// Thread-safe: HMACSHA256 instances are created per-call.
/// </summary>
public sealed class HmacService
{
    private readonly byte[]? _key;

    public bool IsEnabled => _key is { Length: > 0 };

    public HmacService(HmacOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SecretKey))
        {
            try { _key = Convert.FromBase64String(options.SecretKey); }
            catch { /* invalid base64 — HMAC disabled */ }
        }
    }

    /// <summary>
    /// Signs <paramref name="entity"/> and returns the base64 signature, or null when disabled.
    /// The entity's Signature property is not modified.
    /// </summary>
    public string? Sign<T>(T entity) where T : ISignable
    {
        if (!IsEnabled) return null;
        return Compute(Canonicalize(entity));
    }

    /// <summary>
    /// Verifies the HMAC signature stored in <paramref name="entity"/>.
    /// Returns Disabled when no key is configured, Unsigned when signature is null,
    /// Valid or Invalid otherwise.
    /// </summary>
    public VerifyResult Verify<T>(T entity) where T : ISignable
    {
        if (!IsEnabled) return VerifyResult.Disabled;
        if (entity.Signature is null) return VerifyResult.Unsigned;

        try
        {
            var expected = Convert.FromBase64String(Compute(Canonicalize(entity)));
            var actual   = Convert.FromBase64String(entity.Signature);
            return CryptographicOperations.FixedTimeEquals(expected, actual)
                ? VerifyResult.Valid
                : VerifyResult.Invalid;
        }
        catch { return VerifyResult.Invalid; }
    }

    // Temporarily clears Signature so it is excluded from the signed payload.
    private static string Canonicalize<T>(T entity) where T : ISignable
    {
        var saved = entity.Signature;
        entity.Signature = null;
        try { return JsonSerializer.Serialize(entity, CanonicalOptions); }
        finally { entity.Signature = saved; }
    }

    private string Compute(string canonical)
    {
        using var hmac = new HMACSHA256(_key!);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    // Non-indented, camelCase, enum-as-string — must match across all components.
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}
