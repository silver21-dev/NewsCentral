namespace NewsService.Services;

/// <summary>What NewsService does about the NewsViewer launch surface (<c>ViewerTasks:Mode</c>). It never creates or repairs it.</summary>
internal enum ViewerTaskMode
{
    /// <summary>Verify and report drift (the default).</summary>
    VerifyOnly,

    /// <summary>Do nothing.</summary>
    Off,
}

/// <summary>The effective mode, plus an error to log when the configured value was not recognised.</summary>
internal readonly record struct ViewerTaskModeResult(ViewerTaskMode Mode, string? Error);

/// <summary>
/// Resolves <c>ViewerTasks:Mode</c>. Pure. Absent or unrecognised falls back to
/// <see cref="ViewerTaskMode.VerifyOnly"/> (an unrecognised value with an error): a typo must not
/// silently switch the check off.
/// </summary>
internal static class ViewerTaskModeResolver
{
    public const string ValidValues = "VerifyOnly, Off";

    public static ViewerTaskModeResult Resolve(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return new(ViewerTaskMode.VerifyOnly, null);

        // Explicit names only: Enum.TryParse would also accept "1" and "0,1".
        switch (mode.Trim().ToLowerInvariant())
        {
            case "verifyonly": return new(ViewerTaskMode.VerifyOnly, null);
            case "off": return new(ViewerTaskMode.Off, null);
            default:
                return new(ViewerTaskMode.VerifyOnly,
                    $"ViewerTasks:Mode '{mode}' is not recognised; valid values are {ValidValues}. Falling back to VerifyOnly");
        }
    }
}
