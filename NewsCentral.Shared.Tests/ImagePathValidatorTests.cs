using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>
/// ImagePath hardening (docs/anti-tamper.md §8): the only accepted shape is the one NewsCentral emits,
/// <c>{team}/images/{original|generated}/{name}.png|.jpg</c>; everything else is rejected, never thrown.
/// </summary>
public sealed class ImagePathValidatorTests
{
    private const string Team = "cz-its";

    [Theory]
    // traversal
    [InlineData("cz-its/images/../../x.png")]
    [InlineData("cz-its/../x/images/a.png")]
    [InlineData(@"..\x.png")]
    [InlineData(@"cz-its\images\..\..\..\Windows\x.png")]
    [InlineData("cz-its/images/./a.png")]
    [InlineData("cz-its/images/generated/../../../x.png")]
    // absolute
    [InlineData(@"C:\x.png")]
    [InlineData("C:x.png")]
    [InlineData(@"\x.png")]
    [InlineData("/x.png")]
    [InlineData("/cz-its/images/generated/a.png")]
    // UNC / device
    [InlineData(@"\\srv\share\x.png")]
    [InlineData("//srv/share/x.png")]
    [InlineData(@"\\?\C:\x.png")]
    [InlineData(@"\\.\C:\x.png")]
    // shape
    [InlineData("other/images/generated/a.png")]
    [InlineData("cz-its/pictures/generated/a.png")]
    [InlineData("cz-its/images/other/a.png")]
    [InlineData("cz-its/images/a.png")]                    // missing segment
    [InlineData("cz-its/images/generated/sub/a.png")]      // extra segment
    [InlineData("a.png")]
    [InlineData("cz-its/images/generated/a.exe")]
    [InlineData("cz-its/images/generated/a.png.exe")]
    [InlineData("cz-its/images/generated/a.gif")]
    [InlineData("cz-its/images/generated/a")]
    [InlineData("cz-its/images/generated/.png")]
    [InlineData("cz-its/images/generated/a.png:evil")]
    [InlineData("cz-its/images/generated/a.png::$DATA")]
    [InlineData("cz-its/images/generated/a.png.")]
    [InlineData("cz-its/images/generated/a.png ")]
    [InlineData("cz-its/images/generated/a<b.png")]
    [InlineData("cz-its/images/generated/a\0b.png")]
    [InlineData("cz-its/images/generated/con.png")]
    [InlineData("cz-its/images/generated//a.png")]
    [InlineData("cz-its/images/generated/a.png/")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects(string? path)
    {
        var result = ImagePathValidator.Validate(path, Team);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Theory]
    [InlineData("cz-its/images/generated/poster_a_v1.jpg")]
    [InlineData("cz-its/images/original/poster_a.png")]
    [InlineData("cz-its/images/generated/POSTER.PNG")]     // mixed-case extension
    [InlineData("cz-its/images/generated/poster.JpG")]
    [InlineData("CZ-ITS/Images/Generated/a.png")]          // team / literal segments are OrdinalIgnoreCase
    [InlineData("cz-its/images/generated/my poster v2.png")]
    public void Accepts(string path) =>
        Assert.True(ImagePathValidator.Validate(path, Team).IsValid);

    [Fact]
    public void EmptyTeamFolder_Rejects() =>
        Assert.False(ImagePathValidator.Validate("cz-its/images/generated/a.png", "").IsValid);

    [Fact]
    public void ExistingSignedFixture_AllImagePathsAreAccepted()
    {
        var json = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "CanonicalJson", "net9-signed-index.json"));
        var index = JsonSerializer.Deserialize<TeamIndexFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        })!;

        Assert.NotEmpty(index.PublishedAssignments);
        Assert.All(index.PublishedAssignments, a =>
            Assert.True(ImagePathValidator.Validate(a.Content.ImagePath, index.TeamFolderName).IsValid, a.Content.ImagePath));
    }

    // ── Containment ───────────────────────────────────────────────────────────

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "nc-contain", Team);

    [Fact]
    public void Containment_NormalPath_Resolves()
    {
        Assert.True(ImagePathValidator.TryResolveContained(Root, "images/generated/a.png", out var full, out _));
        Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../x.png")]                                  // parent
    [InlineData("images/../../x.png")]                        // string-check-clean-looking, resolves outside
    [InlineData("../cz-its-evil/images/a.png")]               // sibling sharing the root's name as a prefix
    [InlineData("images/generated/../../../../x.png")]
    public void Containment_EscapingPath_Rejected(string relative)
    {
        Assert.False(ImagePathValidator.TryResolveContained(Root, relative, out var full, out var reason));
        Assert.Equal(string.Empty, full);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Containment_RootItself_Rejected() =>
        Assert.False(ImagePathValidator.TryResolveContained(Root, ".", out _, out _));

    [Fact]
    public void Containment_UnusablePath_RejectedNotThrown() =>
        Assert.False(ImagePathValidator.TryResolveContained(Root, "images/a\0.png", out _, out _));

    [Theory]
    [InlineData("cz-its/images/generated/a.png", true)]
    [InlineData("CZ-ITS/images/generated/a.png", true)]
    [InlineData("other/images/generated/a.png", false)]
    [InlineData("cz-its-evil/images/generated/a.png", false)]
    public void BlobName_MustStartWithTeamPrefix(string blob, bool expected) =>
        Assert.Equal(expected, ImagePathValidator.IsBlobNameUnderTeam(blob, Team));
}

public sealed class ImageHashVerifierTests
{
    private static readonly byte[] Data = [1, 2, 3, 4];
    private static readonly string Hex =
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Data)).ToLowerInvariant();

    [Theory]
    [InlineData("sha256:{0}")]
    [InlineData("SHA256:{0}")]
    [InlineData("{0}")]
    public void Matches_AcceptsEmittedFormat_CaseInsensitiveHex(string format)
    {
        foreach (var hex in new[] { Hex, Hex.ToUpperInvariant() })
        {
            Assert.True(ImageHashVerifier.TryParse(string.Format(format, hex), out var expected, out _));
            Assert.True(ImageHashVerifier.Matches(Data, expected));
        }
    }

    [Fact]
    public void Matches_WrongBytes_False()
    {
        Assert.True(ImageHashVerifier.TryParse("sha256:" + Hex, out var expected, out _));
        Assert.False(ImageHashVerifier.Matches([9, 9], expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256:")]
    [InlineData("sha256:abc123")]
    [InlineData("sha256:zz")]
    public void TryParse_MissingOrMalformed_False(string? hash)
    {
        Assert.False(ImageHashVerifier.TryParse(hash, out _, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
