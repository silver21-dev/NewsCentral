using System.Security.Cryptography;
using System.Text.Json;
using NewsCentral.Security;

namespace NewsService.Services;

/// <summary>
/// Manages all reads and writes to the local cache at %programdata%\NewsCentral\.
/// Every file access goes through this class to keep path resolution centralised.
/// </summary>
public sealed class CacheManager
{
    private const string HashSuffix = ".hash";

    private readonly string _root;
    private readonly JsonSerializerOptions _json;

    public CacheManager(string cacheRoot, JsonSerializerOptions jsonOptions)
    {
        _root = cacheRoot;
        _json = jsonOptions;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    // ── Path resolution ──────────────────────────────────────────────────────

    public string Resolve(string relativePath) =>
        Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void EnsureParent(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    // ── Text / JSON ──────────────────────────────────────────────────────────

    public async Task<string?> ReadTextAsync(string relativePath)
    {
        var path = Resolve(relativePath);
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
    }

    public async Task WriteTextAsync(string relativePath, string content)
    {
        var path = Resolve(relativePath);
        EnsureParent(path);
        await File.WriteAllTextAsync(path, content);
    }

    /// <summary>
    /// Writes via a temp file in the same directory then an overwriting move, so a reader (or a crash)
    /// never sees a half-written file. The temp file is removed if the write fails.
    /// </summary>
    public async Task WriteTextAtomicAsync(string relativePath, string content)
    {
        var path = Resolve(relativePath);
        EnsureParent(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    public async Task<T?> ReadJsonAsync<T>(string relativePath) where T : class
    {
        var json = await ReadTextAsync(relativePath);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, _json);
    }

    public async Task WriteJsonAsync<T>(string relativePath, T value)
    {
        await WriteTextAsync(relativePath, JsonSerializer.Serialize(value, _json));
    }

    // ── Binary (images) ──────────────────────────────────────────────────────

    /// <summary>
    /// Writes an image to the cache and stores its SHA-256 hash in a sidecar
    /// file ({path}.hash) so subsequent cycles can detect unchanged content
    /// without re-downloading or re-hashing.
    /// </summary>
    public async Task WriteBytesAsync(string relativePath, byte[] data)
    {
        var path = Resolve(relativePath);
        EnsureParent(path);
        await File.WriteAllBytesAsync(path, data);

        var hex = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        await File.WriteAllTextAsync(path + HashSuffix, $"sha256:{hex}");
    }

    /// <summary>Returns the stored hash (format "sha256:…") or null if absent.</summary>
    public string? ReadStoredHash(string relativePath)
    {
        var hashPath = Resolve(relativePath) + HashSuffix;
        return File.Exists(hashPath) ? File.ReadAllText(hashPath).Trim() : null;
    }

    public bool FileExists(string relativePath) => File.Exists(Resolve(relativePath));

    /// <summary>
    /// Containment check for a validated image path: the resolved cache path must lie inside
    /// <c>{cacheRoot}\{team}\</c>.
    /// </summary>
    public bool TryValidateImagePath(string teamFolder, string imagePath, out string reason) =>
        ImagePathValidator.TryResolveContained(
            Resolve(teamFolder), imagePath[(teamFolder.Length + 1)..], out _, out reason);

    /// <summary>SHA-256 of the cached file, or null when it is absent or unreadable.</summary>
    public async Task<byte[]?> ComputeHashAsync(string relativePath)
    {
        try
        {
            await using var stream = File.OpenRead(Resolve(relativePath));
            return await SHA256.HashDataAsync(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ── Uploads folder ───────────────────────────────────────────────────────

    /// <summary>Returns full paths to all session-*.json files in the uploads folder.</summary>
    public IEnumerable<string> ListUploadFiles()
    {
        var folder = Resolve("uploads");
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "session-*.json")
            : [];
    }

    public void DeleteFile(string fullPath)
    {
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }
}
