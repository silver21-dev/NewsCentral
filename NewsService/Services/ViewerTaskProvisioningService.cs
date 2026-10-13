namespace NewsService.Services;

/// <summary>
/// Runs <see cref="ViewerTaskProvisioner"/> shortly after start, off the sync Worker's path, and
/// retries every 5 minutes until one pass is fully successful; then it exits. Every service start
/// runs it again, which is what repairs a task that was deleted or an install that moved.
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
}
