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

The registry hive path (and the install folder) embed a Company name:

- Registry: `HKLM\Software\{Company}\NewsCentral\{Component}`
- Scheduled tasks: `\{Company}\NewsCentral\NewsViewer Launch` and
  `\{Company}\NewsCentral\NewsViewer Content Arrived`. The legacy Workstation Unlock task the new launch
  tasks replace is `\{Company}\NewsCentral\NewsViewer`; it is removed on install.

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
side alone. The same source and log are what NewsViewer's **`NewsViewer Content Arrived`** scheduled task
subscribes to for the `ContentChanged` event (EventID 2001, written once per sync cycle that accepted new
content): register the source **before NewsService first runs**, and see "NewsViewer launch surface" below
for the match rule.

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

NewsViewer runs per-user, in the interactive session, non-elevated. Its launch surface is two machine-wide
scheduled tasks (below), **registered by the installer** and verified by NewsService. There is **no HKLM Run entry**.

### NewsViewer launch surface

**The installer owns the tasks.** NewsService does not create, modify, repair or delete scheduled tasks
under any configuration: a LocalSystem service registering scheduled tasks is a persistence technique that
EDR products and ASR rules watch for, and a disabled capability would still have to be audited on every
machine. Instead it **verifies and reports** (`ViewerTasks\Mode`, **REG_SZ**, `VerifyOnly` by default or
`Off`): after a short delay, and then once per hour, it reads both tasks with `schtasks.exe /Query /XML`
(no Task Scheduler COM, no write verb) and compares them with the definitions built from the embedded
`deploy/scheduled-tasks` XMLs. A matching `[nc-hash:<sha256>]` stamp in the task's Description (written by
the registration script over the whole final definition) passes, a different stamp is drift, and a task
without a stamp (hand-made or legacy) is compared on command, working directory, event subscription and
trigger set. Drift - a missing task, a moved install, a changed trigger or principal - is logged at
`Warning` once per change; nothing is repaired, so the installer (or the script it ships) must be re-run.
`ViewerTasks\NewsViewerPath` overrides the exe path NewsService expects (default
`..\NewsViewer\NewsViewer.exe` relative to the NewsService install folder - matches the install layout
above). The former `ViewerTasks\Manage` value is gone; if it is still in the registry it is ignored and a
`Warning` names `ViewerTasks\Mode` as its replacement. See `docs/configuration.md`,
`docs/newsservice-spec.md`.

1. **Scheduled task `\{Company}\NewsCentral\NewsViewer Launch`** — session presence. Triggers: **logon**
   of any user (15 s delay so the shell is up), workstation **unlock**, **RemoteConnect** (RDP connect /
   reconnect) and **ConsoleConnect** (fast user switching back to a local session). No arguments.
   **Replaces the HKLM Run entry and the legacy Workstation Unlock task.**

2. **Scheduled task `\{Company}\NewsCentral\NewsViewer Content Arrived`** — new content. Event trigger on
   Application log / provider `NewsService` / **EventID 2001** (ContentChanged, once per sync cycle,
   written by NewsService after the cache is materialized). Argument **`--trigger content`**, which turns
   on the idle check (an away user is not interrupted).

Both tasks: principal the well-known group SID `S-1-5-32-545` (BUILTIN\Users) — MUST be the SID, not
the string "Users", which is localized (e.g. `Uzivatele`) and fails to resolve at import on non-English
Windows; run level `LeastPrivilege` (NOT "highest privileges": elevation would detach the task from the
user's desktop and break wallpaper application); `MultipleInstancesPolicy` **Parallel** (one instance per
logged-on session — the single-instance mutex is session-local, `Local\NewsCentral.NewsViewer`, so each
session gets its own poster/notification); no execution time limit; `StartWhenAvailable` false. The
reasons are spelled out in `deploy/scheduled-tasks/README.md`. There is deliberately no repeating/timer
trigger: timely delivery is the service's responsibility, the viewer re-evaluates at logon, unlock/connect
and content arrival.

3. **Start menu shortcut (optional, on demand)** - a shortcut whose target is
   `"%ProgramFiles%\{Company}\NewsCentral\NewsViewer\NewsViewer.exe" --manual`. The `--manual` switch
   shows today's poster on demand, bypassing the daily gate without stamping it. It is not an
   automatic trigger; `Active = false` still makes it exit silently.

**What the installer must do**

The installer owns scheduled-task **creation and removal**. There is no runtime fallback: NewsService
does not create, modify, repair or delete a scheduled task under any configuration, and a deleted task is
only reported (see above).

- **Register both tasks by running the registration script from the package** (see "The registration
  script is a build artifact" below). Do not hand-roll `schtasks` calls, and do not write an HKLM Run value.
  The script registers `NewsViewer-Launch.xml` and `NewsViewer-ContentArrived.xml` with the install path
  substituted into `Command` and `WorkingDirectory`, as UTF-16 LE with BOM (`schtasks` is sensitive to file
  encoding, so keep that encoding for any rewritten copy), and stamps each task's Description with the
  `nc-hash` NewsService verifies.
- **Register the NewsService event source** (`NewsService`, log `Application`). The script does this when
  the source is missing; NewsService also creates it explicitly at startup unless
  `ViewerTasks\RegisterEventSource` = `"false"`. Where it is `"false"`, the installer (the script) is the
  only creator and must run before NewsService first writes the ContentChanged event, so the first 2001
  event is attributed to a registered source and the Content Arrived task can fire.
- **Remove the legacy Workstation Unlock task** (`\{Company}\NewsCentral\NewsViewer`) and the old dev-box
  tasks, tolerantly (a missing task is not a failure), or an unlock starts NewsViewer twice. The script does
  this. NewsService no longer retires them.
- **Remove the legacy HKLM Run value `NewsViewer` on upgrade** (only when its data contains
  `NewsViewer.exe`) and stop writing it. NewsService no longer removes it; a Run value alongside the logon
  trigger starts NewsViewer twice at logon (the single-instance mutex absorbs it, but it is wasted work).
  `scripts/Register-NewsViewerTasks.ps1` does this; the generated script does not.
- **On uninstall, remove both tasks** (`Register-NewsViewerTasks.generated.ps1 -Uninstall`, or the
  installer's equivalent). **The service can never remove its own tasks** - it cannot act on its own
  uninstall - so uninstall-side ownership stays with the installer.

### The registration script is a build artifact

The script is generated **once, on the build machine, from the publish output**, shipped inside the
package, reviewed, signed and versioned with the release, and run by the installer from the package. No
production machine generates it and no running service writes it. Add this build step after publishing:

```
NewsService.exe --emit-task-script <package-staging>\Register-NewsViewerTasks.generated.ps1 --newsviewer-path "<target install dir>\NewsViewer\NewsViewer.exe"
```

- The path argument is required (no argument: the usage line on stderr and a non-zero exit). Exit code 0 on
  success; the reason on stderr and non-zero otherwise.
- `--newsviewer-path` is the NewsViewer.exe path **as it will be on the target machine**. Always pass it:
  without it the path is derived from the build layout, and a `ViewerTasks\NewsViewerPath` value in the build
  machine's registry would also be baked in. The command prints the `Company` and exe path it used - check
  them in the build log.
- The script carries `Company` (`SolutionConstants.Company`), the install path, the event log and source
  names, both task names, the legacy task names, and both task XMLs embedded as base64 of the exact file
  bytes (the UTF-16 LE BOM that `schtasks` needs survives). The packaging team needs no separate briefing on
  any of them, and a `Company` mismatch (which fails silently) cannot happen. It contains no secret.
- Run it elevated from the package: `-WhatIf` previews both tasks, the event source and the legacy
  removal; no switch registers them; `-Uninstall` removes both tasks. It is idempotent. Do not hand-edit it:
  rebuild it.
- Its header records the generating NewsService version, the generation timestamp and the build machine.

`scripts/Register-NewsViewerTasks.ps1` remains the dev-box and pilot path (no build step needed): it
registers the same tasks, creates the event source and removes the legacy tasks and the Run value. Tasks it
registers carry no `nc-hash`, so NewsService verifies them by field comparison only.

**Log/Source must match.** The Content Arrived subscription names the event log and source literally.
They MUST equal NewsService's `Logging:EventLog:LogName` / `SourceName` (shipped: `Application` /
`NewsService`). The generated script bakes in the values the build machine resolved, so build the script
with the same configuration the fleet will run; changing either value on the fleet requires regenerating the
script and re-running it, or the task silently never fires. NewsService reports a mismatch as drift
(`VerifyOnly`).

**Ship NewsService and NewsViewer together.** NewsViewer's notification records and `source`-marked
poster records add optional fields to the HMAC-signed session telemetry record. An older NewsService
verifying such a record deserializes without those fields, computes a different signature and
**discards the record as invalid** (ordinary poster records are unaffected). Upgrade both components in
the same rollout (NewsService first where the order is controllable).

**Task definition files:** the most robust way to create a task is to import a task-definition XML
(`schtasks /create /xml ...`). `schtasks` has historically been sensitive to file encoding — UTF-16 LE
with BOM is the safe choice. `.gitattributes` marks `deploy/scheduled-tasks/*.xml` as binary so Git never
re-encodes them.

NewsViewer writes no EventLog source and requires none.

## Uninstall

Both components — remove:

- All installed files under `%ProgramFiles%\{Company}\NewsCentral\{Component}\`.
- NewsService: stop and delete the `NewsService` Windows Service.
- NewsService: the six `PersonalizationCSP` display-surface values it applied, and the
  published-image folder — see **"NewsService — display-surface cleanup"** below. This is **not**
  optional: skipped, it leaves the machine with a permanently enforced lock screen and wallpaper
  that no remaining software can ever clear.
- NewsViewer: delete **both** scheduled tasks, `\{Company}\NewsCentral\NewsViewer Launch` and
  `\{Company}\NewsCentral\NewsViewer Content Arrived` (and, on a machine that still has it, the legacy
  `\{Company}\NewsCentral\NewsViewer` task). The reference script removes the two new tasks with
  `-Company <Company> -Uninstall`. Do this even though NewsService registered them: NewsService cannot
  clean up after its own uninstall. (A machine that still carries a pre-takeover `HKLM\...\Run\NewsViewer`
  value should lose it too.)
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

Scheduled-task folder: deleting the tasks leaves an empty `\{Company}\NewsCentral` task folder. Removing it
is optional; leaving it is harmless.

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

- **The scheduled tasks.** If the old product is removed after the new files and tasks are laid down,
  the old uninstall's "delete scheduled task" action runs after the new install created them —
  deleting a task the new version just installed (an old uninstall removes the legacy
  `\{Company}\NewsCentral\NewsViewer` task, and a newer one removes both `NewsViewer Launch` and
  `NewsViewer Content Arrived`). NewsViewer would then have no launch at all — no logon, unlock/connect
  or content-arrival launch — until NewsService next starts and re-registers the tasks (it self-heals
  at every start, but not within a running session), with no error to indicate why.
- **The six `PersonalizationCSP` display-surface values.** If the old uninstall's display-surface
  cleanup (see "NewsService — display-surface cleanup" above) runs *after* the new NewsService has
  already applied content, it clears the lock screen and wallpaper the new version just wrote — the
  machine is left with neither surface enforced and no error to explain it. This case is self-healing
  (the next poll cycle re-applies both surfaces), but it is confusing in the field for the gap between
  the clear and the next cycle, and it's still worth getting the ordering right rather than relying on
  the self-heal.

(For an Intune-only delivery the same principle holds — fully remove before install — even though the
`RemoveExistingProducts` term is MSI-specific.)

## Field diagnostics

NewsViewer has no logging framework and writes no EventLog source. It does keep one small per-user text
file, **`%LOCALAPPDATA%\NewsCentral\launch.log`** — one line per launch with the decision and its reason
(`reason=RemoteSession`, `NoContent`, `NoQualifyingMonitor`, `NotVisible`, `NoShell`, `Idle`,
`Busy`, `NotifyDisabled`, `MarkerInitialized`, `AlreadySeen`, `AlreadyRunning`, `Gate`, `Manual`, …) plus the
notification outcome lines; format and reasons in `docs/newsviewer-spec.md` ("Launch decision", "Launch
log"). It is the first thing to read when a user reports "I don't see any news" — the `reason=` says why
nothing was shown. (A machine with `Active = false` exits silently and logs nothing.) Further steps:

- verify the machine's GPO registry configuration under
  `HKLM\Software\{Company}\NewsCentral\NewsViewer\` (teams configured, signing public keys present,
  `Company` matches);
- on a machine with no static `teams\` entries, check `{CacheRootPath}\resolved-teams.json` instead
  — an absent file or an empty `Teams` array means Entra resolved no dynamic team for this machine,
  which is NewsService's responsibility (device/group provisioning, `Entra:Enabled`, Graph consent —
  see `docs/entra-dynamic-teams.md`), not something to chase in NewsViewer;
- clear the per-user gate by deleting `%LOCALAPPDATA%\NewsCentral\viewerstate.json` (forces a
  re-display on next launch; it also holds the per-user "last notified content" marker, so the next
  launch re-initializes it silently).

EventLog logging for NewsViewer remains a planned follow-up. When added, the installer will gain one
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

**NewsViewer:** files -> run the packaged `Register-NewsViewerTasks.generated.ps1` (registers `NewsViewer Launch` and
`NewsViewer Content Arrived`; Users SID, least privilege, Parallel; creates the NewsService event source if
missing; removes the legacy Workstation Unlock task) -> remove a legacy HKLM Run value. NewsService never creates
tasks.

**Uninstall:** remove files, service, the six PersonalizationCSP display-surface values
(ownership-checked first) and the published-image folder, both NewsViewer tasks, ProgramData cache; leave
GPO config, per-user state (including the HKCU wallpaper style values), and EventLog source.

**Hand-off verification:** inspect the ACTUAL publish output for BOTH components — do not infer it
from project files. Confirm `appsettings.json` IS present and `appsettings.Development.json` is NOT.
For NewsService this exclusion depends on the csproj's `<Content Update>` suppressing the Worker
SDK's default publish of `appsettings.*.json`; if that override is ever lost, a developer's personal
configuration ships to the fleet silently.
