using NewsCentral.Configuration;
using NewsCentral.Security;
using NewsService;
using NewsService.Configuration;
using NewsService.Services;

// --emit-task-script is handled before any host exists, so it works on a machine where the service
// is not installed (a packaging pipeline) and needs nothing but appsettings.json.
if (EmitTaskScriptCommand.IsRequested(args))
    return EmitTaskScriptCommand.Run(args, Console.Out, Console.Error);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "NewsService");

// ── Load appsettings, then the optional dev overlay, then registry overrides ─
// The overlay is wired explicitly and unconditionally (optional: true — its mere presence
// activates it), NOT via DOTNET_ENVIRONMENT: the host runs as Production, so the built-in
// appsettings.{Environment}.json mechanism would never load it. It is gitignored and excluded
// from publish (CopyToPublishDirectory=Never), so nothing in it can reach the fleet. The
// registry provider must stay LAST — registry always wins over both JSON layers.
// Company defines the registry path itself and is therefore not registry-overridable; it is the
// compile-time constant SolutionConstants.Company. The solution segment is SolutionConstants.SolutionName.
builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true);
builder.Configuration.AddRegistryOverrides(SolutionConstants.Company, SolutionConstants.SolutionName, "NewsService");

// Bind typed POCOs from the merged (appsettings + registry) configuration.
var config = builder.Configuration.Get<ServiceConfiguration>() ?? new ServiceConfiguration();
builder.Services.AddSingleton(config);

// ViewerTasks:RegisterEventSource=false: the host's EventLog logger provider would create a missing
// source on its first write, so it is not added while the source is absent. Unknown = leave it alone.
var eventSourceMissingAndNotOurs = !EventSourceGuard.RegistrationAllowed(builder.Configuration)
    && EventSourceGuard.SourceExists(EventSourceGuard.SourceName(builder.Configuration)) == false;
if (eventSourceMissingAndNotOurs)
{
    var providers = builder.Services
        .Where(d => d.ServiceType == typeof(ILoggerProvider)
                    && d.ImplementationType == typeof(Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider))
        .ToList();
    foreach (var d in providers) builder.Services.Remove(d);
}

// ── Repository reader — mode resolved from merged configuration ───────────────
if (config.Repository.StorageMode.Equals("Azure", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IRepositoryReader, AzureBlobRepositoryReader>();
}
else
{
    builder.Services.AddSingleton<IRepositoryReader>(sp =>
        new LocalShareRepositoryReader(
            config.Repository.SharePath,
            sp.GetRequiredService<ILogger<LocalShareRepositoryReader>>()));
}

// AzureBlob section is needed by the blob reader and by the Entra Graph client (which reuses
// the same credential even when StorageMode=Share), so register it unconditionally.
builder.Services.AddSingleton(config.AzureBlob);

// Optional shared WinHTTP transport for blob + Graph (AzureBlob:UseWinHttpProxy). Default off →
// the factory instantiates nothing and all SDKs are built exactly as before.
builder.Services.AddSingleton<AzureProxyTransportFactory>();

// ── Entra device team resolution (Phase 2 — produces resolved-teams.json) ─────
// GraphServiceClient/credential are built lazily inside EntraDeviceClient, so nothing is
// constructed when Entra is disabled or AzureBlob creds are absent.
builder.Services.AddSingleton<IDeviceIdentityProvider, DeviceIdentityProvider>();
builder.Services.AddSingleton<IEntraDeviceClient, EntraDeviceClient>();
builder.Services.AddSingleton<IEntraGroupClient, EntraGroupClient>();
builder.Services.AddSingleton<EntraTeamResolutionService>();

// ── Signing services ─────────────────────────────────────────────────────────
builder.Services.AddSingleton(new HmacService(config.Hmac));   // telemetry (unchanged)
builder.Services.AddSingleton<EcdsaSignatureService>();         // index.json verification

// ── Cache and services ───────────────────────────────────────────────────────
builder.Services.AddSingleton(_ => new CacheManager(
    config.Service.CacheRootPath, JsonDefaults.Options));

builder.Services.AddSingleton<IPersonalizationService, PersonalizationService>();
builder.Services.AddSingleton<IImagePublisher, ImagePublisher>();
builder.Services.AddSingleton<TelemetryUploader>();
builder.Services.AddSingleton<IContentChangedNotifier, EventLogContentChangedNotifier>();
builder.Services.AddSingleton<SyncService>();

builder.Services.AddHostedService<Worker>();
// ── NewsViewer launch surface: read-only verification (the installer owns the tasks) ──────────
builder.Services.AddSingleton<ITaskQueryClient, SchtasksQueryClient>();
builder.Services.AddSingleton<ViewerTaskVerifier>(sp => new ViewerTaskVerifier(
    config,
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ITaskQueryClient>(),
    sp.GetRequiredService<ILogger<ViewerTaskVerifier>>()));
builder.Services.AddHostedService<ViewerTaskVerificationService>();

var host = builder.Build();

// Create the EventLog source explicitly, before anything writes through it, so its existence does not
// depend on Logging:EventLog:LogLevel (the first write could otherwise be ContentChanged itself).
EventSourceGuard.EnsureSource(builder.Configuration, host.Services.GetRequiredService<ILogger<Program>>());

// Startup sanity check — never fatal; a bad or user-writable publish path just means neither
// surface (lock screen, wallpaper) can be reliably managed (see ImagePublisher.Publish), but the
// operator needs to know why.
ImagePublisher.CheckPublishFolderAcl(
    config.Delivery.PublishedImagePath, host.Services.GetRequiredService<ILogger<ImagePublisher>>());

// Session-host advisory — never fatal, best-effort heuristic (see comment below); a wrong read
// just means the warning doesn't fire, which is the safe direction to err in.
WarnIfSessionHostSurfaceUnmanaged(config, host.Services.GetRequiredService<ILogger<Program>>());

if (eventSourceMissingAndNotOurs)
    host.Services.GetRequiredService<ILogger<Program>>().LogWarning(
        "{Message}", EventSourceGuard.MissingSourceMessage(EventSourceGuard.SourceName(builder.Configuration)));

host.Run();
return 0;

/// <summary>
/// Warns once at startup when this machine looks like a Windows Server with Remote Desktop
/// connections allowed and either Delivery:LockScreenEnabled or Delivery:WallpaperEnabled is
/// still at its default <c>true</c> — machine-wide personalization is unsuitable for RDS session
/// hosts, VDI templates, and RemoteApp hosts (one value can't serve many sessions, RDS policy can
/// suppress the desktop background outright, and non-persistent VDI rebuilds every boot).
///
/// Detection is a best-effort, two-part registry heuristic, deliberately conservative:
///   1. HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\fDenyTSConnections == 0
///      (Remote Desktop connections allowed — high confidence, this is exactly what the
///      "Allow Remote Desktop connections" checkbox toggles).
///   2. HKLM\SYSTEM\CurrentControlSet\Control\ProductOptions\ProductType != "WinNT"
///      (a Windows Server SKU, not a client edition — the same registry-backed value WMI exposes
///      as Win32_OperatingSystem.ProductType; medium-high confidence, widely relied upon).
/// Both must hold. This deliberately does NOT try to detect the RD Session Host role
/// specifically (e.g. via Server Manager / Win32_ServerFeature) — no reliable role-presence
/// signal is available from the registry alone without WMI, and WMI would need a new package.
/// The combined signal never fires on a client Windows workstation (ProductType is always
/// "WinNT" there), which is the one false positive explicitly called out as unacceptable; it can
/// still under-fire (miss a real RDSH box whose fDenyTSConnections is non-default) or, more
/// rarely, over-fire on a plain member server with admin RDP enabled but no RDS role at all —
/// accepted trade-offs given the instruction to prefer silence over a false positive.
/// </summary>
static void WarnIfSessionHostSurfaceUnmanaged(ServiceConfiguration config, ILogger logger)
{
    try
    {
        if (!config.Delivery.LockScreenEnabled && !config.Delivery.WallpaperEnabled) return;

        using var tsKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Terminal Server");
        var tsAllowed = tsKey?.GetValue("fDenyTSConnections") is int deny && deny == 0;
        if (!tsAllowed) return;

        using var productKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\ProductOptions");
        var productType = productKey?.GetValue("ProductType") as string;
        var isServerSku = !string.Equals(productType, "WinNT", StringComparison.OrdinalIgnoreCase);
        if (!isServerSku) return;

        logger.LogWarning(
            "This machine appears to be a Windows Server with Remote Desktop connections allowed " +
            "— possibly an RDS session host, VDI template, or RemoteApp host. Machine-wide " +
            "lock-screen/wallpaper personalization (Delivery:LockScreenEnabled, " +
            "Delivery:WallpaperEnabled) is unsuitable for shared multi-session or non-persistent " +
            "hosts; both should normally be set to 0 on such machines. See docs/newsservice-spec.md.");
    }
    catch (Exception ex)
    {
        logger.LogDebug(ex, "Could not evaluate the session-host advisory heuristic — skipping");
    }
}
