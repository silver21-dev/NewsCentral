using Microsoft.Win32;

namespace NewsCentral.Configuration;

/// <summary>The two display surfaces NewsService delivers machine-wide via PersonalizationCSP.</summary>
public enum DeliveredSurface
{
    Wallpaper,
    LockScreen
}

/// <summary>
/// Resolves the image NewsService is currently delivering for a surface, for NewsViewer's optional
/// "show delivered image" buttons. Strictly read-only. A surface is available only when NewsService's
/// own enable flag for it is on, PersonalizationCSP names a non-empty image path, and that file
/// exists. Never throws: any failure is reported through <c>onWarning</c> and the surface is treated
/// as unavailable.
/// </summary>
/// <remarks>
/// This is a documented exception to the per-component registry subkey invariant: NewsViewer reads
/// (never writes) <c>NewsService\Delivery\{Wallpaper,LockScreen}Enabled</c>. The fallback used when
/// that value is absent is NewsService's own default, taken from <see cref="ConfigManifests"/>
/// rather than duplicated here.
/// </remarks>
public sealed class DeliveredImageResolver
{
    internal const string CspSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";

    private readonly Func<string, string, object?> _readHklm;
    private readonly Func<string, bool> _fileExists;
    private readonly Action<string>? _onWarning;
    private readonly string _serviceDeliverySubKey;

    /// <param name="readHklm">Reads a value from HKLM (64-bit view): (subKeyPath, valueName) to raw value or null.</param>
    /// <param name="fileExists">File existence check.</param>
    /// <param name="onWarning">Receives a message for every swallowed failure.</param>
    /// <param name="company">Hive segment; defaults to the build-time <see cref="SolutionConstants.Company"/>.</param>
    public DeliveredImageResolver(
        Func<string, string, object?> readHklm,
        Func<string, bool> fileExists,
        Action<string>? onWarning = null,
        string company = SolutionConstants.Company)
    {
        _readHklm = readHklm;
        _fileExists = fileExists;
        _onWarning = onWarning;
        _serviceDeliverySubKey = $@"Software\{company}\{SolutionConstants.SolutionName}\NewsService\Delivery";
    }

    /// <summary>Production instance: HKLM read through <see cref="RegistryView.Registry64"/>, real file system.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static DeliveredImageResolver CreateDefault(Action<string>? onWarning = null) =>
        new(ReadHklm64, File.Exists, onWarning);

    /// <summary>The image path for <paramref name="surface"/>, or null when it is not available.</summary>
    public string? Resolve(DeliveredSurface surface)
    {
        try
        {
            var (flagKey, cspValue) = surface switch
            {
                DeliveredSurface.Wallpaper => ("Delivery:WallpaperEnabled", "DesktopImagePath"),
                _ => ("Delivery:LockScreenEnabled", "LockScreenImagePath")
            };

            if (!IsEnabled(flagKey)) return null;

            var path = _readHklm(CspSubKey, cspValue) as string;
            if (string.IsNullOrWhiteSpace(path)) return null;

            return _fileExists(path) ? path : null;
        }
        catch (Exception ex)
        {
            _onWarning?.Invoke($"Delivered {surface} image unavailable: {ex.Message}");
            return null;
        }
    }

    // The leaf of the manifest key is also the registry value name (Delivery\WallpaperEnabled).
    private bool IsEnabled(string manifestKey)
    {
        var leaf = manifestKey[(manifestKey.LastIndexOf(':') + 1)..];
        return ParseFlag(_readHklm(_serviceDeliverySubKey, leaf)) ?? ServiceDefault(manifestKey);
    }

    private static bool ServiceDefault(string manifestKey) =>
        ParseFlag(ConfigManifests.NewsService.Keys.First(k => k.CanonicalKey == manifestKey).Default) ?? false;

    // REG_DWORD 0/1 (as the manifest documents) or a REG_SZ boolean; anything else counts as not set.
    private static bool? ParseFlag(object? raw) => raw switch
    {
        int i => i != 0,
        string s when bool.TryParse(s, out var b) => b,
        string s when s == "0" => false,
        string s when s == "1" => true,
        _ => null
    };

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static object? ReadHklm64(string subKey, string valueName)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(subKey);
        return key?.GetValue(valueName);
    }
}
