using NewsCentral.Publishing;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>Covers DuplicateImage — which picture and file name a duplicated presentation is seeded with.</summary>
public sealed class DuplicateImageTests
{
    private static readonly byte[] Original = { 1, 2, 3 };
    private static readonly byte[] Embedded = { 9, 8 };
    private static readonly string EmbeddedBase64 = Convert.ToBase64String(Embedded);

    [Fact]
    public void OriginalBytes_ArePreferredOverBase64()
    {
        var (bytes, _) = DuplicateImage.Choose(Original, EmbeddedBase64, "a.png", "p1");
        Assert.Equal(Original, bytes);
    }

    [Fact]
    public void EmptyOriginal_FallsBackToBase64()
    {
        var (bytes, _) = DuplicateImage.Choose(Array.Empty<byte>(), EmbeddedBase64, "a.png", "p1");
        Assert.Equal(Embedded, bytes);
    }

    [Fact]
    public void NullOriginal_FallsBackToBase64()
    {
        var (bytes, _) = DuplicateImage.Choose(null, EmbeddedBase64, "a.png", "p1");
        Assert.Equal(Embedded, bytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BothMissing_ThrowsWithExactMessage(string? base64)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DuplicateImage.Choose(null, base64, "a.png", "p1"));
        Assert.Equal("Cannot duplicate presentation p1: source image is unavailable", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyName_GivesImageJpg(string? name)
    {
        var (_, originalName) = DuplicateImage.Choose(Original, null, name, "p1");
        Assert.Equal("image.jpg", originalName);
    }

    [Fact]
    public void GivenName_IsKept()
    {
        var (_, originalName) = DuplicateImage.Choose(Original, null, "photo.png", "p1");
        Assert.Equal("photo.png", originalName);
    }
}
