using System.Runtime.InteropServices;

namespace NewsViewer.Services;

/// <summary>
/// Brings a form of this process to the foreground. Only for use while handling the user's own input
/// (a click), when the foreground rules permit it: no AttachThreadInput, synthetic keystrokes or other
/// foreground-lock workarounds. Source-generated <c>LibraryImport</c> P/Invoke, blittable types only.
/// </summary>
internal static partial class ForegroundWindow
{
    /// <summary>
    /// Activates <paramref name="form"/>. Returns false when Windows refused the foreground switch; the form
    /// is then at least brought to the front of the z-order.
    /// </summary>
    public static bool Activate(Form form)
    {
        form.Activate();
        bool ok;
        try { ok = SetForegroundWindow(form.Handle); }
        catch { ok = false; }
        if (!ok) form.BringToFront();
        return ok;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);
}
