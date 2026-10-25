#pragma warning disable CA1416 // Validate platform compatibility

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Configuration;
using NewsCentral.Localization;
using NewsCentral.Security;
using NewsCentral.Services;
using System.Reflection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace NewsCentral;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Try multiple methods to load configuration
        IConfiguration? config = null;

        // Method 1: Try embedded resource
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("NewsCentral.appsettings.json");

            if (stream != null)
            {
                config = new ConfigurationBuilder()
                    .AddJsonStream(stream)
                    .Build();
                System.Diagnostics.Debug.WriteLine("✓ Configuration loaded from embedded resource");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load embedded config: {ex.Message}");
        }

        // Method 2: Try file system if embedded failed
        if (config == null)
        {
            try
            {
                var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

                if (File.Exists(appSettingsPath))
                {
                    config = new ConfigurationBuilder()
                        .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: false)
                        .Build();
                    System.Diagnostics.Debug.WriteLine($"✓ Configuration loaded from file: {appSettingsPath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load file config: {ex.Message}");
            }
        }

        // Method 3: Use hardcoded defaults if all else fails
        if (config == null)
        {
            var inMemorySettings = new Dictionary<string, string>
            {
                {"DataPath",                          "C:\\Download\\NewsCentral"},
                {"Authentication:EnableAutoLogin",    "true"},
                {"Authentication:UseMockUPN",         "true"},
                {"Authentication:MockUPN",            "petr.prachar@company.com"},
                {"Storage:DefaultStorageType",        "NetworkShare"},
                {"Storage:EnableBlobDistribution",    "false"},
                {"Storage:DistributionMode",          "Local"},
                {"Storage:LocalDistributionPath",     "C:\\Download\\NewsCentralDist"},
                {"Storage:AzureBlobContainerName",    "newscentral"},
                {"AzureBlob:TenantId",                ""},
                {"AzureBlob:ClientId",                ""},
                {"AzureBlob:AccountName",             ""}
            };

            config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings!)
                .Build();
            System.Diagnostics.Debug.WriteLine("⚠ Using hardcoded default configuration");
        }

        // Add appsettings as the base layer, then registry overrides on top.
        // builder.Configuration is a ConfigurationManager — it implements both
        // IConfigurationBuilder and IConfigurationRoot, so values added via
        // AddRegistryOverrides are immediately readable from builder.Configuration.
        builder.Configuration.AddConfiguration(config);
        builder.Configuration.AddRegistryOverrides(
            SolutionConstants.Company,   // build-time constant; defines the hive path, not configuration
            SolutionConstants.SolutionName,
            "NewsCentral");

        // DEBUG: read from builder.Configuration (includes registry overrides)
        System.Diagnostics.Debug.WriteLine("=== CONFIGURATION CHECK ===");
        System.Diagnostics.Debug.WriteLine($"DataPath:        '{builder.Configuration["DataPath"]}'");
        System.Diagnostics.Debug.WriteLine($"EnableAutoLogin: '{builder.Configuration["Authentication:EnableAutoLogin"]}'");
        System.Diagnostics.Debug.WriteLine($"UseMockUPN:      '{builder.Configuration["Authentication:UseMockUPN"]}'");
        System.Diagnostics.Debug.WriteLine($"MockUPN:         '{builder.Configuration["Authentication:MockUPN"]}'");
        System.Diagnostics.Debug.WriteLine("=== END CONFIGURATION CHECK ===");

        // Create AppConfiguration from the merged configuration — includes
        // registry overrides on top of appsettings.json defaults.
        // Previously this used the original 'config' object, which only held
        // appsettings.json values and never saw registry overrides.
        var appConfig = new AppConfiguration(builder.Configuration);

        if (string.IsNullOrEmpty(appConfig.DataPath))
            System.Diagnostics.Debug.WriteLine("⚠ WARNING: AppConfig.DataPath is empty!");
        else
            System.Diagnostics.Debug.WriteLine($"✓ AppConfig.DataPath: '{appConfig.DataPath}'");

        builder.Services.AddSingleton(appConfig);
        builder.Services.AddSingleton(
            new HmacService(new HmacOptions { SecretKey = appConfig.HmacSecretKey }));
        builder.Services.AddSingleton<EcdsaSignatureService>();

        // Policy environment catalog (M3b) — read ONCE at startup from a dedicated registry-only
        // configuration (never the merged appsettings+registry one above), so appsettings can never
        // impersonate a Group Policy-defined environment. Computed here (moved up from later in M4a)
        // because EnvironmentContext's startup DataPath selection, just below, needs it.
        var registryOnlyConfig = new ConfigurationBuilder()
            .AddRegistryOverrides(SolutionConstants.Company, SolutionConstants.SolutionName, "NewsCentral")
            .Build();
        var environmentCatalog = EnvironmentCatalogReader.Read(registryOnlyConfig);
        foreach (var warning in environmentCatalog.Warnings)
            System.Diagnostics.Debug.WriteLine($"[EnvironmentCatalog] {warning}");
        builder.Services.AddSingleton(environmentCatalog);

        // Per-user environment list (M4a) — the same instance is used here to pick the startup
        // DataPath and then registered for EnvironmentDirectoryService to read/write at runtime.
        var userEnvironmentStore = new UserEnvironmentStore();
        builder.Services.AddSingleton(userEnvironmentStore);

        var startupState = userEnvironmentStore.Load();
        var startupOptions = EnvironmentListBuilder.Build(
            environmentCatalog, appConfig.DataPath, startupState, currentDataPath: null, includeHidden: false);
        var selectedStartupPath = EnvironmentStartupSelector.Select(
            startupOptions, startupState.LastUsedDataPath, environmentCatalog, appConfig.DataPath);

        // EnvironmentStartupSelector never falls back to an environment that isn't shown — if the
        // visible list is empty (no Policy entries, no Configured DataPath, no user entries), fall
        // back to AppConfiguration.DataPath directly here, outside that pure function's concern.
        var startupDataPath = !string.IsNullOrWhiteSpace(selectedStartupPath)
            ? selectedStartupPath
            : appConfig.DataPath;

        System.Diagnostics.Debug.WriteLine(
            $"[EnvironmentContext] Startup DataPath: '{startupDataPath}' " +
            $"(selected: {(selectedStartupPath != null ? $"'{selectedStartupPath}'" : "none — fell back to Configured")})");

        // Single runtime source of DataPath — everything below reads it on every call instead of
        // capturing AppConfiguration.DataPath once at construction (see EnvironmentContext.cs).
        builder.Services.AddSingleton(new EnvironmentContext(startupDataPath));

        // ── Authoring tier storage (always local / Azure Files SMB) ─────────
        builder.Services.AddSingleton<IStorageService, LocalStorageService>();

        // M5a: records the outcome of every real distribution call for MainLayout's admin banner
        // and the Environment Management page; resets on an environment switch.
        builder.Services.AddSingleton<DistributionStatusTracker>();

        // ── Distribution tier storage (config-driven, rebuildable on environment switch or save) ──
        // DistributionServiceRouter holds the actual Distribution.Enabled/Mode selection logic
        // (moved from here in M2) and rebuilds its inner service whenever EnvironmentContext.Changed
        // or EnvironmentSettingsService.SettingsChanged fires; a build failure (e.g. missing Azure
        // settings, or an invalid config/environment.json) never fails DI resolution — see
        // DistributionServiceRouter.cs.
        builder.Services.AddSingleton<DistributionServiceRouter>();
        builder.Services.AddSingleton<IBlobDistributionService>(
            sp => sp.GetRequiredService<DistributionServiceRouter>());

        // Register WindowsIdentityService (needs to be before AuthenticationService)
        builder.Services.AddSingleton<WindowsIdentityService>();

        // AuthenticationService now takes IStorageService + WindowsIdentityService — both already
        // registered above, so plain constructor injection is enough (no custom factory needed).
        builder.Services.AddSingleton<AuthenticationService>();

        // Per-environment distribution settings (config/environment.json) — DistributionServiceRouter
        // reads this instead of AppConfiguration directly; see EnvironmentSettingsService.cs.
        builder.Services.AddSingleton<EnvironmentSettingsService>();

        // Team signing keys live in the per-environment secure store (SecurePath), not under DataPath. The
        // root is re-resolved on every call so an environment switch or a settings save takes effect at once.
        builder.Services.AddSingleton(sp =>
        {
            var environment = sp.GetRequiredService<EnvironmentContext>();
            var settings = sp.GetRequiredService<EnvironmentSettingsService>();
            return new TeamKeyService(
                () => environment.DataPath,
                async () =>
                {
                    var effective = await settings.GetAsync();
                    return effective.Settings is null
                        ? new SecureRootResult(null, effective.Error ?? "environment settings unavailable")
                        : new SecureRootResult(
                            EnvironmentSettingsResolver.EffectiveSecurePath(effective.Settings, environment.DataPath), null);
                });
        });

        // Environment picker / switch / add / remove / hide (M4a) — needs TeamContextService,
        // registered a few lines below in "Register other services"; constructor-injection order
        // doesn't matter to the DI container, only presence at resolution time.
        builder.Services.AddSingleton<EnvironmentDirectoryService>();

        // Register other services
        builder.Services.AddSingleton<TeamService>();
        builder.Services.AddSingleton<UserService>();
        builder.Services.AddSingleton<PosterGenerationService>();
        builder.Services.AddSingleton<IndexGenerationService>();
        builder.Services.AddSingleton<PresentationService>();
        builder.Services.AddSingleton<TeamContextService>();
        builder.Services.AddSingleton<ScheduleService>();
        builder.Services.AddSingleton<AssignmentService>();
        builder.Services.AddSingleton<PublishingService>();
        builder.Services.AddSingleton<PosterStatisticsService>();

        // UI-2.2: cached poster thumbnails — PosterThumbnailCache lives in NewsCentral.Shared
        // (platform-neutral); the scaler and the ILogger bridge are this app's own.
        builder.Services.AddSingleton<NewsCentral.Ui.IPosterThumbnailScaler, WindowsPosterThumbnailScaler>();
        builder.Services.AddSingleton<NewsCentral.Ui.IPosterThumbnailCacheLogger, PosterThumbnailCacheLoggerBridge>();
        builder.Services.AddSingleton<NewsCentral.Ui.PosterThumbnailCache>();

        // M5b: DataSeederService no longer seeds anything (no Initialization:* dependency left) —
        // plain constructor injection is enough, same as AuthenticationService above.
        builder.Services.AddSingleton<DataSeederService>();

        // M5b: creates a brand-new environment's first administrator and claims it — the setup
        // wizard's I/O layer. Depends only on EnvironmentDirectoryService (registered above).
        builder.Services.AddSingleton<EnvironmentInitializer>();

        // Add localization — ResourcesPath tells the factory where to find per-type .resx files
        builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        // L10N-1: resolves and applies the UI language (follows Windows by default, per-user
        // override after sign-in — see AuthenticationService). Registered as a singleton so it
        // captures Windows' own UI culture exactly once, before anything in-process can change it.
        builder.Services.AddSingleton<UiLanguageService>();

        // L10N-1.1: replaces the framework's IStringLocalizerFactory (registered by AddLocalization
        // just above, via TryAdd) with a decorator that resolves every string against
        // UiLanguageService.CurrentUiCulture instead of the ambient CultureInfo.CurrentUICulture —
        // see UiCultureStringLocalizerFactory/UiCultureStringLocalizer for why the ambient value
        // alone is unreliable in Blazor Hybrid. MUST be registered AFTER AddLocalization so this
        // registration — not the framework's — is the one every IStringLocalizer/IStringLocalizer<T>
        // consumer actually resolves.
        builder.Services.AddSingleton<IStringLocalizerFactory>(sp =>
        {
            var inner = new ResourceManagerStringLocalizerFactory(
                sp.GetRequiredService<IOptions<LocalizationOptions>>(),
                sp.GetRequiredService<ILoggerFactory>());
            return new UiCultureStringLocalizerFactory(inner, sp.GetRequiredService<UiLanguageService>());
        });

        var app = builder.Build();

        // Before any UI renders, resolve against Windows (the login page always follows Windows —
        // AuthenticationService.LoginAsync/LoginWithUpnAsync apply the signed-in user's own
        // preference once sign-in succeeds).
        app.Services.GetRequiredService<UiLanguageService>().Apply(null);

        return app;
    }
}

#pragma warning restore CA1416