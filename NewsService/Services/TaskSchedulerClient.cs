using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace NewsService.Services;

/// <summary>Outcome of one schtasks.exe call.</summary>
internal sealed record SchtasksResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Result of a task query: whether the task exists and, if so, its raw XML.</summary>
internal sealed record TaskQueryResult(bool Exists, string? Xml);

/// <summary>
/// Thin wrapper over schtasks.exe (no Task Scheduler COM). Implementations never throw for an
/// expected outcome (missing task, non-zero exit); a failure to start the process is reported as a
/// failed <see cref="SchtasksResult"/> too.
/// </summary>
internal interface ITaskSchedulerClient
{
    Task<TaskQueryResult> QueryAsync(string taskName, CancellationToken ct);
    Task<SchtasksResult> CreateAsync(string taskName, string xml, CancellationToken ct);
    Task<SchtasksResult> DeleteAsync(string taskName, CancellationToken ct);
}

/// <summary>Who this process runs as — decides whether "access denied" is a fault or the dev-box case.</summary>
internal interface IProcessIdentity
{
    /// <summary>True when running as LocalSystem (the installed service).</summary>
    bool IsLocalSystem { get; }

    /// <summary>True when LocalSystem or an elevated administrator.</summary>
    bool IsElevated { get; }
}

internal sealed class WindowsProcessIdentity : IProcessIdentity
{
    public bool IsLocalSystem { get; }
    public bool IsElevated { get; }

    public WindowsProcessIdentity()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            IsLocalSystem = identity.IsSystem;
            IsElevated = IsLocalSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { /* unknown = neither: the safe direction (a failure is then reported as an Error) */ }
    }
}

internal sealed class SchtasksClient : ITaskSchedulerClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static string SchtasksPath => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public async Task<TaskQueryResult> QueryAsync(string taskName, CancellationToken ct)
    {
        var result = await RunAsync(["/Query", "/TN", taskName, "/XML"], ct);
        // schtasks reports a missing task and any other failure alike (exit 1): a task that cannot be
        // read is treated as absent, and the following Create then surfaces the real error.
        return result.Success ? new TaskQueryResult(true, result.Output) : new TaskQueryResult(false, null);
    }

    public async Task<SchtasksResult> CreateAsync(string taskName, string xml, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "NewsViewerTask-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            // UTF-16 LE with BOM: the encoding schtasks /XML accepts reliably.
            await File.WriteAllTextAsync(tmp, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true), ct);
            return await RunAsync(["/Create", "/TN", taskName, "/XML", tmp, "/F"], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SchtasksResult(-1, ex.Message);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort — a leftover temp file is harmless */ }
        }
    }

    public Task<SchtasksResult> DeleteAsync(string taskName, CancellationToken ct) =>
        RunAsync(["/Delete", "/TN", taskName, "/F"], ct);

    private static async Task<SchtasksResult> RunAsync(string[] arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(SchtasksPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) startInfo.ArgumentList.Add(a);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var output = (await stdout) + (await stderr);
                return new SchtasksResult(process.ExitCode, output);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new SchtasksResult(-1, $"schtasks timed out after {Timeout.TotalSeconds:0} s.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new SchtasksResult(-1, ex.Message);
        }
    }
}
