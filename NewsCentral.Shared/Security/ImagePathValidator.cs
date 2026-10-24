namespace NewsCentral.Security;

/// <summary>Outcome of <see cref="ImagePathValidator.Validate"/>; <see cref="Reason"/> is for logging.</summary>
public readonly record struct ImagePathValidation(bool IsValid, string Reason)
{
    public static ImagePathValidation Ok { get; } = new(true, "ok");
    public static ImagePathValidation Fail(string reason) => new(false, reason);
}

/// <summary>
/// Validates the <c>ContentInfo.ImagePath</c> of a downloaded <c>index.json</c> before any
/// component builds a file or blob path from it (docs/anti-tamper.md §8). The only accepted shape is
/// exactly what NewsCentral's <c>PresentationService</c> emits:
/// <c>{team}/images/{original|generated}/{name}.png|.jpg</c>, with <c>/</c> as the only separator.
/// Pure and non-throwing.
/// </summary>
public static class ImagePathValidator
{
    private const int SegmentCount = 4;

    private static readonly string[] AllowedSubfolders = ["original", "generated"];
    private static readonly string[] AllowedExtensions = [".png", ".jpg"];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <param name="imagePath">The index entry's <c>ImagePath</c> as authored.</param>
    /// <param name="teamFolder">The team folder of the index being processed.</param>
    public static ImagePathValidation Validate(string? imagePath, string? teamFolder)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return ImagePathValidation.Fail("empty ImagePath");
        if (string.IsNullOrWhiteSpace(teamFolder))
            return ImagePathValidation.Fail("no team folder to validate against");

        if (imagePath.Contains(':'))
            return ImagePathValidation.Fail("contains ':' (drive letter or alternate data stream)");
        if (imagePath.Contains('\\'))
            return ImagePathValidation.Fail("contains '\\' (only '/' is accepted; rooted, UNC and device paths are rejected)");

        var segments = imagePath.Split('/');
        var invalidChars = Path.GetInvalidFileNameChars();

        foreach (var segment in segments)
        {
            if (segment.Length == 0) return ImagePathValidation.Fail("empty segment (rooted path or '//')");
            if (segment is "." or "..") return ImagePathValidation.Fail($"'{segment}' segment");
            if (segment[^1] is '.' or ' ') return ImagePathValidation.Fail($"segment '{segment}' ends with a dot or space");
            if (segment.IndexOfAny(invalidChars) >= 0) return ImagePathValidation.Fail($"segment '{segment}' contains an invalid filename character");
        }

        if (segments.Length != SegmentCount)
            return ImagePathValidation.Fail($"expected {SegmentCount} segments (team/images/original|generated/name), found {segments.Length}");

        if (!segments[0].Equals(teamFolder, StringComparison.OrdinalIgnoreCase))
            return ImagePathValidation.Fail($"team segment '{segments[0]}' is not the index's team '{teamFolder}'");
        if (!segments[1].Equals("images", StringComparison.OrdinalIgnoreCase))
            return ImagePathValidation.Fail($"second segment '{segments[1]}' is not 'images'");
        if (!AllowedSubfolders.Contains(segments[2], StringComparer.OrdinalIgnoreCase))
            return ImagePathValidation.Fail($"third segment '{segments[2]}' is not 'original' or 'generated'");

        var name = segments[3];
        var ext  = Path.GetExtension(name);
        if (!AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return ImagePathValidation.Fail($"extension '{ext}' is not .png or .jpg");

        var stem = name[..^ext.Length];
        if (stem.Length == 0)
            return ImagePathValidation.Fail("file name has no name before the extension");
        if (ReservedDeviceNames.Contains(stem.Split('.')[0]))
            return ImagePathValidation.Fail($"'{stem}' is a reserved device name");

        return ImagePathValidation.Ok;
    }

    /// <summary>
    /// Containment check applied after <see cref="Validate"/>: the fully-normalized combination of
    /// <paramref name="teamRoot"/> and <paramref name="relativePath"/> must lie strictly inside
    /// <paramref name="teamRoot"/>. Used for the cache (<c>{cacheRoot}\{team}</c>) and the file-share source
    /// (<c>{shareRoot}\{team}</c>). Never throws.
    /// </summary>
    public static bool TryResolveContained(string teamRoot, string relativePath, out string fullPath, out string reason)
    {
        fullPath = string.Empty;
        try
        {
            var root = Path.GetFullPath(teamRoot);
            if (!root.EndsWith(Path.DirectorySeparatorChar) && !root.EndsWith(Path.AltDirectorySeparatorChar))
                root += Path.DirectorySeparatorChar;

            var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"resolves to '{full}', outside '{root}'";
                return false;
            }

            fullPath = full;
            reason = "ok";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = $"not a usable path ({ex.GetType().Name})";
            return false;
        }
    }

    /// <summary>
    /// Blob-name check: the name handed to the blob client must be the validated path and start with
    /// <c>{team}/</c>. Call only with a string that already passed <see cref="Validate"/>.
    /// </summary>
    public static bool IsBlobNameUnderTeam(string blobName, string teamFolder) =>
        blobName.StartsWith(teamFolder + "/", StringComparison.OrdinalIgnoreCase);
}
