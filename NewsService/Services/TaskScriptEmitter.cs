using System.Reflection;
using System.Text;

namespace NewsService.Services;

internal enum EmitOutcome { Written, Unchanged }

/// <summary>
/// Writes the generated script, and only when it differs: the comparison ignores the
/// <c># Generated:</c> timestamp line, so a restart that resolves the same inputs leaves the file
/// (and its timestamp) alone.
/// </summary>
internal static class TaskScriptEmitter
{
    public static TaskScriptHeader NewHeader() => new(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "unknown",
        DateTimeOffset.UtcNow,
        Environment.MachineName);

    public static string ExpandPath(string path) => Environment.ExpandEnvironmentVariables(path.Trim());

    public static EmitOutcome Emit(string path, string script)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (File.Exists(path) && SameIgnoringTimestamp(File.ReadAllText(path, Encoding.ASCII), script))
            return EmitOutcome.Unchanged;

        // Temp file + replace: a reader never sees a half-written script.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, script, Encoding.ASCII);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        return EmitOutcome.Written;
    }

    internal static bool SameIgnoringTimestamp(string a, string b) => Stable(a) == Stable(b);

    private static string Stable(string script) =>
        string.Join('\n', script.Replace("\r\n", "\n").Split('\n')
            .Where(line => !line.StartsWith(TaskScriptGenerator.GeneratedMarker, StringComparison.Ordinal)));
}
