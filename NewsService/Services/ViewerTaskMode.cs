namespace NewsService.Services;

/// <summary>How much of the NewsViewer launch surface NewsService manages (<c>ViewerTasks:Mode</c>).</summary>
internal enum ViewerTaskMode
{
    /// <summary>Create, verify and repair (the default).</summary>
    Manage,

    /// <summary>Verify and log drift; change nothing.</summary>
    VerifyOnly,

    /// <summary>Do nothing.</summary>
    Off,
}

/// <summary>The effective mode, plus an error to log when the configured value was not recognised.</summary>
internal readonly record struct ViewerTaskModeResult(ViewerTaskMode Mode, string? Error);

/// <summary>
/// Resolves <c>ViewerTasks:Mode</c>. Pure. A typo must never disable the launch surface, so an
/// unrecognised value falls back to <see cref="ViewerTaskMode.Manage"/> (with an error), never to Off.
/// An unset Mode defers to the older <c>ViewerTasks:Manage</c> bool, so existing "Manage = 0"
/// deployments stay hands-off.
/// </summary>
internal static class ViewerTaskModeResolver
{
    public const string ValidValues = "Manage, VerifyOnly, Off";

    public static ViewerTaskModeResult Resolve(string? mode, bool legacyManage)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return new(legacyManage ? ViewerTaskMode.Manage : ViewerTaskMode.Off, null);

        // Explicit names only: Enum.TryParse would also accept "1", "2" and "0,1".
        switch (mode.Trim().ToLowerInvariant())
        {
            case "manage": return new(ViewerTaskMode.Manage, null);
            case "verifyonly": return new(ViewerTaskMode.VerifyOnly, null);
            case "off": return new(ViewerTaskMode.Off, null);
            default:
                return new(ViewerTaskMode.Manage,
                    $"ViewerTasks:Mode '{mode}' is not recognised; valid values are {ValidValues}. " +
                    "Falling back to Manage so the launch surface is not disabled by a typo");
        }
    }
}
