using System.Security.Cryptography;
using NewsCentral.Security;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class KeyFingerprintTests
{
    [Fact]
    public void Of_IsFirst16HexOfSha256OverDecodedBytes_Lowercase()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var expected = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

        Assert.Equal(expected, KeyFingerprint.Of(Convert.ToBase64String(bytes)));
        Assert.Equal("74f81fe167d99b4c", KeyFingerprint.Of("AQIDBAU="));   // stable known value
    }

    [Fact]
    public void Of_IsWhitespaceInsensitiveAtTheEdges()
    {
        Assert.Equal(KeyFingerprint.Of("AQIDBAU="), KeyFingerprint.Of("  AQIDBAU=\r\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!!")]
    public void Of_NullEmptyOrInvalid_ReturnsNull(string? input) => Assert.Null(KeyFingerprint.Of(input));
}
