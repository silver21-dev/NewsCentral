using Microsoft.Win32;

namespace NewsService.Services;

/// <summary>
/// Access to the legacy HKLM Run value that used to launch NewsViewer at logon. Only that one value
/// is ever read or deleted.
/// </summary>
internal interface IRunKeyAccessor
{
    /// <summary>The value's data as a string; null when the value is absent or not a string.</summary>
    string? GetValue(string name);

    /// <summary>Deletes the value; true when it existed and was removed.</summary>
    bool DeleteValue(string name);
}

internal sealed class RegistryRunKeyAccessor : IRunKeyAccessor
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    // Always the 64-bit view, whatever the bitness of this process.
    private static RegistryKey OpenHklm64() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public string? GetValue(string name)
    {
        using var hklm = OpenHklm64();
        using var run = hklm.OpenSubKey(RunKeyPath);
        return run?.GetValue(name) as string;
    }

    public bool DeleteValue(string name)
    {
        using var hklm = OpenHklm64();
        using var run = hklm.OpenSubKey(RunKeyPath, writable: true);
        if (run?.GetValue(name) is null) return false;
        run.DeleteValue(name, throwOnMissingValue: false);
        return true;
    }
}
