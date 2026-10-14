using System.ComponentModel;
using System.Diagnostics;

namespace NewsService.Services;

/// <summary>Result of a task query: whether the task exists and, if so, its raw XML.</summary>
internal sealed record TaskQueryResult(bool Exists, string? Xml);

/// <summary>
/// Read-only view of Task Scheduler. NewsService never creates, modifies or deletes a scheduled
/// task; the installer owns that (docs/packaging.md). Implementations never throw for an expected
/// outcome (missing task, non-zero exit).
/// </summary>
internal interface ITaskQueryClient
{
    Task<TaskQueryResult> QueryAsync(string taskName, CancellationToken ct);
}

/// <summary>Reads a task with <c>schtasks.exe /Query /XML</c> (no Task Scheduler COM).</summary>
internal sealed class SchtasksQueryClient : ITaskQueryClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static string SchtasksPath => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public async Task<TaskQueryResult> QueryAsync(string taskName, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(SchtasksPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "/Query", "/TN", taskName, "/XML" }) startInfo.ArgumentList.Add(a);

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
                var output = await stdout;
                await stderr;
                // schtasks reports a missing task and any other failure alike (exit 1): a task that
                // cannot be read is treated as absent.
                return process.ExitCode == 0 ? new TaskQueryResult(true, output) : new TaskQueryResult(false, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new TaskQueryResult(false, null);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new TaskQueryResult(false, null);
        }
    }
}
