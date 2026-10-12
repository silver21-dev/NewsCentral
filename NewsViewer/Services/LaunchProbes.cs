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

    private const int SmRemoteSession = 0x1000;
    private const int WtsClientProtocolType = 16;   // USHORT: 0 = console, 1 = ICA, 2 = RDP

    private readonly bool _simulateRemote;
    private bool? _remote;

    /// <param name="simulateRemote">Debug-only <c>--simulate-remote</c>: <see cref="IsRemoteSession"/> reports true.</param>
    public LaunchProbes(bool simulateRemote = false) => _simulateRemote = simulateRemote;

    // Cached: the answer cannot change during this one-shot process, and Program consults it more than once.
    public bool IsRemoteSession() => _remote ??= _simulateRemote || DetectRemote();

    // Any one signal is enough (RemoteDetection.IsRemote); an unreadable signal simply does not count.
    private static bool DetectRemote()
    {
        bool smRemote = false, horizon = false;
        int? protocol = null;
        try { smRemote = GetSystemMetrics(SmRemoteSession) != 0; } catch { }
        try { horizon = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ViewClient_Machine_Name")); } catch { }
        try { protocol = QueryClientProtocolType(); } catch { }
        return RemoteDetection.IsRemote(smRemote, horizon, protocol);
    }

    private static int? QueryClientProtocolType()
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, WtsClientProtocolType,
                out var buffer, out var bytes) || buffer == IntPtr.Zero)
            return null;
        try { return bytes >= 2 ? Marshal.ReadInt16(buffer) : null; }
        finally { WTSFreeMemory(buffer); }
    }

    // A published-app (seamless) session runs apps without Explorer, so there is no taskbar window and no
    // desktop to show a poster on. A failed probe counts as "has a shell": never block on a probe error.
    public bool HasShell()
    {
        try { return FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero; }
        catch { return true; }
    }

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
        bool qunsBusy;
        try { qunsBusy = SHQueryUserNotificationState(out int state) == 0 && ProbeMath.IsBusyState(state); }
        catch { qunsBusy = false; }
        return qunsBusy || IsFullScreenForeground();
    }

    // Foreground window covers its whole monitor (rcMonitor) and is not maximized. Any failure = not busy.
    private static bool IsFullScreenForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            GetWindowThreadProcessId(hwnd, out uint pid);
            bool own = pid == (uint)Environment.ProcessId;

            string? cls = null;
            var buf = Marshal.AllocHGlobal(512);
            try
            {
                int len = GetClassNameW(hwnd, buf, 256);
                if (len > 0) cls = Marshal.PtrToStringUni(buf, len);
            }
            finally { Marshal.FreeHGlobal(buf); }

            ScreenRect? window = null, monitor = null;
            if (GetWindowRect(hwnd, out var r)) window = new ScreenRect(r.Left, r.Top, r.Right, r.Bottom);

            var hMon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (hMon != IntPtr.Zero)
            {
                var mi = new MonitorInfo { CbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfoW(hMon, ref mi)) monitor = new ScreenRect(mi.Monitor.Left, mi.Monitor.Top, mi.Monitor.Right, mi.Monitor.Bottom);
            }

            return FullScreenHeuristic.IsBusy(true, own, cls, window, monitor, IsZoomed(hwnd));
        }
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

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint CbSize; public Rect Monitor; public Rect Work; public uint Flags; }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetClassNameW(IntPtr hWnd, IntPtr lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int pquns);
}
