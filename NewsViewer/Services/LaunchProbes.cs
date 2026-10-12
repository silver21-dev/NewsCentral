using System.Runtime.InteropServices;
using NewsCentral.Launch;

namespace NewsViewer.Services;

/// <summary>
/// Win32-backed environment probes for the launch decision. Source-generated <c>LibraryImport</c>
/// P/Invoke with blittable types only (no COM, NativeAOT-safe). Every probe fails safe: a failed
/// visibility check counts as "not visible", a failed busy check as "not busy".
/// </summary>
internal sealed partial class LaunchProbes : ILaunchProbes
{
    private const uint WtsCurrentSession = 0xFFFFFFFF;
    private const int WtsSessionInfoEx = 25;
    private const int WtsActive = 0;
    private const int WtsSessionStateUnlock = 1;   // WTS_SESSIONSTATE_LOCK = 0, UNLOCK = 1 (verified on an unlocked session)
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    public bool IsSessionVisible()
    {
        try { return IsSessionActiveAndUnlocked() && IsInputDesktopDefault(); }
        catch { return false; }
    }

    public TimeSpan IdleTime()
    {
        var info = new LastInputInfo { CbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;   // unknown: treat as present
        return ProbeMath.Idle(unchecked((uint)Environment.TickCount), info.DwTime);
    }

    public bool IsBusy()
    {
        try { return SHQueryUserNotificationState(out int state) == 0 && ProbeMath.IsBusyState(state); }
        catch { return false; }
    }

    // WTSINFOEXW { DWORD Level; union { WTSINFOEX_LEVEL1_W { ULONG SessionId; WTS_CONNECTSTATE_CLASS
    // SessionState; LONG SessionFlags; ... } } } — the union is 8-byte aligned (LARGE_INTEGER members),
    // so SessionState sits at offset 12 and SessionFlags at 16.
    private static bool IsSessionActiveAndUnlocked()
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, WtsSessionInfoEx,
                out var buffer, out var bytes) || buffer == IntPtr.Zero || bytes < 20)
            return false;
        try
        {
            int state = Marshal.ReadInt32(buffer, 12);
            int flags = Marshal.ReadInt32(buffer, 16);
            return state == WtsActive && flags == WtsSessionStateUnlock;
        }
        finally { WTSFreeMemory(buffer); }
    }

    // While Winlogon / the secure desktop has input (lock screen, UAC), OpenInputDesktop fails with
    // access denied or returns a desktop that is not "Default" — both mean not visible.
    private static bool IsInputDesktopDefault()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero) return false;
        var name = Marshal.AllocHGlobal(512);
        try
        {
            if (!GetUserObjectInformationW(desktop, UoiName, name, 512, out _)) return false;
            return string.Equals(Marshal.PtrToStringUni(name), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            CloseDesktop(desktop);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(
        IntPtr hServer, uint sessionId, int wtsInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(IntPtr pMemory);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformationW(IntPtr hObj, int nIndex, IntPtr pvInfo, uint nLength, out uint lpnLengthNeeded);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr hDesktop);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo plii);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int pquns);
}
