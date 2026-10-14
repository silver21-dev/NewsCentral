using System.Reflection;
using System.Text;

namespace NewsService.Services;

/// <summary>
/// Writes the generated registration script. Used only by <c>--emit-task-script</c> on the build
/// machine: the script is a build artifact, never written by a running service.
/// </summary>
internal static class TaskScriptEmitter
{
    public static TaskScriptHeader NewHeader() => new(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "unknown",
        DateTimeOffset.UtcNow,
        Environment.MachineName);

    public static void Write(string path, string script)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

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
    }
}
