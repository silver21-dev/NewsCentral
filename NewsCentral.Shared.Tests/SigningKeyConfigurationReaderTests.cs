using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;

namespace NewsCentral.Shared.Tests;

public sealed class SigningKeyConfigurationReaderTests
{
    [Fact]
    public void GetPublicKeys_ReturnsBothKeys_CurrentFirst()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signing:PublicKey"]         = "PUBKEY_CURRENT",
                ["Signing:PublicKeyPrevious"] = "PUBKEY_PREVIOUS"
            })
            .Build();

        var keys = SigningKeyConfigurationReader.GetPublicKeys(config);

        Assert.Equal(2, keys.Length);
        Assert.Equal("PUBKEY_CURRENT",  keys[0]);
        Assert.Equal("PUBKEY_PREVIOUS", keys[1]);
    }

    [Fact]
    public void GetPublicKeys_FiltersEmptyStrings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signing:PublicKey"]         = "",                // empty → omitted
                ["Signing:PublicKeyPrevious"] = "PUBKEY_PREVIOUS"
            })
            .Build();

        var keys = SigningKeyConfigurationReader.GetPublicKeys(config);

        Assert.Single(keys);
        Assert.Equal("PUBKEY_PREVIOUS", keys[0]);
    }

    [Fact]
    public void GetPublicKeys_FiltersNullValues()
    {
        // Config key absent → IConfiguration returns null
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signing:PublicKeyPrevious"] = "PUBKEY_PREVIOUS"
                // PublicKey absent → null
            })
            .Build();

        var keys = SigningKeyConfigurationReader.GetPublicKeys(config);

        Assert.Single(keys);
        Assert.Equal("PUBKEY_PREVIOUS", keys[0]);
    }

    [Fact]
    public void GetPublicKeys_ReturnsEmpty_WhenNeitherConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var keys = SigningKeyConfigurationReader.GetPublicKeys(config);

        Assert.Empty(keys);
    }

    [Fact]
    public void GetPublicKeys_IgnoresLegacyPerTeamKeys()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signing:team-a:PublicKey"] = "KEY_A",
                ["Signing:PublicKey"]        = "KEY_ENV"
            })
            .Build();

        var keys = SigningKeyConfigurationReader.GetPublicKeys(config);

        Assert.Equal(new[] { "KEY_ENV" }, keys);
    }
}
