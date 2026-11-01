using Microsoft.Extensions.Configuration;

namespace NewsCentral.Configuration;

public static class SigningKeyConfigurationReader
{
    /// <summary>
    /// Returns the environment's ECDSA public keys in priority order — current key first, previous key
    /// second — suitable for <see cref="NewsCentral.Security.SignatureGate.Verify"/>. Null/empty entries are
    /// omitted; an empty array is returned when neither key is configured, which makes the gate return
    /// <c>Disabled</c>.
    /// </summary>
    /// <remarks>
    /// One key pair per environment, so the keys are flat (not per team). Registry source:
    ///   HKLM\Software\[Company]\NewsCentral\NewsService\Signing\PublicKey
    ///   HKLM\Software\[Company]\NewsCentral\NewsService\Signing\PublicKeyPrevious
    /// and the same under NewsViewer\Signing. Both are surfaced as configuration keys
    /// (<c>Signing:PublicKey</c>, <c>Signing:PublicKeyPrevious</c>) by <see cref="RegistryConfigurationProvider"/>.
    /// </remarks>
    public static string?[] GetPublicKeys(IConfiguration config)
    {
        var current  = config["Signing:PublicKey"];
        var previous = config["Signing:PublicKeyPrevious"];

        return new[] { current, previous }
            .Where(k => !string.IsNullOrEmpty(k))
            .ToArray();
    }
}
