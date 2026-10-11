# NewsCentral — Packaging & Installation Specification

## Purpose and scope

This document specifies what the installer(s) for the NewsCentral fleet components must do. It is
written for a packaging team producing an .msi and/or Intune (.intunewin) package. It describes
required actions and their parameters, not a specific packaging technology.

In scope — the two fleet components:

- **NewsService** — a Windows Service that syncs content to each machine.
- **NewsViewer** — a per-user desktop application that displays content.

Out of scope:

- **NewsCentral** — the authoring application. Installed separately on content-author machines only;
  not part of the fleet package.
- **NewsTester** — a planned preview tool, not yet built. Not in this package.

**Recommended: a single package installing both components** (two feature groups if the tooling
supports it). The two components MUST move together: both read the same ECDSA-signed `index.json`,
and the signature covers the exact shape of the JSON, so a schema change is not backward or forward
compatible in either direction — an older client rejects a newer index outright. If NewsService is
upgraded on a machine and NewsViewer is not, NewsService will cache content that the older
NewsViewer then refuses to verify: it skips the team and displays nothing, SILENTLY, with no error
visible to the user. A single package makes that lockstep structural rather than a deployment
discipline that must be remembered on every future release.

If the components are delivered as two packages anyway, they MUST always be deployed as a pair, in
the same deployment action. Never upgrade one without the other. (The requirements below are stated
per component and are correct whichever package shape is chosen.)

## Runtime prerequisite (critical)

Both components are published **framework-dependent**. They do not contain the .NET runtime.

Every target machine must have the **.NET 10 Desktop Runtime (x64)** installed before these components
run. The Desktop runtime is required (not merely the base runtime) because NewsViewer is a WinForms
application; the Desktop runtime includes the base runtime that NewsService needs.

This is declared a **deployment prerequisite**, satisfied by the fleet's baseline image or a separate
managed runtime deployment. The package does not bundle or chain the runtime installer.

If the runtime is absent, failure is not graceful: NewsViewer shows a "runtime not found" dialog at
launch; NewsService fails to start. The packaging team should treat the runtime as a hard dependency
in whatever dependency mechanism their tooling offers.

Note for the support team: because the runtime is shared and machine-wide, a Windows/.NET security
update can service it without the components being repackaged. This is normally beneficial, but it
means runtime behaviour can change without a component redeploy.

## The Company value

The registry hive path and the scheduled-task path both embed a Company name:

- Registry: `HKLM\Software\{Company}\NewsCentral\{Component}`
- Scheduled task: `\{Company}\NewsCentral\NewsViewer`

`Company` is fixed at build time from `Directory.Build.props` in the source tree (surfaced in code as
`SolutionConstants.Company`). The packaging team must build from source so this value is available,
and must use the same value for the task-folder path. It is not a runtime setting and must not be
prompted for or defaulted independently — a mismatch causes silent failure (see Troubleshooting).

## Install location

Both components install to:

```
%ProgramFiles%\{Company}\NewsCentral\{Component}\
```

e.g. `C:\Program Files\Contoso\NewsCentral\NewsService\` and `...\NewsViewer\`.

## NewsService — installation

**Files:** deploy the framework-dependent publish output to
`%ProgramFiles%\{Company}\NewsCentral\NewsService\`. `appsettings.json` is included and must sit beside
the executable. `appsettings.Development.json` is a developer-only file, excluded from the publish
output, and must never appear in the package (the exclusion relies on the csproj's
`<Content Update>` suppressing the Worker SDK's default publish of `appsettings.*.json` — see
`docs/configuration.md`).

**Service registration:**

- Service name: `NewsService`
- Display name: `NewsCentral News Service`
- Account: LocalSystem
- Start type: Automatic (Delayed Start)

Delayed start keeps the service from racing network/proxy availability at boot.

**Recovery actions** — restart on unexpected termination (an unhandled exception, OOM, or host kill).
These do NOT fire for network changes; the service handles a changed proxy/network context at
runtime by retrying on its next poll cycle.

- First failure: restart after 1 minute
- Second failure: restart after 1 minute
- Subsequent failures: restart after 2 minutes
- Reset failure count: after 1 day

**EventLog source:** NewsService writes to the Windows Application log using source name `NewsService`.
The installer must register this EventLog source (creation requires the elevated installer context;
the service running as LocalSystem can write to it but must not be relied on to create it).
Log name: Application. The shipped `appsettings.json` names this same source and log in its
`Logging:EventLog` section (`SourceName`/`LogName`) — the two must stay in step; do not edit either
side alone.

Start the service at the end of installation.

**NewsService — no configuration actions:** the installer does NOT write the component's runtime
configuration (team lists, signing public keys, Azure/proxy settings). That is provisioned
separately by Group Policy into `HKLM\Software\{Company}\NewsCentral\NewsService\`. The installer lays
down binaries and the service; GPO owns configuration. Keep this separation.

## NewsService — provisioning the Azure client secret (ClientSecretEnv mode)

If a site sets `AzureBlob:AuthMode = ClientSecretEnv`, the Azure client secret is NOT in the
registry — NewsService reads it from a **machine-level environment variable**:

```
NEWSSERVICE_AZURE_CLIENTSECRET
```

This is the **only configuration item that does not arrive through GPO/registry**, which is why it
is called out here: without it, that auth mode simply fails.

- **This is provisioning, not an installer action.** The installer must NOT set it. It is
  provisioned alongside the GPO registry configuration, by whatever mechanism the fleet uses for
  machine environment variables.
- **Machine scope is required.** The service runs as LocalSystem and does not see user variables.
- **Required only when `AuthMode = ClientSecretEnv`.** Certificate and ClientSecret modes ignore it.
- **Failure is fail-closed and self-diagnosing:** if the variable is missing or empty, NewsService
  throws with an error message naming the variable, and does not fall back to the registry secret.
- **A restart is not strictly required** — the value is read live from the machine scope on each
  authentication attempt — but restarting the service after setting it is the recommended,
  unambiguous practice.
- **Security honesty: this is a CONVENIENCE delivery, not a secure one.** A machine environment
  variable is clear text readable by any SYSTEM process, exactly like the registry `REG_SZ` secret.
  Azure Key Vault remains the intended secure path. Do not present this mechanism as more secure
  than the registry secret.

## NewsViewer — installation

**Files:** deploy the framework-dependent publish output to
`%ProgramFiles%\{Company}\NewsCentral\NewsViewer\`. `appsettings.json` is included and must sit beside
the executable. `appsettings.Development.json` is a developer-only file, excluded from the publish
output, and must never appear in the package.

**Localization satellite assemblies:** the publish output contains culture subfolders (`de/ es/ fr/ pt/ ar/ zh-Hans/ zh-Hant/`),
each holding `NewsViewer.resources.dll`. These MUST be deployed alongside `NewsViewer.exe` —
packaging that copies only top-level files will silently drop all translations (the app then falls
back to English). Neutral cultures cover regional variants via the standard .NET fallback chain
(`de` serves `de-DE`/`de-AT`/`de-CH`, etc.; `zh-Hans`/`zh-Hant` are neutral script cultures).

NewsViewer runs per-user, in the interactive session, non-elevated. It is launched by two
complementary mechanisms.

1. **HKLM Run value** — fires once at each interactive logon, in the logging-on user's session:

   ```
   Key:   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
   Name:  NewsViewer
   Value: "%ProgramFiles%\{Company}\NewsCentral\NewsViewer\NewsViewer.exe"
   ```

2. **Scheduled task** — fires on workstation unlock:

   - Task path: `\{Company}\NewsCentral\NewsViewer`
   - Trigger: on workstation unlock (session state change → SessionUnlock), any user
   - Principal: well-known SID `S-1-5-32-545` (the built-in Users group). MUST be the SID, not the
     string "Users", which is localized (e.g. `Uzivatele`) and fails to resolve at import on
     non-English Windows.
   - Logon type: interactive token (runs in the unlocking user's session)
   - Run level: least privilege — NOT "highest privileges"; elevation would detach the task from the
     user's desktop and break wallpaper application.
   - Repeating trigger: none in this release.

The two mechanisms do not overlap: Run fires at logon, the task fires at unlock. Together they give
a re-check at each natural session boundary. There is deliberately no repeating/timer trigger —
guaranteeing timely delivery of new content is the service's responsibility (it syncs content into
the cache); the viewer only needs to re-evaluate at logon and unlock.

3. **Start menu shortcut (optional, on demand)** - a shortcut whose target is
   `"%ProgramFiles%\{Company}\NewsCentral\NewsViewer\NewsViewer.exe" --manual`. The `--manual` switch
   shows today's poster on demand, bypassing the daily gate without stamping it. It is not an
   automatic trigger and does not change the Run/unlock behavior above; `Active = false` still
   makes it exit silently.

**Task definition file:** the most robust way to create this task is to import a task-definition XML
(`schtasks /create /xml ...`). `schtasks` has historically been sensitive to file encoding — UTF-16 LE
with BOM is the safe choice.

NewsViewer writes no EventLog source and requires none.

## Uninstall

Both components — remove:

- All installed files under `%ProgramFiles%\{Company}\NewsCentral\{Component}\`.
- NewsService: stop and delete the `NewsService` Windows Service.
- NewsService: the six `PersonalizationCSP` display-surface values it applied, and the
  published-image folder — see **"NewsService — display-surface cleanup"** below. This is **not**
  optional: skipped, it leaves the machine with a permanently enforced lock screen and wallpaper
  that no remaining software can ever clear.
- NewsViewer: delete the `HKLM\...\Run\NewsViewer` value and the scheduled task
  `\{Company}\NewsCentral\NewsViewer`.
- The machine content cache under `%ProgramData%\NewsCentral\` (and its per-team subfolders).

Deliberately left in place (do NOT remove):

- **GPO-provisioned registry configuration** under `HKLM\Software\{Company}\NewsCentral\`. Group Policy
  owns this; it is removed or re-applied on GPO's own lifecycle. The installer must not touch it.
- **Per-user state** — `viewerstate.json` under each user's `%LOCALAPPDATA%\NewsCentral\`. An installer
  running as SYSTEM cannot cleanly reach every user profile, and leaving it is harmless: a reinstall
  picks it up without issue.
- **NewsViewer's per-user HKCU wallpaper style values** — `WallpaperStyle`, `TileWallpaper` under
  `HKCU\Control Panel\Desktop`, and `Background` under `HKCU\Control Panel\Colors`. Consistent with
  the per-user-state rule above, and harmless: once the CSP-enforced wallpaper image is cleared
  (below) these become ordinary Windows personalization settings the user can change freely.
- **The NewsService EventLog source.** Removing an EventLog source is fussy and unnecessary; an orphaned
  source is conventional and harmless.

Scheduled-task folder: deleting the task leaves an empty `\{Company}\NewsCentral` task folder. Removing
it is optional; leaving it is harmless.

Task-delete must be tolerant: `schtasks /delete` returns non-zero if the task is already gone. The
uninstall action must not treat that as a failure.

### NewsService — display-surface cleanup

NewsService enforces two machine-wide display surfaces via `PersonalizationCSP`
(`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP`): the lock screen
(`LockScreenImagePath`, `LockScreenImageUrl`, `LockScreenImageStatus`) and the desktop wallpaper
(`DesktopImagePath`, `DesktopImageUrl`, `DesktopImageStatus`). Uninstalling the product removes the
only thing on the machine capable of clearing them — if the uninstaller doesn't do it, nothing else
ever will, and the machine keeps an enforced lock screen and wallpaper permanently, with
Personalization greyed out in Settings. `scripts/Remove-DisplaySurfaces.ps1` is the reference
implementation for the actions below; an MSI/Intune uninstaller should perform the equivalent, in
this order and with these rules:

1. **Clear the registry values first, then remove the folder.** A folder removed while CSP still
   references it leaves Windows pointing at missing files.
2. **Only clear a value that points inside `Delivery:PublishedImagePath`.** Read the configured path
   from `HKLM\Software\{Company}\NewsCentral\NewsService\Delivery\PublishedImagePath`, falling back
   to the default (`C:\Windows\Web\NewsCentral`) when the value is absent. Compare fully-normalized
   absolute paths. **A value pointing anywhere else belongs to another management system — Intune,
   GPO, a manual admin change — and must be left untouched.** If the configured path cannot be
   resolved at all, clear nothing: fail closed, exactly as the runtime teardown in `SyncService`
   does (`docs/anti-tamper.md` → "Teardown"). The uninstaller is not exempt from that ownership
   rule — silently clearing a foreign lock screen during an uninstall would be a worse failure than
   the one this section fixes.
3. **Do not remove the `PersonalizationCSP` key itself** — other CSP settings may live there.
4. **Tolerate absence.** A missing value or a missing folder is a normal outcome (already
   uninstalled, never applied, or `LockScreenEnabled`/`WallpaperEnabled` were off), not a failure —
   the same precedent as `schtasks /delete` above.
5. **Remove only the files this product wrote** — `lockscreen-*` and `wallpaper-*` in the publish
   folder — then remove the folder itself only if it is now empty. **Never a recursive delete of a
   registry-supplied path**: the folder came from configuration, not from this installer, and the
   installer must not assume it owns anything else that might be in it.

**Caveat for the admin: uninstall before retiring the GPO.** GPO-provisioned configuration is
deliberately left in place at uninstall (above). But if the GPO that set a **customized**
`Delivery:PublishedImagePath` is retired *before* the product is uninstalled, that customization is
no longer readable, the cleanup falls back to the default path, and the actual (custom) folder is
left behind untouched. Uninstall NewsCentral before retiring its GPO, not after.

## Upgrade (critical ordering requirement)

If a future version is delivered as an in-place upgrade, the old version's uninstall actions must
complete **before** the new version's install actions begin. In MSI terms: schedule
`RemoveExistingProducts` early (immediately after `InstallInitialize`).

The failure this prevents is silent, and now has two instances:

- **The scheduled task.** If the old product is removed after the new files and task are laid down,
  the old uninstall's "delete scheduled task" action runs after the new install created that task —
  deleting the task the new version just installed. NewsViewer would then have only the Run value
  and no unlock trigger, with no error to indicate why.
- **The six `PersonalizationCSP` display-surface values.** If the old uninstall's display-surface
  cleanup (see "NewsService — display-surface cleanup" above) runs *after* the new NewsService has
  already applied content, it clears the lock screen and wallpaper the new version just wrote — the
  machine is left with neither surface enforced and no error to explain it. This case is self-healing
  (the next poll cycle re-applies both surfaces), but it is confusing in the field for the gap between
  the clear and the next cycle, and it's still worth getting the ordering right rather than relying on
  the self-heal.

(For an Intune-only delivery the same principle holds — fully remove before install — even though the
`RemoveExistingProducts` term is MSI-specific.)

## Field diagnostics (limitation in this release)

NewsViewer emits no runtime diagnostic logging in this release. When a user reports "I don't see any
news," the available troubleshooting is:

- verify the machine's GPO registry configuration under
  `HKLM\Software\{Company}\NewsCentral\NewsViewer\` (teams configured, signing public keys present,
  `Company` matches);
- on a machine with no static `teams\` entries, check `{CacheRootPath}\resolved-teams.json` instead
  — an absent file or an empty `Teams` array means Entra resolved no dynamic team for this machine,
  which is NewsService's responsibility (device/group provisioning, `Entra:Enabled`, Graph consent —
  see `docs/entra-dynamic-teams.md`), not something to chase in NewsViewer;
- clear the per-user gate by deleting `%LOCALAPPDATA%\NewsCentral\viewerstate.json` (forces a
  re-display on next launch).

EventLog logging for NewsViewer is a planned follow-up. When added, the installer will gain one
action: registering a `NewsViewer` EventLog source. The package structure should leave room for that.

## Troubleshooting — silent Company mismatch

`Company` defines the registry hive path and is not registry-overridable. If the value the components
were built with does not match the hive GPO wrote to, the lookup returns nothing with NO error: every
GPO override is silently ignored and the component runs on shipped defaults (NewsService finds no
teams and does nothing; NewsViewer shows nothing). If a freshly deployed machine behaves as though it
has no configuration, verify that the built-in `Company` value and the GPO hive path
`HKLM\Software\{Company}\NewsCentral\` use the identical company name.

## Summary of installer actions

**NewsService:** files → register service (LocalSystem, delayed-auto) → set recovery actions →
register EventLog source → start service.

**NewsViewer:** files → HKLM Run value → import scheduled task (unlock trigger, Users SID, least
privilege).

**Uninstall:** remove files, service, the six PersonalizationCSP display-surface values
(ownership-checked first) and the published-image folder, Run value, task, ProgramData cache; leave
GPO config, per-user state (including the HKCU wallpaper style values), and EventLog source.

**Hand-off verification:** inspect the ACTUAL publish output for BOTH components — do not infer it
from project files. Confirm `appsettings.json` IS present and `appsettings.Development.json` is NOT.
For NewsService this exclusion depends on the csproj's `<Content Update>` suppressing the Worker
SDK's default publish of `appsettings.*.json`; if that override is ever lost, a developer's personal
configuration ships to the fleet silently.
