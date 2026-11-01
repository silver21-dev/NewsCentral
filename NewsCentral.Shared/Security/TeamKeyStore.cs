using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Models;

namespace NewsCentral.Security;

public enum KeyLoadOutcome
{
    /// <summary>No team-signing.json exists for the team. Signing keeps today's behaviour (unsigned).</summary>
    NotConfigured,

    /// <summary>The file was read and parsed.</summary>
    Loaded,

    /// <summary>The file may exist but could not be read or parsed. Never the same as "no key".</summary>
    Unreadable
}

/// <param name="FromLegacyLocation">True when the keys were read from the old DataPath location because migration failed.</param>
public sealed record TeamKeyLoadResult(
    KeyLoadOutcome Outcome, TeamSigningKeys? Keys, string? Reason, bool FromLegacyLocation = false)
{
    public static TeamKeyLoadResult NotConfigured() => new(KeyLoadOutcome.NotConfigured, null, null);
    public static TeamKeyLoadResult Loaded(TeamSigningKeys keys, bool legacy = false) => new(KeyLoadOutcome.Loaded, keys, null, legacy);
    public static TeamKeyLoadResult Unreadable(string reason) => new(KeyLoadOutcome.Unreadable, null, reason);
}

/// <summary>
/// Per-team signing keys at <c>{SecurePath}\keys\{teamFolderName}\team-signing.json</c> (same JSON format as the
/// old DataPath file). Pure over <see cref="ISecureStorage"/>; no DataPath access.
/// </summary>
public sealed class TeamKeyStore
{
    public const string FileName = "team-signing.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISecureStorage _storage;

    public TeamKeyStore(ISecureStorage storage) => _storage = storage;

    public static string RelativePath(string teamFolderName) => $"keys/{teamFolderName}/{FileName}";

    /// <summary>Team folder names are generated slugs; anything that could climb out of the keys folder is refused.</summary>
    public static bool IsSafeTeamFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name != "." && name != ".."
        && name.IndexOfAny(['/', '\\', ':']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public async Task<TeamKeyLoadResult> LoadAsync(string teamFolderName)
    {
        if (!IsSafeTeamFolderName(teamFolderName))
            return TeamKeyLoadResult.Unreadable("invalid team folder name");

        byte[]? bytes;
        try
        {
            bytes = await _storage.ReadBytesAsync(RelativePath(teamFolderName));
        }
        catch (Exception ex)
        {
            return TeamKeyLoadResult.Unreadable(Describe(ex));
        }

        return bytes is null ? TeamKeyLoadResult.NotConfigured() : Parse(bytes);
    }

    /// <summary>Parses file bytes; any parse failure is Unreadable, never NotConfigured.</summary>
    public static TeamKeyLoadResult Parse(byte[] bytes, bool legacy = false)
    {
        try
        {
            var keys = JsonSerializer.Deserialize<TeamSigningKeys>(bytes, Json);
            return keys is null
                ? TeamKeyLoadResult.Unreadable("team-signing.json is empty")
                : TeamKeyLoadResult.Loaded(keys, legacy);
        }
        catch (JsonException ex)
        {
            return TeamKeyLoadResult.Unreadable($"team-signing.json could not be parsed ({ex.Message})");
        }
    }

    /// <summary>Throws on any storage failure; the caller reports it. Refuses an invalid team folder name.</summary>
    public async Task SaveAsync(string teamFolderName, TeamSigningKeys keys)
    {
        if (!IsSafeTeamFolderName(teamFolderName))
            throw new ArgumentException("Invalid team folder name.", nameof(teamFolderName));

        await _storage.WriteBytesAsync(RelativePath(teamFolderName), Serialize(keys));
    }

    public static byte[] Serialize(TeamSigningKeys keys) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(keys, Json));

    /// <summary>
    /// A short identifier for the key pair for audit lines: the first 16 hex chars of SHA-256 over the PUBLIC key.
    /// Never derived from or containing private material. Null when there is no public key.
    /// </summary>
    public static string? Fingerprint(TeamSigningKeys? keys) => KeyFingerprint.Of(keys?.PublicKey);

    internal static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        IOException io => $"IO error ({io.Message})",
        _ => $"{ex.GetType().Name}: {ex.Message}"
    };
}
