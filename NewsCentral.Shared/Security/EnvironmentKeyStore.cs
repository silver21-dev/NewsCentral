using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Models;

namespace NewsCentral.Security;

public enum KeyLoadOutcome
{
    /// <summary>No environment-signing.json exists. Signing keeps today's behaviour (unsigned).</summary>
    NotConfigured,

    /// <summary>The file was read and parsed.</summary>
    Loaded,

    /// <summary>The file may exist but could not be read or parsed. Never the same as "no key".</summary>
    Unreadable
}

public sealed record SigningKeyLoadResult(KeyLoadOutcome Outcome, EnvironmentSigningKeys? Keys, string? Reason)
{
    public static SigningKeyLoadResult NotConfigured() => new(KeyLoadOutcome.NotConfigured, null, null);
    public static SigningKeyLoadResult Loaded(EnvironmentSigningKeys keys) => new(KeyLoadOutcome.Loaded, keys, null);
    public static SigningKeyLoadResult Unreadable(string reason) => new(KeyLoadOutcome.Unreadable, null, reason);
}

/// <summary>
/// The environment signing keys at <c>{SecurePath}\keys\environment-signing.json</c>. Pure over
/// <see cref="ISecureStorage"/>.
/// </summary>
public sealed class EnvironmentKeyStore
{
    public const string FileName = "environment-signing.json";
    public const string RelativePath = "keys/" + FileName;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISecureStorage _storage;

    public EnvironmentKeyStore(ISecureStorage storage) => _storage = storage;

    public async Task<SigningKeyLoadResult> LoadAsync()
    {
        byte[]? bytes;
        try
        {
            bytes = await _storage.ReadBytesAsync(RelativePath);
        }
        catch (Exception ex)
        {
            return SigningKeyLoadResult.Unreadable(Describe(ex));
        }

        return bytes is null ? SigningKeyLoadResult.NotConfigured() : Parse(bytes);
    }

    /// <summary>Parses file bytes; any parse failure is Unreadable, never NotConfigured.</summary>
    public static SigningKeyLoadResult Parse(byte[] bytes)
    {
        try
        {
            var keys = JsonSerializer.Deserialize<EnvironmentSigningKeys>(bytes, Json);
            return keys is null
                ? SigningKeyLoadResult.Unreadable($"{FileName} is empty")
                : SigningKeyLoadResult.Loaded(keys);
        }
        catch (JsonException ex)
        {
            return SigningKeyLoadResult.Unreadable($"{FileName} could not be parsed ({ex.Message})");
        }
    }

    /// <summary>Throws on any storage failure; the caller reports it.</summary>
    public Task SaveAsync(EnvironmentSigningKeys keys) => _storage.WriteBytesAsync(RelativePath, Serialize(keys));

    public static byte[] Serialize(EnvironmentSigningKeys keys) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(keys, Json));

    /// <summary>Fingerprint (the KeyId) of the current key: derived from the PUBLIC key only. Null when there is none.</summary>
    public static string? Fingerprint(EnvironmentSigningKeys? keys) => KeyFingerprint.Of(keys?.Current?.PublicKey);

    internal static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        IOException io => $"IO error ({io.Message})",
        _ => $"{ex.GetType().Name}: {ex.Message}"
    };
}
