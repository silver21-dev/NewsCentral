using NewsCentral.Security;

namespace NewsService.Services;

/// <summary>
/// Reads published content from a local or UNC file share.
/// This is the primary mode for development and on-premises deployments.
/// </summary>
public sealed class LocalShareRepositoryReader : IRepositoryReader
{
    private readonly string _sharePath;
    private readonly ILogger<LocalShareRepositoryReader> _logger;

    public LocalShareRepositoryReader(string sharePath, ILogger<LocalShareRepositoryReader> logger)
    {
        _sharePath = sharePath;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_sharePath)) return false;
            try { return Directory.Exists(_sharePath); }
            catch { return false; }
        }
    }

    public string SyncSource => "Share";

    /// <summary>Full path to the repository uploads folder for telemetry hand-off.</summary>
    public string UploadsPath => Path.Combine(_sharePath, "uploads");

    private string Resolve(string relativePath) =>
        Path.Combine(_sharePath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public bool TryValidateImagePath(string teamFolder, string imagePath, out string reason) =>
        ImagePathValidator.TryResolveContained(
            Path.Combine(_sharePath, teamFolder), imagePath[(teamFolder.Length + 1)..], out _, out reason);

    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, ct);
    }

    public async Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, ct);
    }
}
