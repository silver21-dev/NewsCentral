namespace NewsCentral.Security;

/// <summary>
/// Storage rooted at the environment's SecurePath. Deliberately separate from the DataPath
/// <c>IStorageService</c>: the secure area has a different access list from the shared authoring data,
/// and nothing stored here may ever be routed through a path every author can read.
/// Paths are relative to <see cref="Root"/>, forward-slash separated.
/// </summary>
public interface ISecureStorage
{
    string Root { get; }

    /// <summary>The file's bytes, or null when the file (or its folder) does not exist. Any other failure
    /// (access denied, IO error) throws — callers must not treat it as "absent".</summary>
    Task<byte[]?> ReadBytesAsync(string relativePath);

    Task WriteBytesAsync(string relativePath, byte[] content);

    /// <summary>Deletes the file; a no-op when it is already gone.</summary>
    Task DeleteAsync(string relativePath);
}

/// <summary>
/// <see cref="ISecureStorage"/> on the local file system or a UNC share. Reads open the file directly instead of
/// testing <c>File.Exists</c> first: <c>File.Exists</c> answers false when access is denied, which would turn an
/// unreadable key into "no key".
/// </summary>
public sealed class LocalSecureStorage : ISecureStorage
{
    public LocalSecureStorage(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("SecurePath must not be empty.", nameof(root));
        Root = root;
    }

    public string Root { get; }

    private string Resolve(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public async Task<byte[]?> ReadBytesAsync(string relativePath)
    {
        try
        {
            return await File.ReadAllBytesAsync(Resolve(relativePath));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task WriteBytesAsync(string relativePath, byte[] content)
    {
        var full = Resolve(relativePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Write beside the target and move over it, so a reader never sees a half-written key.
        var temp = full + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, content);
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    public Task DeleteAsync(string relativePath)
    {
        File.Delete(Resolve(relativePath));
        return Task.CompletedTask;
    }
}
