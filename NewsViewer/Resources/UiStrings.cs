using System.Globalization;
using System.Resources;

namespace NewsViewer.Resources;

/// <summary>
/// Accessor for the user-facing viewer strings in UiStrings.resx (default EN). Lookups go
/// through <see cref="ResourceManager"/> with <see cref="CultureInfo.CurrentUICulture"/>, so
/// adding UiStrings.&lt;culture&gt;.resx satellite files (ES/FR/DE) localizes the viewer with
/// no code change. A missing key falls back to the key name rather than throwing.
/// </summary>
internal static class UiStrings
{
    private static readonly ResourceManager Rm =
        new("NewsViewer.Resources.UiStrings", typeof(UiStrings).Assembly);

    private static string Get(string key) =>
        Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    internal static string AppName => Get(nameof(AppName));
    internal static string AutoCloseTooltip => Get(nameof(AutoCloseTooltip));
    internal static string Close => Get(nameof(Close));
    internal static string FormClosesIn => Get(nameof(FormClosesIn));
    internal static string MoreInformation => Get(nameof(MoreInformation));
    internal static string NotifyDismissTooltip => Get(nameof(NotifyDismissTooltip));
    internal static string NotifyHeading => Get(nameof(NotifyHeading));
    internal static string NotifyOpenTooltip => Get(nameof(NotifyOpenTooltip));
    internal static string NotifyTitleFallback => Get(nameof(NotifyTitleFallback));
    internal static string Offline => Get(nameof(Offline));
    internal static string Online => Get(nameof(Online));
    internal static string Seconds => Get(nameof(Seconds));
    internal static string ShowLockScreenButton => Get(nameof(ShowLockScreenButton));
    internal static string ShowWallpaperButton => Get(nameof(ShowWallpaperButton));
}
