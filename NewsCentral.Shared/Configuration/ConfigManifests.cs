namespace NewsCentral.Configuration;

/// <summary>
/// The hand-authored, per-module config manifests (spec §7). Single source of truth for the
/// Configuration Review page: the JSON/registry providers surface only *present* keys, whereas
/// these manifests supply the full known surface including absent-but-defaulted keys.
/// ValueHint strings are copied verbatim from docs/config-review-spec.md §7.2–7.4.
/// </summary>
public static class ConfigManifests
{
    public static readonly ComponentManifest NewsService = new()
    {
        ComponentName = "NewsService",
        HasTeamsHive = true,
        HasSigningHive = true,
        HasEntraAttributeSchemes = true,
        HasEntraGroupTeams = true,
        Keys = new[]
        {
            Key("Company", SolutionConstants.Company, null, RegistryValueType.None, ControlKind.Text,
                OverridableState.DefinesPath, "compile-time constant (SolutionConstants.Company); defines hive path"),
            Key("Service:PollIntervalSeconds", "60", @"Service\PollIntervalSeconds", RegistryValueType.RegSz,
                ControlKind.Number, OverridableState.Overridable,
                "integer seconds, 10–86400 (registry: REG_SZ, NOT DWORD — 0/1 coerce to \"False\"/\"True\" and the int binder throws)"),
            Key("Service:CacheRootPath", @"C:\ProgramData\NewsCentral", @"Service\CacheRootPath", RegistryValueType.RegSz,
                ControlKind.Path, OverridableState.Overridable, "absolute path"),
            Key("Repository:StorageMode", "Share", @"Repository\StorageMode", RegistryValueType.RegSz,
                ControlKind.Dropdown, OverridableState.Overridable, "Share | Azure", options: new[] { "Share", "Azure" }),
            Key("Repository:SharePath", "", @"Repository\SharePath", RegistryValueType.RegSz,
                ControlKind.Path, OverridableState.Overridable,
                "UNC or local path; warning: an empty REG_SZ is a PRESENT value — creating the registry value with empty data overrides the appsettings path with empty and disables the share repository"),
            Key("AzureBlob:AuthMode", "Certificate", @"AzureBlob\AuthMode", RegistryValueType.RegSz,
                ControlKind.Dropdown, OverridableState.Overridable, "Certificate | ClientSecret | ClientSecretEnv",
                options: new[] { "Certificate", "ClientSecret", "ClientSecretEnv" }),
            Key("AzureBlob:TenantId", "", @"AzureBlob\TenantId", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable, "GUID"),
            Key("AzureBlob:ClientId", "", @"AzureBlob\ClientId", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable, "GUID"),
            Key("AzureBlob:CertificateThumbprint", "", @"AzureBlob\CertificateThumbprint", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable, "40 hex chars"),
            Key("AzureBlob:ClientSecret", "", @"AzureBlob\ClientSecret", RegistryValueType.RegSz,
                ControlKind.Redacted, OverridableState.Overridable, "client secret", isSecret: true),
            Key(SolutionConstants.NewsServiceAzureClientSecretEnvVar, "", null, RegistryValueType.None,
                ControlKind.Redacted, OverridableState.EnvironmentSourced,
                "machine-scope environment variable (EnvironmentVariableTarget.Machine) read when AzureBlob:AuthMode = ClientSecretEnv; presence only — the value is never displayed; not registry-overridable",
                isSecret: true, envVar: SolutionConstants.NewsServiceAzureClientSecretEnvVar),
            Key("AzureBlob:AccountName", "", @"AzureBlob\AccountName", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable, "storage account, no suffix"),
            Key("AzureBlob:ContainerName", "newscentral", @"AzureBlob\ContainerName", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable, "container name"),
            Key("AzureBlob:UseWinHttpProxy", "false", @"AzureBlob\UseWinHttpProxy", RegistryValueType.RegSz,
                ControlKind.Toggle, OverridableState.Overridable, "true | false (registry: REG_SZ)"),
            Key("Hmac:SecretKey", "", @"Hmac\SecretKey", RegistryValueType.RegSz,
                ControlKind.Redacted, OverridableState.Overridable,
                "Base64, 32 bytes; warning: an empty REG_SZ is a PRESENT value — creating the registry value with empty data overrides the appsettings key with empty and disables HMAC",
                isSecret: true),
            Key("Signing:RequireSignedIndex", "false", @"Signing\RequireSignedIndex", RegistryValueType.RegSz,
                ControlKind.Toggle, OverridableState.Overridable,
                "true | false (registry: REG_SZ); ad-hoc read, not on POCO"),
            Key("Entra:Enabled", "false", @"Entra\Enabled", RegistryValueType.RegSz,
                ControlKind.Toggle, OverridableState.Overridable, "true | false (registry: REG_SZ)"),
            Key("Entra:GracePeriodMinutes", "240", @"Entra\GracePeriodMinutes", RegistryValueType.RegSz,
                ControlKind.Number, OverridableState.Overridable,
                "integer minutes (registry: REG_SZ, NOT DWORD — 0 = \"no grace\" is a deliberate setting; DWORD 0/1 coerce to \"False\"/\"True\" and the int binder throws)"),
            Key("Entra:MaxDynamicTeams", "16", @"Entra\MaxDynamicTeams", RegistryValueType.RegSz,
                ControlKind.Number, OverridableState.Overridable,
                "integer count; 0 = no cap (registry: REG_SZ, NOT DWORD — 0 is a deliberate setting; DWORD 0/1 coerce to \"False\"/\"True\" and the int binder throws); each dynamic team costs one index.json fetch per sync cycle and one index parse per NewsViewer selection pass"),
            Key("Entra:GroupTeams:ExclusionGroup", "", @"Entra\GroupTeams\ExclusionGroup", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable,
                "Entra group id/name — fleet-wide kill switch suppressing every Entra:GroupTeams:Instances entry"),
            Key("Delivery:DefaultLockScreenPath", "", @"Delivery\DefaultLockScreenPath", RegistryValueType.RegSz,
                ControlKind.Path, OverridableState.Overridable,
                "absolute; SYSTEM-readable; warning: an empty REG_SZ is a PRESENT value — creating the registry value with empty data overrides the appsettings path with empty; no content + no default CLEARS a value NewsService itself published, leaves a foreign value alone"),
            Key("Delivery:DefaultWallpaperPath", "", @"Delivery\DefaultWallpaperPath", RegistryValueType.RegSz,
                ControlKind.Path, OverridableState.Overridable,
                "absolute; warning: an empty REG_SZ is a PRESENT value — creating the registry value with empty data overrides the appsettings path with empty; same teardown semantics as Delivery:DefaultLockScreenPath. Moved here from the NewsViewer hive — NewsService now owns the wallpaper image"),
            Key("Delivery:PublishedImagePath", @"C:\Windows\Web\NewsCentral", @"Delivery\PublishedImagePath", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable,
                "Protected folder for applied display images (both lock screen and wallpaper); must not be user-writable"),
            Key("Delivery:LockScreenEnabled", "true", @"Delivery\LockScreenEnabled", RegistryValueType.Dword,
                ControlKind.Toggle, OverridableState.Overridable,
                "true | false (registry: DWORD 0/1); false = the lock-screen surface is not read, written, or cleared at all — NOT a revert, existing content stays frozen; opt-out for RDS/VDI/RemoteApp hosts"),
            Key("Delivery:WallpaperEnabled", "true", @"Delivery\WallpaperEnabled", RegistryValueType.Dword,
                ControlKind.Toggle, OverridableState.Overridable,
                "true | false (registry: DWORD 0/1); mirrors Delivery:LockScreenEnabled exactly — false = the wallpaper surface is not read, written, or cleared at all; NOT a revert; opt-out for RDS/VDI/RemoteApp hosts"),
            Key("ViewerTasks:Manage", "true", @"ViewerTasks\Manage", RegistryValueType.Dword,
                ControlKind.Toggle, OverridableState.Overridable,
                "true | false (registry: DWORD 0/1 — genuine bool, DWORD is safe); true = NewsService registers, verifies and repairs the two NewsViewer scheduled tasks on every start and removes the legacy launch surface; false = completely hands-off (packaging owns the tasks; the Run key is not touched)"),
            Key("ViewerTasks:NewsViewerPath", "", @"ViewerTasks\NewsViewerPath", RegistryValueType.RegSz,
                ControlKind.Path, OverridableState.Overridable,
                "full path of NewsViewer.exe the tasks launch (registry: REG_SZ); empty = ..\\NewsViewer\\NewsViewer.exe next to the NewsService install folder; set it on a dev box to point at a Debug build; warning: an empty REG_SZ is a PRESENT value that overrides the appsettings path with empty"),
            Key("Telemetry:UploadEnabled", "true", @"Telemetry\UploadEnabled", RegistryValueType.Dword,
                ControlKind.Toggle, OverridableState.Overridable,
                "true | false (registry: DWORD 0/1 — genuine bool, DWORD is safe); false = session telemetry is not forwarded to the repository; the fixed 30-day local retention sweep still runs"),
            Key("Logging:LogLevel:Default", "Information", @"Logging\LogLevel\Default", RegistryValueType.RegSz,
                ControlKind.Dropdown, OverridableState.Overridable,
                "Trace | Debug | Information | Warning | Error | Critical | None (registry: REG_SZ); standard .NET logging key, honoured by the generic host",
                options: new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" },
                displayName: "Log level"),
            Key("Logging:EventLog:LogLevel:Default", "Information", @"Logging\EventLog\LogLevel\Default", RegistryValueType.RegSz,
                ControlKind.Dropdown, OverridableState.Overridable,
                "Trace | Debug | Information | Warning | Error | Critical | None (registry: REG_SZ); standard .NET logging key, honoured by the generic host",
                options: new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" },
                displayName: "EventLog log level"),
        }
    };

    public static readonly ComponentManifest NewsViewer = new()
    {
        ComponentName = "NewsViewer",
        HasTeamsHive = true,
        HasSigningHive = true,
        Keys = new[]
        {
            Key("Company", SolutionConstants.Company, null, RegistryValueType.None, ControlKind.Text,
                OverridableState.DefinesPath, "compile-time constant (SolutionConstants.Company); defines hive path"),
            Key("CacheRootPath", @"C:\ProgramData\NewsCentral", "CacheRootPath", RegistryValueType.RegSz,
                ControlKind.Path, OverridableState.Overridable, "absolute path"),
            Key("Active", "true", "Active", RegistryValueType.Dword,
                ControlKind.Toggle, OverridableState.Overridable,
                "true | false (registry: DWORD 0/1); false = NewsViewer exits at startup with no action"),
            Key("BypassDailyGate", "false", "BypassDailyGate", RegistryValueType.Dword,
                ControlKind.Toggle, OverridableState.Overridable, "true | false (registry: DWORD 0/1)"),
            Key("BypassImageIntegrityCheck", "false", "BypassImageIntegrityCheck", RegistryValueType.Dword,
                ControlKind.Toggle, OverridableState.Overridable, "true | false (registry: DWORD 0/1)"),
            Key("Hmac:SecretKey", "", @"Hmac\SecretKey", RegistryValueType.RegSz,
                ControlKind.Redacted, OverridableState.Overridable,
                "Base64, 32 bytes; warning: an empty REG_SZ is a PRESENT value — creating the registry value with empty data overrides the appsettings key with empty and disables HMAC",
                isSecret: true),
            Key("Signing:RequireSignedIndex", "false", @"Signing\RequireSignedIndex", RegistryValueType.RegSz,
                ControlKind.Toggle, OverridableState.Overridable,
                "true | false (registry: REG_SZ); ad-hoc read, not on POCO"),
            Key("Delivery:WallpaperStyle", "Fit", @"Delivery\WallpaperStyle", RegistryValueType.RegSz,
                ControlKind.Dropdown, OverridableState.Overridable, "Fill | Fit | Stretch | Center | Tile",
                options: new[] { "Fill", "Fit", "Stretch", "Center", "Tile" }),
            Key("Delivery:WallpaperBackgroundColor", "0 0 0", @"Delivery\WallpaperBackgroundColor", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable, "\"R G B\", each 0–255"),
            Key("Display:LogicalDayStartHour", "0", @"Display\LogicalDayStartHour", RegistryValueType.RegSz,
                ControlKind.Number, OverridableState.Overridable,
                "0–23 local (registry: REG_SZ, NOT DWORD — 0/1 coerce to \"False\"/\"True\"); out-of-range → 0"),
            Key("Display:ShowInRemoteSessions", "true", @"Display\ShowInRemoteSessions", RegistryValueType.Dword,
                ControlKind.Toggle, OverridableState.Overridable,
                "Show posters and notifications in remote sessions (RDP, Citrix, Horizon). false = previous behavior (silent exit, logged as RemoteSession)."),
            Key("Ui:ShowDeliveredImages", "false", @"Ui\ShowDeliveredImages", RegistryValueType.Dword,
                ControlKind.Toggle, OverridableState.Overridable,
                "true | false (registry: DWORD 0/1); true = the poster form shows Wallpaper / Lock screen buttons that display the image NewsService currently delivers (each enabled only when its Delivery:WallpaperEnabled / Delivery:LockScreenEnabled flag is on and the PersonalizationCSP file exists)"),
            Key("Ui:FrameColor", "", @"Ui\FrameColor", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable,
                "Colour of the poster window's side panel, title bar and border (#RRGGBB). Empty = theme."),
            Key("Ui:FrameTextColor", "", @"Ui\FrameTextColor", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable,
                "Text colour on those surfaces (#RRGGBB). Empty = automatic black/white by contrast when FrameColor is set, otherwise theme."),
            Key("Ui:NotifyNewContent", "true", @"Ui\NotifyNewContent", RegistryValueType.Dword,
                ControlKind.Toggle, OverridableState.Overridable,
                "true | false (registry: DWORD 0/1); true = show a 'News arrived!' notification when new content arrives after today's poster was shown. false = previous behavior (silent exit)."),
        }
    };

    public static readonly ComponentManifest NewsCentral = new()
    {
        ComponentName = "NewsCentral",
        HasTeamsHive = false,
        HasSigningHive = false,
        HasEnvironmentCatalog = true,
        Keys = new[]
        {
            Key("Company", SolutionConstants.Company, null, RegistryValueType.None, ControlKind.Text,
                OverridableState.DefinesPath, "compile-time constant (SolutionConstants.Company); defines hive path"),
            Key("DataPath", "", "DataPath", RegistryValueType.RegSz,
                ControlKind.Path, OverridableState.Overridable, "authoring data root"),
            Key("Authentication:EnableAutoLogin", "true", @"Authentication\EnableAutoLogin", RegistryValueType.RegSz,
                ControlKind.Toggle, OverridableState.Overridable,
                "true | false (registry: REG_SZ; DWORD also works for this boolean). Shows the UPN Login " +
                "button on the login page (sign-in with the Windows user's UPN) — no sign-in happens " +
                "automatically; the key name is historical"),
            Key("Authentication:UseMockUPN", "false", @"Authentication\UseMockUPN", RegistryValueType.RegSz,
                ControlKind.Toggle, OverridableState.Overridable,
                "true | false (registry: REG_SZ); Debug builds only — ignored in Release"),
            Key("Authentication:MockUPN", "", @"Authentication\MockUPN", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable,
                "UPN string; Debug builds only — ignored in Release"),
            Key("Storage:EnableBlobDistribution", "false", @"Storage\EnableBlobDistribution", RegistryValueType.RegSz,
                ControlKind.Toggle, OverridableState.Overridable, "true | false (registry: REG_SZ)"),
            Key("Storage:DistributionMode", "Local", @"Storage\DistributionMode", RegistryValueType.RegSz,
                ControlKind.Dropdown, OverridableState.Overridable, "Local | AzureBlob",
                options: new[] { "Local", "AzureBlob" }),
            Key("Storage:LocalDistributionPath", "", @"Storage\LocalDistributionPath", RegistryValueType.RegSz,
                ControlKind.Path, OverridableState.Overridable, "absolute; must differ from DataPath"),
            Key("Storage:AzureBlobContainerName", "newscentral", @"Storage\AzureBlobContainerName", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable, "container name"),
            Key("AzureBlob:TenantId", "", @"AzureBlob\TenantId", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable, "GUID"),
            Key("AzureBlob:ClientId", "", @"AzureBlob\ClientId", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable, "GUID"),
            Key("AzureBlob:AccountName", "", @"AzureBlob\AccountName", RegistryValueType.RegSz,
                ControlKind.Text, OverridableState.Overridable, "storage account, no suffix"),
            Key("Hmac:SecretKey", "", @"Hmac\SecretKey", RegistryValueType.RegSz,
                ControlKind.Redacted, OverridableState.Overridable,
                "Base64, 32 bytes; warning: an empty REG_SZ is a PRESENT value — creating the registry value with empty data overrides the appsettings key with empty and disables HMAC",
                isSecret: true),
            Key("AllowUserEnvironments", "true", "AllowUserEnvironments", RegistryValueType.Dword,
                ControlKind.Toggle, OverridableState.RegistryOnly,
                "Group Policy: allow operators to add their own environments (enforced from M4)"),
        }
    };

    /// <summary>All three manifests in fixed page order: NewsCentral → NewsService → NewsViewer (spec §3).</summary>
    public static readonly IReadOnlyList<ComponentManifest> All = new[] { NewsCentral, NewsService, NewsViewer };

    private static ConfigKeyDescriptor Key(
        string canonicalKey, string @default, string? subkey, RegistryValueType regType,
        ControlKind control, OverridableState ovr, string valueHint,
        bool isSecret = false, string[]? options = null, string? displayName = null,
        string? envVar = null) => new()
    {
        CanonicalKey = canonicalKey,
        DisplayName = displayName ?? Leaf(canonicalKey),
        RegistrySubkeyPath = subkey,
        RegistryType = regType,
        Default = @default,
        ControlKind = control,
        Options = options ?? System.Array.Empty<string>(),
        IsSecret = isSecret,
        OverridableState = ovr,
        EnvironmentVariableName = envVar,
        ValueHint = valueHint
    };

    private static string Leaf(string key)
    {
        var i = key.LastIndexOf(':');
        return i < 0 ? key : key[(i + 1)..];
    }
}
