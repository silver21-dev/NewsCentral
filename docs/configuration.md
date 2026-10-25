# Configuration Model

> For the anti-tamper-relevant subset (signing keys, `RequireSignedIndex`, image-integrity bypass, HMAC key) consolidated into one table with defaults and effects, see `docs/anti-tamper.md`.

## Registry Hive

All registry-configurable values reside under:

```
HKLM\Software\[Company]\[NewsCentral]\
```

`[Company]` is **not configuration** — it is a single **build-time constant** authored once in `Directory.Build.props` (`<Company>`) and surfaced to code as `SolutionConstants.Company` (generated into `NewsCentral.Shared`), identical across all three components. It is **absent from every `appsettings.json`**, has **no runtime default and no fallback**, and an MSBuild target (`Directory.Build.targets`) fails the build if it is empty, so it cannot ship unset. It is **not** registry-overridable: it *defines* the hive path and cannot be read from the path it defines. A `Company` **mismatch fails silently** — `RegistryConfigurationProvider`'s `OpenSubKey` returns `null` with no error, so every registry/GPO override is quietly ignored and the component runs on its shipped defaults. That silent-drift risk is exactly why `Company` is a single source of truth (the packaging MSI reads the same `Directory.Build.props` value for the scheduled-task path `\{Company}\NewsCentral\NewsViewer`). The `[NewsCentral]` solution segment is likewise **not** configuration — it is the fixed constant `SolutionConstants.SolutionName` (in `NewsCentral.Shared`, namespace `NewsCentral.Configuration`).

## Precedence Rule

Registry values override `appsettings.json` values. Override is implemented via `RegistryConfigurationProvider` (in `NewsCentral.Shared`) wired into all components through `IConfigurationBuilder.AddRegistryOverrides(company, "NewsCentral", componentName)`. Any `appsettings.json` key can be overridden by mirroring the JSON section hierarchy as registry subkeys.

## Registry Layout

Each component reads from its own subkey. Values set for one component do not affect another. (One read-only exception: NewsViewer with `Ui\ShowDeliveredImages` also reads `NewsService\Delivery\WallpaperEnabled`/`LockScreenEnabled`; see `docs/newsviewer-spec.md`.)

**NewsService**
```
HKLM\Software\[Company]\NewsCentral\NewsService\
├── Service\
│       PollIntervalSeconds   REG_SZ    (int poll interval in seconds; MUST be REG_SZ — DWORD 0/1 coerce to "False"/"True" and the int binder throws)
│       CacheRootPath         REG_SZ    (overrides Service:CacheRootPath)
├── Repository\
│       StorageMode   REG_SZ   ("Share" or "Azure")
│       SharePath     REG_SZ   (UNC or local path to the file-share repository)
├── AzureBlob\
│       AuthMode               REG_SZ   ("Certificate", "ClientSecret", or "ClientSecretEnv")
│       TenantId               REG_SZ
│       ClientId               REG_SZ
│       AccountName            REG_SZ
│       ContainerName          REG_SZ
│       CertificateThumbprint  REG_SZ
│       ClientSecret           REG_SZ
│       UseWinHttpProxy        REG_SZ   "true" or "false"  (default false; route blob+Graph via machine WinHTTP proxy)
├── Hmac\
│       SecretKey   REG_SZ    (Base64-encoded 32-byte key; empty = HMAC disabled)
├── Signing\
│       RequireSignedIndex  REG_SZ    "true" or "false"  (default false; when true, Unsigned/Disabled indexes are rejected)
│       <teamFolderName>\
│           PublicKey          REG_SZ   (Base64 SubjectPublicKeyInfo — ECDSA index verification)
│           PublicKeyPrevious  REG_SZ   (Base64 SPKI; optional — rotation window)
│       (one subtree per team; surfaced as Signing:{teamFolderName}:PublicKey via RegistryConfigurationProvider)
├── Entra\
│       Enabled              REG_SZ    "true" or "false"  (default false; gates the whole feature)
│       GracePeriodMinutes   REG_SZ    (int retention window in minutes for the last resolved team while Graph is unreachable; default 240; MUST be REG_SZ — 0 = "no grace" is deliberate, and DWORD 0/1 coerce to "False"/"True" and the int binder throws)
│       MaxDynamicTeams      REG_SZ    (int cap on total dynamic teams written across both sources; 0 = no cap; default 16; MUST be REG_SZ — DWORD 0/1 coerce to "False"/"True" and the int binder throws)
│       AttributeSchemes\
│           <schemeName>\
│               Selector   REG_SZ   "extensionAttribute1"
│               Mappings\
│                   FAT   REG_SZ   "extensionAttribute2-extensionAttribute5-extensionAttribute4"
│       GroupTeams\
│           ExclusionGroup   REG_SZ   (fleet-wide kill switch; empty = none)
│           Instances\
│               <label>\
│                   InclusionGroup   REG_SZ   (required; activates the instance)
│                   ExclusionGroup   REG_SZ   (optional, per-instance)
│       (one attribute scheme per <schemeName>, one group instance per <label>; both multi-instance —
│        see docs/entra-dynamic-teams.md; surfaced via the same recursive registry walk)
├── Delivery\
│       DefaultLockScreenPath   REG_SZ   (absolute, SYSTEM-readable path to a default lock-screen image; "" = no default)
│       DefaultWallpaperPath    REG_SZ   (absolute path to a default wallpaper image; "" = no default; moved here from NewsViewer)
│       PublishedImagePath     REG_SZ    (protected folder for applied display images, both surfaces; must not be user-writable; default C:\Windows\Web\NewsCentral)
│       LockScreenEnabled      DWORD     (0 = the lock-screen surface is not read/written/cleared at all — NOT a revert; default 1)
│       WallpaperEnabled       DWORD     (0 = the wallpaper surface is not read/written/cleared at all — NOT a revert; mirrors LockScreenEnabled; default 1)
├── ViewerTasks\
│       Mode            REG_SZ  ("VerifyOnly" or "Off" - an enum, never a DWORD; default VerifyOnly)
│       RegisterEventSource REG_SZ "true" or "false" (default "true"; false = NewsService never creates the EventLog source and never writes to a missing one)
│       NewsViewerPath  REG_SZ  (full path of the NewsViewer.exe the tasks launch; empty/absent = %ProgramFiles%\{Company}\NewsCentral\NewsViewer\NewsViewer.exe)
├── Telemetry\
│       UploadEnabled   DWORD   (0 = do not forward session telemetry to the repository; the fixed 30-day local retention sweep still runs; default 1)
├── Logging\
│   ├── LogLevel\
│   │       Default   REG_SZ   ("Trace" | "Debug" | "Information" | "Warning" | "Error" | "Critical" | "None"; default Information)
│   └── EventLog\
│       └── LogLevel\
│               Default   REG_SZ   (same values; EventLog provider only; default Information)
└── teams\
        (one REG_SZ value per team; value name = team folder name; data = "")
        e.g.  cz-its   REG_SZ   ""
              de-prod  REG_SZ   ""
```

**NewsViewer**
```
HKLM\Software\[Company]\NewsCentral\NewsViewer\
│   CacheRootPath                REG_SZ    (overrides ViewerConfiguration.CacheRootPath)
│   Active                       DWORD     (0 = NewsViewer exits at startup with no action; default 1)
│   BypassDailyGate             DWORD     (1 = skip once-per-day gate at startup)
│   BypassImageIntegrityCheck    DWORD     (1 = skip image SHA-256 verification)
├── Display\
│       LogicalDayStartHour  REG_SZ   (int 0..23; default 0 = calendar day — see below; MUST be REG_SZ)
│       ShowInRemoteSessions  REG_DWORD   (0/1; default 1 - show posters and notifications in remote sessions: RDP, Citrix, Horizon; 0 = silent exit logged as RemoteSession; the wallpaper style re-assert never runs in a remote session)
├── Ui\
│       Theme   REG_SZ   ("Dark" | "Light"; default Dark — viewer color theme, resolved once at launch; "light" case-insensitive selects Light, anything else Dark; registry-only, no appsettings key)
│       ShowDeliveredImages   REG_DWORD   (0/1; default 0 - poster form offers Wallpaper / Lock screen buttons for the image NewsService delivers; read-only reads of NewsService\Delivery flags are an exception to the own-subkey rule - see docs/newsviewer-spec.md)
│       NotifyNewContent   REG_DWORD   (0/1; default 1 - show a "News arrived!" notification when new content arrives after today's poster was shown; 0 = silent exit as before)
│       FrameColor   REG_SZ   (#RRGGBB; default empty = theme - colour of the poster window side panel, title bar and outer border; invalid = theme, noted in launch.log)
│       FrameTextColor   REG_SZ   (#RRGGBB; default empty = automatic black/white by contrast once FrameColor is set; ignored without FrameColor)
│       AllowedDomains   REG_SZ   (domains the poster's More information link may open, ';'-separated; default empty = the button is disabled for every link)
├── Hmac\
│       SecretKey   REG_SZ
├── Signing\
│       RequireSignedIndex  REG_SZ    "true" or "false"  (default false; when true, Unsigned/Disabled indexes are rejected)
│       <teamFolderName>\
│           PublicKey          REG_SZ   (Base64 SubjectPublicKeyInfo — ECDSA index verification)
│           PublicKeyPrevious  REG_SZ   (Base64 SPKI; optional — rotation window)
│       (one subtree per team; surfaced as Signing:{teamFolderName}:PublicKey via RegistryConfigurationProvider)
├── Delivery\
│       WallpaperStyle            REG_SZ   (Fill | Fit | Stretch | Center | Tile; default Fit — style only, the image is NewsService/CSP-owned)
│       WallpaperBackgroundColor  REG_SZ   ("R G B" desktop background for Fit letterbox bars; default "0 0 0")
└── teams\
        (one REG_SZ value per team; value name = full folder name)
```

**NewsCentral** (MAUI authoring app)
```
HKLM\Software\[Company]\NewsCentral\NewsCentral\
│   DataPath               REG_SZ    (root for IStorageService)
├── Authentication\
│       EnableAutoLogin   REG_SZ    "true" or "false" (DWORD also works for this boolean); shows the
│                                   UPN Login button on the login page — no sign-in happens
│                                   automatically; the key name is historical; default true
│       EnableLocalAccounts DWORD   (0/1; default 0/false — local username/password accounts are opt-in;
│                                   false omits the Login button and Username/Password fields from the
│                                   login page and AuthenticationService.LoginAsync refuses local logon
│                                   with a Warning log — enforced server-side, not only in the UI)
│       UseMockUPN        DWORD     (Debug builds only — ignored in Release; not for registry deployment)
│       MockUPN           REG_SZ    (Debug builds only — ignored in Release; not for registry deployment)
├── Audit\
│       Enabled           DWORD     (0/1; policy override for the audit log. When PRESENT it wins over the
│                                   environment's own audit.enabled and the Environment Management toggle is
│                                   shown locked "set by policy". Absent = the environment setting, default on.
│                                   Set with Set-RegistryOverrides.ps1 -AuditEnabled)
├── Storage\
│       EnableBlobDistribution   DWORD
│       DistributionMode         REG_SZ   ("Local" or "AzureBlob")
│       LocalDistributionPath    REG_SZ
│       AzureBlobContainerName   REG_SZ
├── AzureBlob\
│       TenantId      REG_SZ
│       ClientId      REG_SZ
│       AccountName   REG_SZ
├── Hmac\
│       SecretKey   REG_SZ
├── AllowUserEnvironments   DWORD   (0/1; default 1 when absent; Group Policy — allow operators to add their own environments; read and displayed only — M4 enforces it)
├── Environments\
│   └── <Name>\
│           DataPath                       REG_SZ   (required; the rest are optional per-environment overrides)
│           DisplayName                    REG_SZ
│       ├── Storage\
│       │       EnableBlobDistribution     REG_SZ   "true" or "false"
│       │       DistributionMode           REG_SZ   ("Local" or "AzureBlob")
│       │       LocalDistributionPath      REG_SZ
│       │       AzureBlobContainerName     REG_SZ
│       └── AzureBlob\
│               TenantId      REG_SZ
│               ClientId      REG_SZ
│               AccountName   REG_SZ
│       (one subtree per policy-defined environment; <Name> IS the entry's identity — unlike the
│        Entra GroupTeams instance label, it is never derived from anything else)
└── teams\
        (one REG_SZ value per team)
```

**`LockExpirationMinutes` was removed (DOCS-1).** It was defined in appsettings/registry but read
by nothing — no lock-expiration behavior ever existed. A leftover `LockExpirationMinutes` value
under either layer is now simply ignored.

Precedence per field: the M3a result (environment.json if present and valid, else this machine's
own `Storage:*`/`AzureBlob:*` values) is overlaid field-by-field with whatever the matching policy
entry sets — an `Invalid` environment.json is never rescued by policy. A matching entry's canonical
DataPath (`Path.GetFullPath`, trailing separator trimmed, case-insensitive) must equal the current
`EnvironmentContext.DataPath`.

**Implicit "Default" entry.** If the flat `DataPath` value is set directly under
`HKLM\Software\{Company}\NewsCentral\NewsCentral\` (not in `appsettings.json` — appsettings alone
never creates a policy entry), the flat `DataPath`, `Storage\*` and `AzureBlob\*` values form an
implicit entry named `Default`. An explicit `Environments\Default` subkey, if present, takes
precedence over the implicit one (a warning is recorded when both exist). Duplicate DataPaths
across entries: the first entry in name order wins, with a warning for every entry it shadows.

**NewsTester** (future — stub, no values defined yet)
```
HKLM\Software\[Company]\NewsCentral\NewsTester\
    (reserved; no values written by current tooling)
```

**DWORD mapping:** `0` → `"False"`, `1` → `"True"`, values > 1 → numeric string. The configuration binder selects the correct interpretation from the target POCO property type. **REG_SZ** values are stored as-is. All other registry value types are ignored.

**`teams\` sub-hive:** team folder names are the value *names* (not the value data). Value names must match the generated folder name exactly — the sanitized team name with no `team-` prefix (e.g. `cz-its`, not `CZ_ITS`). The provider exposes them as `teams:0`, `teams:1`, … so `IConfiguration.GetSection("teams").GetChildren()` returns one entry per team. Use `TeamConfigurationReader.GetTeams(configuration)` to read them.

**`Signing\` sub-hive:** contains `RequireSignedIndex` (REG_SZ `"true"`/`"false"`, default `"false"`) plus one subkey per team. Each team subkey holds `PublicKey` and (optionally) `PublicKeyPrevious` as `REG_SZ` values containing Base64-encoded SubjectPublicKeyInfo. `RegistryConfigurationProvider` surfaces all values via its recursive `WalkKey` traversal: `Signing:RequireSignedIndex`, `Signing:{teamFolderName}:PublicKey`, and `Signing:{teamFolderName}:PublicKeyPrevious`. Use `SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolderName)` to read the key list; use `configuration.GetValue<bool>("Signing:RequireSignedIndex")` to read the flag. When `RequireSignedIndex` is `true`, `SignatureGate.ShouldReject` rejects Unsigned and Disabled indexes in addition to Invalid — enabling fail-closed enforcement. NewsService and NewsViewer each maintain their own separate `Signing\` subtrees.

## environment.json — secure store, audit log (NewsCentral)

`{DataPath}\config\environment.json` (per environment, edited on Environment Management by a system
administrator) carries three settings beyond distribution:

| Field | Default | Meaning |
|---|---|---|
| `securePath` | `{DataPath}\secure` | Root of the secure key store. Team signing keys live at `{securePath}\keys\{team}\team-signing.json`. Must be an absolute path. Changing it does **not** move existing keys. An environment without `environment.json` uses the default. |
| `auditPath` | `{DataPath}\audit` | Root of the audit log (`{auditPath}\{yyyy-MM}\{yyyy-MM-dd}_{MACHINE}_{windowsUser}.jsonl`). Must be an absolute path. |
| `audit.enabled` | `true` | Whether events are recorded. The registry value `Audit\Enabled` (DWORD) overrides it; when the registry value is present the Environment Management toggle is locked ("set by policy"). |

NTFS permissions on these folders (and on `config\users.json`) are applied by `scripts/Set-AuthoringAcl.ps1`,
never by the app; the app only warns about drift. See `docs/anti-tamper.md` §9 and `docs/production-deployment.md`.

## appsettings.json — NewsCentral

`Company` is deliberately **absent** — it is the build-time constant `SolutionConstants.Company`, not an appsettings key (see the Registry Hive note above).

```json
{
  "DataPath": "C:\\Download\\NewsCentral",
  "Authentication": {
    "EnableAutoLogin": true,
    "EnableLocalAccounts": false,
    "UseMockUPN": true,
    "MockUPN": "user@company.com"
  },
  "Storage": {
    "EnableBlobDistribution": true,
    "DistributionMode": "Local",
    "LocalDistributionPath": "C:\\Download\\NewsCentralDist",
    "AzureBlobContainerName": "newscentral"
  },
  "AzureBlob": {
    "TenantId": "",
    "ClientId": "",
    "AccountName": ""
  },
  "Hmac": {
    "SecretKey": ""
  }
}
```

NewsCentral authenticates to Azure using an **interactive MSAL user session** (`InteractiveBrowserCredential`) — no service credentials are stored in config. Token is persisted in a named cache (`"NewsCentral"`) so subsequent calls are non-interactive.

**No seeded first admin (M5b).** There is no `Initialization:*` configuration any more, and no admin/admin account is ever created automatically. A brand-new environment (no `config/users.json` yet) is initialized exclusively through the **setup wizard** — from the login page for a Policy or Configured environment, or from Environment Management's "Initialize an environment" section for any environment while signed in to another one — where the operator running the wizard chooses the first System Administrator's own username and password. See `docs/newscentral-spec.md`.

## appsettings.json — NewsService

NewsService resolves configuration across the **same three layers as NewsViewer** (registry always wins):

| Layer | File / source | Status |
|---|---|---|
| Base | `appsettings.json` | **Shipped, neutral, committed** — a tracked artifact (never gitignored). Every key explicit. This is the base layer the installer lays down; GPO overrides sit on top of it. |
| Dev overlay | `appsettings.Development.json` | **Optional, gitignored, dev-only** — added explicitly in `Program.cs` with `optional: true`; its mere presence activates it (no environment variable — the host runs as Production, so the built-in `appsettings.{Environment}.json` mechanism would never load it). **Never published** (`CopyToPublishDirectory=Never`), so nothing in it can reach the fleet. Documented shape: `appsettings.Development.json.example`. |
| Override | Registry (GPO) | **Always wins** — `HKLM\Software\{Company}\NewsCentral\NewsService`. The registry provider is the last configuration source. |

`appsettings.json` is a committed base artifact, not a hand-maintained local file; GPO sits on top of it.

> **M6 — generating this surface.** NewsCentral's Environment Management page's **Export fleet
> settings** button generates the `Repository\`/`AzureBlob\` and `Signing\` values below for the
> current environment (a `Set-RegistryOverrides.ps1` invocation and a `.reg` file); see
> `docs/production-deployment.md` Step 7.

> ⚠️ **Configuration is bound once at host start — verified on Windows 11 Enterprise.** `Program.cs` reads `appsettings.json`, the dev overlay, and the registry once into a single `ServiceConfiguration` object when the process starts. Every subsequent poll cycle re-evaluates **content and schedules** — it never re-reads configuration. A registry change to any key (`Delivery:LockScreenEnabled`/`WallpaperEnabled` included) has **no effect until NewsService is restarted**, however many poll cycles pass in between. This catches people out specifically with the toggles: pushing a GPO change and expecting it to take effect on the next cycle does not work — restart the service (or wait for its next scheduled restart/reboot) after any registry change.

> ⚠️ **Worker-SDK trap — the overlay exclusion must use `<Content Update>`, not `<Content Include>`.** NewsService uses `Microsoft.NET.Sdk.Worker`, whose **default content items already glob `appsettings.json` AND `appsettings.*.json` — and those defaults PUBLISH**. The overlay is therefore suppressed in `NewsService.csproj` with `<Content Update="appsettings.Development.json">` carrying `CopyToPublishDirectory=Never`; a `<Content Include>` would double-include the SDK's own glob item and would **not** suppress publishing. Without the `Update`, a developer's `appsettings.Development.json` — personal share paths and all — would ship to the fleet automatically. NewsViewer uses plain `Microsoft.NET.Sdk`, which has no such glob, which is why **its** csproj uses `Include` instead.

```json
{
  "Service": {
    "PollIntervalSeconds": 60,
    "CacheRootPath": "C:\\ProgramData\\NewsCentral"
  },
  "Repository": {
    "StorageMode": "Share",
    "SharePath": ""
  },
  "AzureBlob": {
    "AuthMode": "Certificate",
    "TenantId": "",
    "ClientId": "",
    "CertificateThumbprint": "",
    "ClientSecret": "",
    "AccountName": "",
    "ContainerName": "newscentral",
    "UseWinHttpProxy": false
  },
  "Hmac": {
    "SecretKey": ""
  },
  "Signing": {
    "RequireSignedIndex": false
  },
  "Entra": {
    "Enabled": false,
    "GracePeriodMinutes": 240,
    "MaxDynamicTeams": 16,
    "AttributeSchemes": {},
    "GroupTeams": {
      "ExclusionGroup": "",
      "Instances": {}
    }
  },
  "Delivery": {
    "DefaultLockScreenPath": "",
    "DefaultWallpaperPath": "",
    "PublishedImagePath": "C:\\Windows\\Web\\NewsCentral",
    "LockScreenEnabled": true,
    "WallpaperEnabled": true
  },
  "Telemetry": {
    "UploadEnabled": true
  },
  "ViewerTasks": {
    "Mode": "VerifyOnly",
    "RegisterEventSource": true,
    "NewsViewerPath": ""
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    },
    "EventLog": {
      "SourceName": "NewsService",
      "LogName": "Application",
      "LogLevel": {
        "Default": "Information"
      }
    }
  }
}
```

`Logging:EventLog:SourceName` / `LogName` name the **same EventLog source the installer registers** (`NewsService` in the Application log — see `docs/packaging.md`); the two must stay in step, so do not edit either side alone.

`Delivery:DefaultLockScreenPath` — absolute, machine-readable (SYSTEM-readable in the pre-logon context) path to a default lock-screen image applied when no lock-screen content is active. Empty (`""`) means no default. **This is no longer unconditionally "sticky"**: when there is no active content and no usable default, a lock-screen value NewsService itself previously published is **cleared**, returning the machine to Windows' own default lock screen at the next lock; only a value NewsService did **not** write (GPO, Intune, a manual admin change) is left in place. See `docs/newsservice-spec.md` → Step 2. NewsService-only.

`Delivery:DefaultWallpaperPath` — absolute path to a default wallpaper image applied when no active `IsWallpaper` content exists. Empty (`""`) means no default. Same semantics as `DefaultLockScreenPath` in every respect, including the teardown behavior above. **This key lives in the NewsService hive, not NewsViewer's** — it moved here when wallpaper-**image** ownership migrated to NewsService; NewsViewer retains only the wallpaper **style** keys (`WallpaperStyle`, `WallpaperBackgroundColor`, below). NewsService-only.

`Delivery:PublishedImagePath` (default `C:\Windows\Web\NewsCentral`) — the protected folder `ImagePublisher` copies the applied image into, for **both** surfaces (each in its own content-derived namespace: `lockscreen-*` / `wallpaper-*`), before `PersonalizationCSP` is pointed at it, instead of pointing CSP directly at the `%ProgramData%` cache. The published file name is content-derived (`{prefix}-{hash16}.ext`), and the SHA-256 recorded in the signed index (or, for a configured default image, the file's own hash) is re-verified at publish time. **This folder must not be writable by standard users** — that is the entire point of publishing here rather than applying from the cache; see `docs/anti-tamper.md` for the threat this closes. This value also gates the **teardown** ownership test (Step 2), shared by both surfaces: a live CSP value is only ever cleared when it resolves to a path inside this folder — an empty or unresolvable value makes that test fail closed (nothing is ever cleared). **Supported location: `%SystemRoot%\Web\NewsCentral`.** It inherits `%SystemRoot%\Web`'s ACL (administrators and SYSTEM full control, Users read-only), which is what makes it safe. A path anywhere else inherits from its parent, and `C:\ProgramData\...` grants standard users the right to create files and folders, so a path elsewhere needs an **explicit ACL set by the installer** (administrators and SYSTEM write, everyone else read) - NewsService never applies an ACL itself (it verifies and reports; the installer owns the folder's ACL). NewsService checks the folder at startup and before every publish and sweep: if `Users`, `Everyone`, `Authenticated Users` or `INTERACTIVE` holds an effective write right (create files, append, write attributes/extended attributes, delete, delete children, change permissions, take ownership, or a generic-write/all bit; a matching Deny for the same identity cancels what it covers; read-only rights such as `ReadAndExecute` do not count) it logs one `Error` naming the path and the identity, **refuses to publish or sweep**, and leaves the previous state alone. The surfaces then keep what they have (fail closed). The error is logged once per distinct finding, and once when the folder is fixed, at which point publishing resumes.

`Delivery:LockScreenEnabled` (bool, default `true`) — master enable for the entire lock-screen surface. `false` stops NewsService reading, writing, or clearing **any** `PersonalizationCSP` lock-screen value that cycle — the surface is left exactly as it stands. **This is not a revert**: a machine that already has lock-screen content applied keeps it frozen in place until the values are cleared by hand, or the toggle is turned back on so the ordinary teardown path can clear it. Provision this **before** first run on machines where the surface should never be managed — see the session-host guidance below. NewsService-only.

`Delivery:WallpaperEnabled` (bool, default `true`) — master enable for the entire desktop-wallpaper surface. **Live** — mirrors `LockScreenEnabled` exactly (same NOT-a-revert semantics, same session-host rationale). NewsService owns the wallpaper image via PersonalizationCSP; NewsViewer's per-user HKCU style re-assert is unaffected by this toggle either way (see `docs/newsviewer-spec.md`). NewsService-only.

> **Prerequisite — Windows Enterprise or Education.** Personalization CSP — the mechanism behind both the lock-screen and wallpaper applies — is documented by Microsoft as supported on Windows Enterprise and Education SKUs, and on Pro only under Shared PC / Cloud Config (BootToCloud) configurations. The raw registry writes NewsService performs are widely observed to work on Pro outside those configurations too, but that remains undocumented behavior — do not rely on it for a production fleet running Pro.

> **Session-host guidance.** `LockScreenEnabled` and `WallpaperEnabled` both exist primarily as an opt-out for **RDS session hosts, VDI templates, and RemoteApp hosts**: one machine-wide registry value cannot correctly serve many concurrent user sessions, RDS policy can suppress the desktop background outright, and a non-persistent VDI image rebuilds from its template every boot, making any applied state meaningless past the next reboot. Set both to `0` in the GPO baseline for such machines. NewsService also logs a best-effort startup `Warning` (see `WarnIfSessionHostSurfaceUnmanaged` in `Program.cs`) when a machine looks like a Windows Server with Remote Desktop connections allowed and either toggle is still at its default — a heuristic, not a guarantee; it never fires on a client Windows workstation but is not a substitute for provisioning the toggles correctly.

> **Prerequisite — Windows Spotlight.** Windows Spotlight must be disabled via GPO or Intune (`HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent` → `DisableWindowsSpotlightFeatures = 1`, `DisableSpotlightCollectionOnDesktop = 1`) or it will intermittently override the CSP-applied lock screen. NewsCentral does not write these keys — they are a GPO/Intune deployment prerequisite, not something either component configures.

`Telemetry:UploadEnabled` (bool, default `true`) — when `false`, NewsService does not forward `session-*.json` telemetry to the repository (a read-only deployment: consume content, write nothing back). NewsViewer is unaffected and always writes session files; NewsService alone decides what becomes of them, so re-enabling upload immediately flushes whatever is still inside the retention window. Independently of this setting, local session files older than a **fixed 30-day window** (`TelemetryDefaults.RetentionDays`, by file `LastWriteTime`) are deleted every cycle — deliberately a constant, not a config key: the rationale is data hygiene (the records describe what a specific user saw and when), not disk space, and a configurable `0` would be ambiguous between delete-everything and keep-forever. Registry override: `Telemetry\UploadEnabled` DWORD `0`/`1`. See `docs/newsservice-spec.md` → Step 4.

`ViewerTasks` - the NewsViewer launch surface that NewsService **verifies and reports on, and never creates or repairs**: the installer owns scheduled-task creation and removal (`docs/packaging.md`; behaviour in `docs/newsservice-spec.md` -> NewsViewer launch-surface verification). `Mode` (enum, default `VerifyOnly`; registry `ViewerTasks\Mode` **REG_SZ** - the text `VerifyOnly` or `Off`, case-insensitive, **never a DWORD**): `VerifyOnly` = verify both tasks (once per hour) and log drift at `Warning`; `Off` = do nothing. An absent or unrecognised value runs as `VerifyOnly`, and an unrecognised one also logs one `Error` naming the value and the valid set - a typo never switches the check off. The former `ViewerTasks\Manage` DWORD no longer exists: if it is still present (dev and pilot machines have it) it is ignored and one `Warning` at startup names `ViewerTasks:Mode` as its replacement. `RegisterEventSource` (bool, default `true`; registry **REG_SZ** `"true"`/`"false"`, like the other tri-state booleans) - `true`: at startup, before anything writes through the source, NewsService creates the EventLog source (`Logging:EventLog:SourceName` in `Logging:EventLog:LogName`) when it is missing, so the existence of the source does not depend on `Logging:EventLog:LogLevel`; `false`: it never creates the source and never writes to a missing one, and warns once at startup that the Content Arrived task will not fire until the source is registered externally. `NewsViewerPath` (string, default empty; registry `ViewerTasks\NewsViewerPath` **REG_SZ**) - full path of the `NewsViewer.exe` the tasks launch; empty = the deployed layout `%ProgramFiles%\{Company}\NewsCentral\NewsViewer\NewsViewer.exe` (token left unexpanded; there is no service-relative default, so the verifier and the generated script cannot disagree); a non-empty value is used as given (dev boxes: the Debug build). The Configuration Review page also uses it to locate NewsViewer's install folder. The event log name and source the Content Arrived task subscribes to are the existing `Logging:EventLog:LogName` / `SourceName`. NewsService-only; bound once at host start like every other key. The registration script is a build artifact (`NewsService.exe --emit-task-script <path>`), not configuration.

## appsettings.json — NewsViewer

NewsViewer resolves configuration across three layers (registry always wins):

| Layer | File / source | Status |
|---|---|---|
| Base | `appsettings.json` | **Shipped, neutral, committed** — a tracked artifact (never gitignored). Carries the shipped neutral values only: a key it does not list takes its **code default** (listed with each key below); the registry is the fleet mechanism, and this file is not edited to add settings. Loaded with `optional: false` (a missing file is a hard startup failure). This is the base layer the MSI installs. |
| Dev overlay | `appsettings.Development.json` | **Optional, gitignored, dev-only** — loaded with `optional: true`; its mere presence activates it (no environment variable). **Never published** (`CopyToPublishDirectory=Never`), so nothing in it can reach the fleet. Documented shape: `appsettings.Development.json.example`. |
| Override | Registry (GPO) | **Always wins** — `HKLM\Software\{Company}\NewsCentral\NewsViewer`. |

`appsettings.json` is a committed base artifact, not a hand-maintained local file; GPO sits on top of it. `Company` is **not** present in any layer's file — it is the build-time constant `SolutionConstants.Company` (see the Registry Hive note above).

> **M6 — generating this surface.** The `Signing\` values below (NewsViewer has no `Repository\`/
> `AzureBlob\` of its own) are generated the same way, by the same **Export fleet settings** button
> — see `docs/production-deployment.md` Step 7.

```json
{
  "Active": true,
  "CacheRootPath": "C:\\ProgramData\\NewsCentral",
  "BypassDailyGate": false,
  "BypassImageIntegrityCheck": false,
  "Display": {
    "LogicalDayStartHour": 0
  },
  "Hmac": {
    "SecretKey": ""
  },
  "Signing": {
    "RequireSignedIndex": false
  },
  "Delivery": {
    "WallpaperStyle": "Fit",
    "WallpaperBackgroundColor": "0 0 0"
  }
}
```

The shipped template above lists only part of the NewsViewer surface. The remaining keys take their **code
default** when absent; as a documentation snippet (not part of any shipped file), the defaults of the keys it omits are:

```json
{
  "Display": {
    "LogicalDayStartHour": 0,
    "ShowInRemoteSessions": true
  },
  "Ui": {
    "ShowDeliveredImages": false,
    "NotifyNewContent": true,
    "FrameColor": "",
    "FrameTextColor": "",
    "AllowedDomains": ""
  }
}
```

(`Ui:Theme` is registry-only and has no appsettings key. Every one of these is set through the registry — see
the tree above and `Set-RegistryOverrides.ps1` — and each key is described below with its default.)

`Active` (NewsViewer) — per-machine master switch. `bool`, default `true` (registry `Active` DWORD `0`/`1` at the hive root; absent = `true`, so an unprovisioned machine is never silently disabled). Evaluated first in `Main()`, before every other startup guard. `false` exits at startup with no action taken: no poster is shown, no wallpaper style re-assert runs, `viewerstate.json` is not written, no telemetry session file is written, and no dialog is shown. The current wallpaper style is deliberately left as-is — deactivation is not a revert. (The wallpaper *image* is NewsService's concern and is unaffected by NewsViewer's `Active` switch in either direction.)

`Display:ShowInRemoteSessions` (NewsViewer) — `bool`, default `true` (registry `Display\ShowInRemoteSessions` DWORD 0/1; genuine boolean, so DWORD is correct). When `true`, posters and notifications are shown in remote sessions (RDP, Citrix, VMware Horizon). `false` restores the previous behavior: NewsViewer exits silently, logged as `reason=RemoteSession` in `launch.log`. `--manual` ignores it. Independently of this value, the HKCU wallpaper style re-assert never runs in a remote session, and a poster that wants a Virtual Desktop is shown on the standard desktop there. See `docs/newsviewer-spec.md` ("Remote sessions").

`Ui:AllowedDomains` (NewsViewer) - `string`, default empty (registry `Ui\AllowedDomains`, **REG_SZ**; text). Domains the poster's **More information** button may open, separated by `;` (`,` also works), e.g. `contoso.com;intranet.contoso.net`. Per entry: whitespace trimmed, a leading `*.` or `.` and a trailing `.` removed, converted to punycode, lower-cased; entries containing `/ : @ ? #`, whitespace or a stray `*` are ignored and noted once per run in `launch.log` (`AllowedDomainsInvalidEntry`). Only `https` links without user information qualify, and the host must equal an entry or be a subdomain of it (`contoso.com` allows `intranet.contoso.com`, not `evilcontoso.com`). **Default empty = the button is disabled for every link**, so deliver it with (or before) the NewsViewer build that enforces it. Rules, log reasons and tooltip: `docs/newsviewer-spec.md`.

`Ui:FrameColor` / `Ui:FrameTextColor` (NewsViewer) — `string`, default empty (registry `Ui\FrameColor` / `Ui\FrameTextColor`, **REG_SZ** `#RRGGBB`; these are text, so REG_SZ is the right type). `FrameColor` brands the poster window's side panel, title bar and outer border fleet-wide; empty = the theme. `FrameTextColor` is the text colour on those surfaces; empty = black or white by contrast once `FrameColor` is set (ignored without it). An invalid value falls back (invalid `FrameColor` → the whole theme; invalid `FrameTextColor` → automatic contrast) and is noted in `launch.log`. The "News arrived!" notification form and the Online pill follow it too (the pill keeps its green/red status dot); the Close button and the accent colours are not affected. Surfaces, derivation rules and examples: `docs/newsviewer-spec.md` ("Poster frame colours"); set with `Set-RegistryOverrides.ps1 -FrameColor '#1E3A5F' [-FrameTextColor '#FFD700']` (an empty string removes the value).

`Display:LogicalDayStartHour` (NewsViewer) — the hour (local time) at which the "logical day" for the once-per-day poster gate rolls over. `int`, default `0` (= calendar day), clamped to `0..23`. Example: `5` makes the day run 05:00 → 04:59 next morning, so a night-shift unlock after midnight is still the same logical day and does not re-trigger the poster. See `docs/newsviewer-spec.md` → The daily gate.

> ⚠️ **`Display:LogicalDayStartHour` MUST be provisioned as `REG_SZ`, never `REG_DWORD`.** `RegistryConfigurationProvider` coerces a `REG_DWORD` of `0` to the string `"False"` and `1` to `"True"` (see the DWORD mapping note above). The configuration binder then tries to convert `"False"`/`"True"` to `int`, throws, and **crashes NewsViewer at startup** — a fleet-wide outage from a one-value mistake. This foot-gun applies to **any** numeric override whose legitimate value range includes `0` or `1` (latent today for `Service:PollIntervalSeconds`). Provision such values as `REG_SZ` (e.g. `LogicalDayStartHour = "5"`).

**Display duration has NO registry or appsettings default.** `DisplayDurationSeconds` is a per-presentation value carried in the content, not configuration. `0` is a **meaningful** value ("unset") resolved in code to `30` by `PresentationDefaults.ResolveDuration` — it is not a missing setting to be filled from config. See `docs/data-model.md` → PresentationDefaults and `docs/newsviewer-spec.md` → Display Duration.

`Delivery` (NewsViewer) — desktop wallpaper **style only**, asserted per-user in HKCU on every run regardless of whether any wallpaper content is active. The wallpaper *image* is owned by NewsService, applied machine-wide via PersonalizationCSP (`Delivery:DefaultWallpaperPath` lives in the **NewsService** `Delivery` section, not here — see above). `WallpaperStyle` is `Fill | Fit | Stretch | Center | Tile` (default `Fit`). `WallpaperBackgroundColor` is `"R G B"` for the Fit letterbox bars (default `"0 0 0"`). NewsViewer-only; the NewsService `Delivery` section is separate and also carries `DefaultLockScreenPath`/`DefaultWallpaperPath`/`PublishedImagePath`/`LockScreenEnabled`/`WallpaperEnabled`.

## Azure Authentication Modes — NewsService only

NewsService runs as an unattended Windows Service with no interactive user. It authenticates to Azure Blob using one of three modes selected by `AzureBlob:AuthMode`:

| Mode | Credential type | Required keys |
|---|---|---|
| `Certificate` | `ClientCertificateCredential(tenantId, clientId, cert)` | `TenantId`, `ClientId`, `CertificateThumbprint` |
| `ClientSecret` | `ClientSecretCredential(tenantId, clientId, secret)` | `TenantId`, `ClientId`, `ClientSecret` |
| `ClientSecretEnv` | `ClientSecretCredential(tenantId, clientId, secret)` | `TenantId`, `ClientId`; secret from machine env var `NEWSSERVICE_AZURE_CLIENTSECRET` |

**Certificate mode** — the certificate is loaded from `Cert:\LocalMachine\My` by thumbprint (`X509Store(StoreName.My, StoreLocation.LocalMachine)`). Local System has access to `LocalMachine\My` by default; no additional key permission grants are required when the service runs as Local System. Preferred for production.

**ClientSecret mode** — uses a plain client secret string. Simpler to configure for development and testing.

**ClientSecretEnv mode (NewsService only)** — same `ClientSecretCredential` as ClientSecret, but the secret is read from the **machine-scope environment variable `NEWSSERVICE_AZURE_CLIENTSECRET`** (constant `SolutionConstants.NewsServiceAzureClientSecretEnvVar`) instead of configuration/registry. **Fail-closed:** if the variable is missing **or** empty, NewsService logs an error and refuses to authenticate — there is no fallback to `AzureBlob:ClientSecret`. Read behaviour: the code uses the Machine-scope read (`EnvironmentVariableTarget.Machine`), which reads the value **live from the registry** — a running service picks up a newly set or changed value on its next authentication attempt, so a restart is **not strictly required**. Contrast: a process-inherited (non-Machine-scope) read would see only the environment captured at service start and would require a restart — the general Windows expectation for env-var changes. Guidance: restarting NewsService after setting the variable remains the **recommended, unambiguous practice** — it matches normal admin expectations and stays robust if the read behaviour ever changes. **Convenience, not security:** a machine environment variable is clear text readable by any SYSTEM process, exactly like the registry `REG_SZ` secret — Azure Key Vault remains the intended secure path.

NewsCentral does **not** use these modes. It authenticates via an interactive MSAL user session. In particular, NewsCentral does **not** support `ClientSecretEnv`: it is an interactive per-user app with no LocalSystem context, so a machine-scoped SYSTEM variable is the wrong mechanism for it, and its config manifest does not offer the mode.

**`AzureBlob:UseWinHttpProxy`** (bool, default `false`) — when `true`, NewsService routes **all** its cloud SDK traffic (Azure Blob **and** Microsoft Graph) through `WinHttpHandler` with `UseWinHttpProxy`, i.e. the **machine WinHTTP proxy** (`netsh winhttp` / WPAD) that the Intune client and Windows Update use. Default `false` keeps today's behavior — the SDKs use the default .NET HTTP stack, which resolves its proxy via **WinINet** (per-user, unreliable under Local System with no user profile loaded). Enable this when NewsService cannot reach Azure/Graph under SYSTEM but the machine otherwise has working cloud connectivity. Shared transport: `AzureProxyTransportFactory` (one set of handlers per process); no per-app proxy config. Registry override: `AzureBlob\UseWinHttpProxy` REG_SZ `"true"`/`"false"`.

## Entra device team resolution (NewsService)

NewsService can resolve **multiple dynamic teams** per machine from the machine's own Entra (Azure AD) device object each poll cycle, writing `{CacheRootPath}\resolved-teams.json`; NewsService and NewsViewer union it with the static team list and consume it (key-with-content verification — see `docs/security.md`). The feature is gated by `Entra:Enabled` (default `false`).

The attribute source is **multi-instance**: zero or more independently-configured **named attribute schemes**, each with its own selector attribute and its own selector→rule mapping table. Each scheme resolves **at most one** dynamic team and carries its **own** grace window — the scheme name is the grace-partition instance id, `(Attribute, {schemeName})`. The group source is likewise multi-instance (see `Entra:GroupTeams` below) — its instance id is *derived* from `InclusionGroup` rather than authored.

**appsettings.json (NewsService):**

```json
"Entra": {
  "Enabled": false,
  "GracePeriodMinutes": 240,
  "MaxDynamicTeams": 16,
  "AttributeSchemes": {
    "Fat": {
      "Selector": "extensionAttribute1",
      "Mappings": {
        "FAT": "extensionAttribute2-extensionAttribute5-extensionAttribute4"
      }
    }
  },
  "GroupTeams": {
    "ExclusionGroup": "",
    "Instances": {
      "prague-its": {
        "InclusionGroup": "NewsCentral Prague ITS",
        "ExclusionGroup": ""
      }
    }
  }
}
```

Ship `AttributeSchemes` and `GroupTeams:Instances` **empty** in the shipped `appsettings.json` — configuration layers merge dictionaries key-by-key, so an entry defined in `appsettings.json` cannot be deleted by the registry. Keeping the base file empty leaves the registry/GPO hive authoritative for which schemes and group instances exist. The former flat `Entra:Mappings` and `Entra:GroupTeam` keys are **removed and no longer read** — there is exactly one way to configure each source now.

**Registry layout** (override; surfaced via the recursive walk in `RegistryConfigurationProvider`):

```
HKLM\Software\[Company]\NewsCentral\NewsService\Entra\
    Enabled              REG_SZ   "true" / "false"
    GracePeriodMinutes   REG_SZ   (int minutes; MUST be REG_SZ — 0 = "no grace" is deliberate; DWORD 0/1 coerce to "False"/"True" and the int binder throws)
    MaxDynamicTeams      REG_SZ   (int count; MUST be REG_SZ — 0 = "no cap" is deliberate; DWORD 0/1 coerce to "False"/"True" and the int binder throws; default 16)
    AttributeSchemes\
        <schemeName>\
            Selector   REG_SZ   "extensionAttribute1"
            Mappings\
                FAT   REG_SZ   "extensionAttribute2-extensionAttribute5-extensionAttribute4"
    GroupTeams\
        ExclusionGroup   REG_SZ   (fleet-wide kill switch; empty = none)
        Instances\
            <label>\
                InclusionGroup   REG_SZ   (required; activates the instance)
                ExclusionGroup   REG_SZ   (optional, per-instance)
```

`Entra:AttributeSchemes:{schemeName}:Selector`, `Entra:AttributeSchemes:{schemeName}:Mappings:{selectorValue}`, `Entra:GroupTeams:ExclusionGroup`, and `Entra:GroupTeams:Instances:{label}:InclusionGroup` etc. bind into `EntraOptions` automatically — no provider change; the existing recursive registry walk handles arbitrary nesting.

**Scheme naming and validation.** A scheme name (the dictionary key / registry subkey name) is the grace-partition instance id and must match `[A-Za-z0-9._-]+` — a name containing `:` would corrupt the configuration path, and one containing `\` cannot exist as a registry subkey. An invalidly-named scheme is skipped (logged at Warning) and is **not** part of the active instance set, so any team it may previously have produced is pruned without grace on the next cycle. A scheme's `Selector` must be `extensionAttribute1`..`extensionAttribute15`; a blank or malformed `Selector` fails that scheme closed (logged at Warning) rather than falling back to a default. One team max per scheme.

**Group-membership teams (`Entra:GroupTeams`):** the group source is **multi-instance** — zero or more independently-configured named inclusion/exclusion pairs under `Instances`, plus one fleet-wide `ExclusionGroup`. Each instance resolves **at most one** dynamic team when the device is a transitive member of its `InclusionGroup` and **not** a member of its own (optional) `ExclusionGroup` **nor** of the fleet-wide `ExclusionGroup`; the team folder name is the canonicalized `InclusionGroup` display name (same canonicalization as attribute teams). Contributes to `resolved-teams.json` tagged `Source: Group`, unioned and verified exactly like attribute teams. Requires the `GroupMember.Read.All` Graph permission (below).

**The instance label is a diagnostic convenience, not the instance identity.** The dictionary key / registry subkey under `Instances` (e.g. `prague-its`) is an operator-facing label — it locates the instance in regedit/GPO and appears in logs, nothing more. The actual instance id (the grace-partition key, `(Group, {id})`) and the team folder name are **both** `Canonicalize(InclusionGroup)` — the same string, derived, never authored. Renaming the label retargets nothing; changing `InclusionGroup` does. Recommend a short label (`prague-its`) rather than restating the derived id. Two instances whose `InclusionGroup` values canonicalize to the same id **collide**: the first by label (ordinal-ignore-case) wins and is kept active; the rest are skipped with a Warning naming both labels and the derived id.

**A blank `InclusionGroup` on an instance skips that instance** (logged at Warning) — it contributes no key, and any team it may previously have produced is pruned without grace on the next cycle (the same `activeKeys` rule that prunes a removed attribute scheme). This differs from the pre-M3 single-instance behavior, where a blank inclusion actively emitted a clean removal for the singleton key; pruning now does that job.

**The fleet-wide `ExclusionGroup` fails closed.** It is group-scoped (never affects attribute schemes) and applies to **every** active group instance on the machine. If its display name cannot be resolved (not found / ambiguous), NewsService suppresses **all** group instances for that cycle — the safe failure mode for a kill switch is to keep killing, not to silently stop — and logs **exactly one** `Error` line per cycle naming the group, regardless of how many instances are affected.

**Rule format:** each scheme's `Selector` attribute on the device selects a rule from that scheme's `Mappings` (ordinal, case-sensitive match on the selector's value). The mapped rule is a `'-'`-joined, ordered list of attribute names drawn from `extensionAttribute1`..`extensionAttribute15`, **except the scheme's own selector attribute** (a rule may not reference the attribute that selected it; 0 and 16+ are invalid regardless). NewsService reads each referenced attribute, joins the values in **rule order** with `-`, and canonicalizes (lower-case; space/underscore → `-`; strip anything outside `[a-z0-9-]`) into a team folder name — byte-for-byte identical to an authored folder built from the same tokens. Any empty referenced attribute aborts resolution for that scheme this cycle.

**Grace (per key, persistent vs transient):** when the device/Graph is **unreachable** (network/timeout/throttling/5xx), each key's — each attribute scheme's, and each group instance's — last resolved team is retained in `resolved-teams.json` with `State = Grace` for up to `GracePeriodMinutes`, then dropped. An authoritative "no team" answer — device read OK but no/invalid mapping for that scheme, or not-in-inclusion/excluded/unresolved group name for that instance, or device object not found — removes that key's entry **immediately**. A **403** on either the device read or the group check is **persistent**: it removes promptly (logged at Error), never grace. Every scheme and every group instance grace independently. An instance removed from configuration, or whose `InclusionGroup` changes (which changes its derived id), is pruned **without grace** on the next cycle and — if retargeted — starts fresh under its new id; the configured instance set is read locally and is authoritative even when Graph is unreachable.

**No configured schemes and no group instances.** When `AttributeSchemes` is empty (or every entry is invalidly named) and `GroupTeams:Instances` yields no valid instance (empty, all blank, or all invalidly configured), NewsService skips the Graph device fetch entirely for that cycle — there is nothing to resolve — and prunes any stale entries.

**Batched membership check.** Regardless of how many group instances are active, NewsService resolves the full set of distinct group display names referenced that cycle (every instance's `InclusionGroup` and non-blank `ExclusionGroup`, plus the global `ExclusionGroup` if set) and evaluates the device's membership in one pass — one `checkMemberGroups` Graph call per 20 distinct groups (the Graph limit), not one call per instance. A name that fails to resolve isolates to the instance(s) referencing it; a transport-level failure (unreachable, or 403) is global to the whole batch.

**`Entra:MaxDynamicTeams`** (int, default `16`; `0` = no cap) bounds the number of dynamic teams written to `resolved-teams.json` across both sources combined. When more teams resolve than the cap, NewsService truncates in a **deterministic order** — every Attribute entry (ordered by `SourceId`, ordinal-ignore-case) before every Group entry (likewise ordered by `SourceId`) — and logs one `Warning` per cycle naming the configured cap, the number resolved, and the dropped ids. Truncation is applied after the grace merge; a dropped entry's grace state is not preserved — if fewer teams resolve on a later cycle it reappears normally with a fresh `LastConfirmedUtc`, not a resumed grace window. The cap is a resource bound, not a resolution-logic change: grace, key derivation, and the Graph call path are unaffected.

**Operational notes.**
- **Per-team cost.** Each dynamic team resolved into `resolved-teams.json` costs NewsService one `index.json` fetch (and any changed images) per sync cycle, and costs NewsViewer one index parse per presentation-selection pass. `MaxDynamicTeams` exists because this cost is linear in the number of dynamic teams a machine ends up subscribed to.
- **Presentation selection is still single-winner.** Regardless of how many teams (static or dynamic) a machine is subscribed to, NewsViewer's poster selection picks exactly one active `News-of-the-Week` assignment — the one newest to the team (the later of `PresentationLastModified` and the assignment's `PublishedDate`) across **all** verified, schedule-active assignments from every subscribed team (see `docs/newsviewer-spec.md`). Subscribing a machine to N dynamic teams means N publishers are competing for that one poster slot; a device provisioned into many attribute schemes or group instances does not get N posters, it gets whichever one across all of them happens to be newest. This is an authoring/governance consideration for whoever provisions a large instance set, not a defect in the selection logic — worth knowing **before** provisioning a large `AttributeSchemes` / `GroupTeams:Instances` set, not after.
- **Change-gated summary logging.** Each cycle logs one line summarizing the full written team set (`Source/SourceId=TeamFolderName (State)` per entry, ordered the same way as the truncation above) — at `Information` when that set differs from the previous cycle (including a transition to zero dynamic teams), at `Debug` otherwise. This is the primary operator-facing signal for "what did Entra resolve"; per-scheme/per-instance outcomes remain at `Debug`, and every `Warning`/`Error` (invalid/colliding instances, unresolvable names, the fail-closed global-exclusion suppression, device-level failures) is unconditional and unaffected by this gating.

**Credential reuse:** the Microsoft Graph reads use the **same `AzureBlob` credential and app registration** as blob access (`AzureCredentialFactory.Create`), built lazily only when Entra is enabled. Because of this, **`AzureBlob:{AuthMode, TenantId, ClientId, …}` must be populated even when `Repository:StorageMode = Share`**.

> Azure app-registration permissions (Graph `Device.Read.All` / `GroupMember.Read.All`, admin consent), storage RBAC, the signing certificate, and device/group provisioning: see `docs/azure-setup.md` — the single source for Azure setup.
