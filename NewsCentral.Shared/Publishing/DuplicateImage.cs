namespace NewsCentral.Publishing;

/// <summary>Pure choice of the picture a duplicated presentation is seeded with.</summary>
public static class DuplicateImage
{
    /// <summary>
    /// The source's ORIGINAL image bytes win when non-empty (the pre-posterization picture); the embedded
    /// content image is only a fallback when the original is missing or unreadable. The name is the source's
    /// original file name, or "image.jpg" when it has none.
    /// </summary>
    /// <exception cref="InvalidOperationException">Neither picture is available.</exception>
    public static (byte[] Bytes, string OriginalName) Choose(
        byte[]? originalBytes,
        string? contentImageBase64,
        string? imageOriginalName,
        string presentationId)
    {
        var imageData = originalBytes;

        if ((imageData is null || imageData.Length == 0) && !string.IsNullOrEmpty(contentImageBase64))
            imageData = Convert.FromBase64String(contentImageBase64);

        if (imageData is null || imageData.Length == 0)
            throw new InvalidOperationException(
                $"Cannot duplicate presentation {presentationId}: source image is unavailable");

        var originalName = string.IsNullOrEmpty(imageOriginalName) ? "image.jpg" : imageOriginalName;

        return (imageData, originalName);
    }
}
