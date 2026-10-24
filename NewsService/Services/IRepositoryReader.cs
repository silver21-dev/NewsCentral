namespace NewsService.Services;

/// <summary>
/// Reads content from the network repository (file share or Azure Blob).
/// NewsService only reads from the repository — it never writes to it
/// except for uploading telemetry files in share mode.
/// </summary>
public interface IRepositoryReader
{
    /// <summary>True when the repository is reachable.</summary>
    bool IsAvailable { get; }

    /// <summary>"Share" or "Azure" — written to status.json as SyncSource.</summary>
    string SyncSource { get; }

    Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default);
    Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Source-specific containment check for an image path that already passed
    /// <c>ImagePathValidator.Validate</c>: the file-share reader requires the resolved path to stay inside
    /// <c>{share}\{team}\</c>, the blob reader that the blob name starts with <c>{team}/</c>. Called before
    /// any read; the default accepts (readers with no path semantics).
    /// </summary>
    bool TryValidateImagePath(string teamFolder, string imagePath, out string reason)
    {
        reason = "ok";
        return true;
    }
}
