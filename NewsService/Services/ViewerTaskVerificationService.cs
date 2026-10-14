namespace NewsService.Services;

/// <summary>
/// Reports on the NewsViewer launch surface (<c>ViewerTasks:Mode</c>). It never creates, modifies or
/// deletes a scheduled task; the installer owns that.
/// <list type="bullet">
/// <item>VerifyOnly (default) - verifies after a short delay and then once per <see cref="VerifyInterval"/>
/// for the life of the service. Drift is logged at Warning only when the drift state changes; recovery is
/// logged once; a restart re-reports existing drift.</item>
/// <item>Off - logs once at start and does nothing else.</item>
/// </list>
/// </summary>
internal sealed class ViewerTaskVerificationService(
    ViewerTaskVerifier verifier,
    ILogger<ViewerTaskVerificationService> logger) : BackgroundService
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    /// <summary>Drift needs reporting, not minute-level detection; each check spawns two schtasks.exe reads.</summary>
    internal static readonly TimeSpan VerifyInterval = TimeSpan.FromHours(1);

    /// <summary>Retry after a verification that could not run at all.</summary>
    internal static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first: hosted services start one after another, and this must not hold up the Worker.
        await Task.Yield();
        try
        {
            verifier.WarnIfObsoleteManageKey();
            var resolved = verifier.ResolveMode();
            if (resolved.Error is not null) logger.LogError("{Error}", resolved.Error);

            if (resolved.Mode == ViewerTaskMode.Off)
            {
                logger.LogInformation("ViewerTasks:Mode=Off - NewsService does not verify the NewsViewer scheduled tasks. " +
                                      "It never creates or repairs them in any mode; the installer owns them");
                return;
            }

            logger.LogInformation("ViewerTasks:Mode=VerifyOnly - NewsService verifies the NewsViewer scheduled tasks and reports drift; " +
                                  "it never creates or repairs them (the installer owns them)");
            await VerifyLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service stopping.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NewsViewer task verification stopped unexpectedly");
        }
    }

    private async Task VerifyLoopAsync(CancellationToken ct)
    {
        await Task.Delay(InitialDelay, ct);
        var reporter = new VerifyReporter(logger);
        while (true)
        {
            var report = await verifier.VerifyAsync(ct);
            if (report is not null) reporter.Report(report);
            await Task.Delay(report is null ? FailureRetryInterval : VerifyInterval, ct);
        }
    }
}
