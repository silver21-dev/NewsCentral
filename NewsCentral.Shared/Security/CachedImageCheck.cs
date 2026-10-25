using System.Security.Cryptography;

namespace NewsCentral.Security;

/// <summary>
/// Display-time gate for a cached image (NewsViewer): the index's <c>ImagePath</c> must pass
/// <see cref="ImagePathValidator"/> and resolve inside <c>{cacheRoot}\{team}\</c>, and — unless
/// <paramref name="skipHashCheck"/> (the dev-only BypassImageIntegrityCheck) — <c>ImageHash</c> must be
/// present and equal the file's SHA-256. There is no fail-open: an empty hash is a rejection. Never
/// throws; <paramref name="reason"/> is for logging and never contains the file's location.
/// </summary>
public static class CachedImageCheck
{
    /// <param name="teamFolder">The team folder of the index the entry came from.</param>
    public static bool TryResolveVerified(string cacheRoot, string teamFolder, string? imagePath,
        string? imageHash, bool skipHashCheck, out string fullPath, out string reason)
    {
        fullPath = string.Empty;

        var shape = ImagePathValidator.Validate(imagePath, teamFolder);
        if (!shape.IsValid)
        {
            reason = $"ImagePath rejected: {shape.Reason}";
            return false;
        }

        // Validated: team is the first segment; the rest is the path under the team root.
        var underTeam = imagePath![(imagePath.IndexOf('/') + 1)..];
        if (!ImagePathValidator.TryResolveContained(Path.Combine(cacheRoot, teamFolder), underTeam, out var full, out var containment))
        {
            reason = $"ImagePath rejected: {containment}";
            return false;
        }

        if (!File.Exists(full))
        {
            reason = "cached image file not found";
            return false;
        }

        if (!skipHashCheck)
        {
            if (!ImageHashVerifier.TryParse(imageHash, out var expected, out var hashProblem))
            {
                reason = hashProblem;
                return false;
            }

            try
            {
                using var stream = File.OpenRead(full);
                if (!ImageHashVerifier.DigestMatches(SHA256.HashData(stream), expected))
                {
                    reason = "cached image does not match ImageHash";
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = $"cached image unreadable ({ex.GetType().Name})";
                return false;
            }
        }

        fullPath = full;
        reason = "ok";
        return true;
    }
}
