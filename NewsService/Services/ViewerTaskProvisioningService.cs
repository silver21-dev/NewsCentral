namespace NewsService.Services;

/// <summary>
/// Runs the NewsViewer launch-surface work shortly after start, off the sync Worker's path
/// (<c>ViewerTasks:Mode</c>):
/// <list type="bullet">
/// <item>Manage - <see cref="ViewerTaskProvisioner"/> every 5 minutes until one pass fully succeeds, then exits;
/// every service start runs it again, which repairs a deleted task or a moved install.</item>
/// <item>VerifyOnly - writes the registration script, then verifies every 5 minutes for the life of the
/// service and logs drift only when the drift state changes.</item>
/// <item>Off - writes the registration script, logs once and does nothing else.</item>
/// </list>
/// </summary>
internal sealed class ViewerTaskProvisioningService(
    ViewerTaskProvisioner provisioner,
    ILogger<ViewerTaskProvisioningService> logger) : BackgroundService
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first: hosted services start one after another, and this must not hold up the Worker.
        await Task.Yield();
        try
        {
            var resolved = provisioner.ResolveMode();
            if (resolved.Error is not null) logger.LogError("{Error}", resolved.Error);

            if (resolved.Mode != ViewerTaskMode.Manage)
            {
                var script = provisioner.TryEmitScript();
                var where = script is null ? "no script was emitted (ViewerTasks:ScriptPath is empty or could not be written)" : $"registration script: {script}";
                if (resolved.Mode == ViewerTaskMode.Off)
                {
                    logger.LogInformation("ViewerTasks:Mode=Off - NewsService does not register, verify or repair the NewsViewer scheduled tasks; {Where}", where);
                    return;
                }

                logger.LogInformation("ViewerTasks:Mode=VerifyOnly - NewsService verifies the NewsViewer scheduled tasks and reports drift, but changes nothing; {Where}", where);
                await VerifyLoopAsync(stoppingToken);
                return;
            }

            await Task.Delay(InitialDelay, stoppingToken);
            while (!await provisioner.RunPassAsync(stoppingToken))
                await Task.Delay(RetryInterval, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service stopping.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NewsViewer task provisioning stopped unexpectedly");
        }
    }

    // Logs on change only: the same drift is reported once, a different drift is reported again, and
    // recovery is reported once. A restart starts with no memory, so existing drift is re-reported.
    private async Task VerifyLoopAsync(CancellationToken ct)
    {
        await Task.Delay(InitialDelay, ct);
        string? reported = null;
        while (true)
        {
            var drift = await provisioner.VerifyAsync(ct);
            if (drift is not null)
            {
                var state = string.Join("\n", drift);
                if (state != reported)
                {
                    if (drift.Count == 0)
                        logger.LogInformation("NewsViewer scheduled tasks verified: both are in place");
                    else
                        logger.LogWarning(
                            "NewsViewer launch surface has drifted and ViewerTasks:Mode=VerifyOnly will not repair it. " +
                            "Re-run the registration script (ViewerTasks:ScriptPath) through your change process: {Drift}",
                            string.Join("; ", drift));
                    reported = state;
                }
            }
            await Task.Delay(RetryInterval, ct);
        }
    }
}
