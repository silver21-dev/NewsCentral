using System.Security.Cryptography;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Publishes a verified display image to a protected, non-user-writable folder
/// (<see cref="DeliverySection.PublishedImagePath"/>) before it is referenced by
/// PersonalizationCSP — shared by both surfaces NewsService manages (lock screen, prefix
/// "lockscreen"; desktop wallpaper, prefix "wallpaper"), each in its own content-derived
/// namespace within the same folder. This closes two gaps in the cache-based apply it replaces:
///   1. The bytes on disk were never re-verified before the path was handed to the OS — the
///      image hash was checked once at download time against the signed index, never again at
///      apply time.
///   2. %ProgramData%\NewsCentral\ inherits C:\ProgramData's ACL, under which a standard user
///      can pre-create a team's images\generated\ folder and, as CREATOR OWNER, retain
///      delete-child rights over everything later written into it. Both surfaces are
///      machine-wide, pre-authentication-reachable (lock screen) or system-enforced (wallpaper)
///      surfaces, so that exposure matters for either.
/// File names are content-derived (<c>{prefix}-{hash16}{ext}</c>), so existence of the target
/// implies correctness and the CSP path changes exactly when the image content changes.
/// </summary>
public interface IImagePublisher
{
    /// <summary>
    /// Verifies <paramref name="sourcePath"/>'s SHA-256 against <paramref name="expectedHash"/>
    /// (accepts either the bare hex form or the "sha256:" prefixed form used in
    /// <c>ContentInfo.ImageHash</c>, case-insensitively), then copies it into the publish root
    /// under a content-derived name. Returns the absolute published path, or <c>null</c> on any
    /// failure — a missing source, a hash mismatch, or an I/O error. Callers must treat
    /// <c>null</c> as "no usable image," never as success: publishing unverified bytes is exactly
    /// what this method exists to prevent.
    /// </summary>
    string? Publish(string sourcePath, string expectedHash, string prefix);

    /// <summary>
    /// Deletes every published file matching <c>{prefix}-*</c> in the publish root except the one
    /// named by <paramref name="keepFileName"/> (or every matching one, if <c>null</c> — a legitimate
    /// steady state once a surface has nothing active to publish). Scoped strictly to
    /// <paramref name="prefix"/>, so sweeping one surface (e.g. "lockscreen") never touches another
    /// surface's files (e.g. "wallpaper-*"). Call this only at the START of a cycle, before anything
    /// is published that cycle — never in the same cycle a file was just written, since Windows may
    /// still hold it open from the apply that just ran.
    /// </summary>
    void SweepExcept(string? keepFileName, string prefix);
}

/// <inheritdoc cref="IImagePublisher"/>
public sealed class ImagePublisher(
    ServiceConfiguration config,
    ILogger<ImagePublisher> logger) : IImagePublisher
{
    private const string Sha256Prefix = "sha256:";

    public string? Publish(string sourcePath, string expectedHash, string prefix)
    {
        if (!File.Exists(sourcePath))
        {
            logger.LogWarning("Image publish skipped — source not found: {Path}", sourcePath);
            return null;
        }

        var publishRoot = config.Delivery.PublishedImagePath;
        if (string.IsNullOrWhiteSpace(publishRoot))
        {
            logger.LogError("Image publish failed — Delivery:PublishedImagePath is not configured");
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(sourcePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Image publish failed — could not read source: {Path}", sourcePath);
            return null;
        }

        var actualHex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var expectedHex = NormalizeHash(expectedHash);

        if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "Image publish rejected — hash mismatch for {Path}: expected {Expected}, actual {Actual}",
                sourcePath, expectedHex, actualHex);
            return null;
        }

        var fileName = $"{prefix}-{actualHex[..16]}{Path.GetExtension(sourcePath)}";

        try
        {
            Directory.CreateDirectory(publishRoot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Image publish failed — could not create publish root: {Root}", publishRoot);
            return null;
        }

        // A folder a non-administrator can write to is refused outright (see IsPublishFolderUserWritable).
        if (IsPublishFolderUserWritable(publishRoot)) return null;

        var targetPath = Path.Combine(publishRoot, fileName);
        if (File.Exists(targetPath))
        {
            // The content-derived name makes existence equivalent to correctness — no re-copy.
            logger.LogDebug("Image already published: {Path}", targetPath);
            return targetPath;
        }

        var tempPath = Path.Combine(publishRoot, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, targetPath, overwrite: true);
            logger.LogDebug("Image published: {Path}", targetPath);
            return targetPath;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Image publish failed — could not write {Path}", targetPath);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            return null;
        }
    }

    public void SweepExcept(string? keepFileName, string prefix)
    {
        var publishRoot = config.Delivery.PublishedImagePath;
        if (string.IsNullOrWhiteSpace(publishRoot) || !Directory.Exists(publishRoot)) return;
        if (IsPublishFolderUserWritable(publishRoot)) return;

        var glob = $"{prefix}-*";
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(publishRoot, glob).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Prefix} sweep: could not enumerate {Root}", prefix, publishRoot);
            return;
        }

        foreach (var path in candidates)
        {
            var name = Path.GetFileName(path);
            if (keepFileName is not null && string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Delete(path);
                logger.LogDebug("{Prefix} sweep: deleted stale {File}", prefix, name);
            }
            catch (Exception ex)
            {
                // A locked stale file must never abort the sweep of its siblings.
                logger.LogWarning(ex, "{Prefix} sweep: failed to delete {File} — continuing", prefix, name);
            }
        }
    }

    private static string NormalizeHash(string hash) =>
        hash.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase)
            ? hash[Sha256Prefix.Length..]
            : hash;


    // ── Publish-folder write-exposure gate ───────────────────────────────────────

    private readonly object _gate = new();
    private string? _reportedSignature;   // the exposure last reported (null = none reported / cleared)
    private bool _reportedUnreadable;

    /// <summary>
    /// Verifies the publish folder is not writable by a non-administrator (see <see cref="PublishFolderAcl"/>).
    /// Returns true when it IS user-writable, in which case publishing and sweeping are refused: the images
    /// are handed to the wallpaper and lock-screen APIs in every session, so a non-administrator who can
    /// replace one bypasses the index-signing chain. Logged once per distinct finding (Error), and once when
    /// it clears; never throws. The service verifies and reports - it never applies an ACL; the installer owns
    /// the folder's ACL. A folder whose ACL cannot be read is not refused (the failure is reported once).
    /// </summary>
    internal bool IsPublishFolderUserWritable(string publishRoot)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(publishRoot); }
        catch { return false; }

        try
        {
            if (!Directory.Exists(fullPath)) return false;   // created on first publish; checked again then
            var exposure = PublishFolderAcl.FindWriteExposure(PublishFolderAcl.Read(fullPath));
            lock (_gate)
            {
                _reportedUnreadable = false;
                if (exposure is null)
                {
                    if (_reportedSignature is not null)
                        logger.LogInformation("Delivery:PublishedImagePath ({Path}) is no longer user-writable; publishing resumes", fullPath);
                    _reportedSignature = null;
                    return false;
                }

                if (exposure.Signature != _reportedSignature)
                {
                    _reportedSignature = exposure.Signature;
                    logger.LogError(
                        "Delivery:PublishedImagePath ({Path}) is user-writable: {Identity} ({Sid}) holds {Rights}. " +
                        "Publishing and sweeping are refused and the previous state is left alone, because a " +
                        "non-administrator who can replace a published image bypasses the signed index. {Remediation}",
                        fullPath, exposure.Identity, exposure.Sid, exposure.Rights, PublishFolderAcl.Remediation(fullPath));
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!_reportedUnreadable)
                {
                    _reportedUnreadable = true;
                    logger.LogWarning(ex, "Could not read the ACL of {Path} - publishing is not refused, but the write-access check did not run", fullPath);
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Startup check, called once from Program.cs after the host is built. Never throws and never stops the
    /// service. An empty or relative path is an Error (every publish would fail); a user-writable folder is an
    /// Error and refuses publishing from then on (see <see cref="IsPublishFolderUserWritable"/>).
    /// </summary>
    internal void VerifyPublishFolder()
    {
        var publishRoot = config.Delivery.PublishedImagePath;
        try
        {
            if (string.IsNullOrWhiteSpace(publishRoot) || !Path.IsPathRooted(publishRoot))
            {
                logger.LogError(
                    "Delivery:PublishedImagePath is empty or not an absolute path: '{Path}' - " +
                    "image publishing will fail every cycle for every surface", publishRoot);
                return;
            }
            IsPublishFolderUserWritable(publishRoot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delivery:PublishedImagePath is not a usable path: '{Path}'", publishRoot);
        }
    }
}
