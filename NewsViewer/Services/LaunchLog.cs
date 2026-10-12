using NewsCentral.Launch;

namespace NewsViewer.Services;

/// <summary>
/// Per-user one-line-per-launch diagnostic log (<c>launch.log</c> next to the gate state). NewsViewer
/// has no other logging. Size-capped (newest half kept above 64 KB). Plain file I/O that never
/// throws; contains no user names or paths — only trigger, decision, reason, content key and (for a
/// notification) the item title.
/// </summary>
internal sealed class LaunchLog
{
    internal const string FileName = "launch.log";

    private readonly string _path;

    public LaunchLog(string userStatePath) => _path = Path.Combine(userStatePath, FileName);

    public void Append(string trigger, string decision, string reason, string? contentKey, string? title = null)
    {
        try
        {
            var line = string.Join(" | ",
                DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                $"trigger={trigger}",
                $"decision={decision}",
                $"reason={reason}",
                $"key={Clean(contentKey)}") +
                (title is null ? string.Empty : $" | title={Clean(title)}");

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, line + "\n");

            if (new FileInfo(_path).Length > LaunchLogTrimmer.MaxBytes)
                File.WriteAllText(_path, LaunchLogTrimmer.Trim(File.ReadAllText(_path)));
        }
        catch { /* diagnostics must never affect the launch */ }
    }

    public void Delete()
    {
        try { File.Delete(_path); } catch { }
    }

    private static string Clean(string? s) =>
        string.IsNullOrEmpty(s) ? "-" : s.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
}
