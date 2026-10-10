using NewsCentral.Models;
using NewsCentral.Security;

namespace NewsViewer.Configuration;

public class ViewerConfiguration
{
    // Company is not configuration — it defines the registry hive path and is the build-time
    // constant SolutionConstants.Company. It is intentionally absent from this POCO.

    /// <summary>
    /// Per-machine master switch (registry: <c>Active</c>, DWORD 0/1, at the hive root). Default
    /// <c>true</c> — an absent key must never disable the fleet. When <c>false</c>, NewsViewer
    /// exits at startup with no action taken: no poster, no wallpaper style re-assert, no
    /// viewerstate write, no telemetry, no dialog. The last-applied wallpaper style is left as-is
    /// (not reverted). The wallpaper image is NewsService's concern and is unaffected either way.
    /// </summary>
    public bool Active { get; set; } = true;

    public string CacheRootPath { get; set; } = @"C:\ProgramData\NewsCentral";
    public bool BypassDailyGate { get; set; } = false;
    public bool BypassImageIntegrityCheck { get; set; } = false;
    public HmacOptions Hmac { get; set; } = new();
    public DeliverySection Delivery { get; set; } = new();
    public DisplaySection Display { get; set; } = new();
    public UiSection Ui { get; set; } = new();
}

public class UiSection
{
    /// <summary>
    /// Registry <c>UiShowDeliveredImages</c> (DWORD 0/1). Default <c>false</c>. When <c>true</c> the poster
    /// form shows two buttons that switch the displayed image to the wallpaper / lock screen
    /// NewsService currently delivers. Theme is read ad hoc in Program.cs and is not on this POCO.
    /// </summary>
    public bool ShowDeliveredImages { get; set; } = false;
}

public class DisplaySection
{
    private int _logicalDayStartHour;

    /// <summary>
    /// Hour (0..23, local) at which the once-per-day display gate rolls over. Default 0 = calendar
    /// day. Clamped on read: an out-of-range value degrades to 0 rather than throwing. Must be
    /// provisioned as REG_SZ — RegistryConfigurationProvider coerces REG_DWORD 0/1 to "False"/"True".
    /// </summary>
    public int LogicalDayStartHour
    {
        get => LogicalDayCalculator.NormalizeStartHour(_logicalDayStartHour);
        set => _logicalDayStartHour = value;
    }
}

public class DeliverySection
{
    /// <summary>
    /// Wallpaper STYLE only — how the image is fitted (Fill/Fit/Stretch/Center/Tile). The
    /// wallpaper IMAGE itself is owned by NewsService (PersonalizationCSP, machine-wide);
    /// NewsViewer no longer selects or applies any wallpaper content. See
    /// docs/newsviewer-spec.md.
    /// </summary>
    public string WallpaperStyle { get; set; } = "Fit";

    /// <summary>"R G B" desktop background colour for Fit letterbox bars (default "0 0 0").</summary>
    public string WallpaperBackgroundColor { get; set; } = "0 0 0";
}
