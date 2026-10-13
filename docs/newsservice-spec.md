# NewsService — Component Specification

**Type:** .NET 10 Windows Service (`Microsoft.NET.Sdk.Worker`)  
**Target:** `net10.0-windows10.0.19041.0`, `win-x64`  
**Status:** Implemented — full sync cycle operational in Share mode; Azure Blob mode implemented with Certificate / ClientSecret / ClientSecretEnv auth.

## Configuration Resolution

`RegistryConfigurationProvider` (from `NewsCentral.Shared`) is added to `IConfigurationBuilder` in `Program.cs` via `AddRegistryOverrides(SolutionConstants.Company, SolutionConstants.SolutionName, "NewsService")`. It reads from `HKLM\Software\{Company}\NewsCentral\NewsService\` and merges registry values on top of `appsettings.json`. Any appsettings.json key can be overridden; see `docs/configuration.md` for the full registry layout.

Key registry values for NewsService:

| Registry path | Type | Effect |
|---|---|---|
| `Repository\StorageMode` | `REG_SZ` | `Share` (default) or `Azure` |
| `Service\PollIntervalSeconds` | `REG_SZ` | Overrides `Service:PollIntervalSeconds` (int; MUST be REG_SZ — DWORD 0/1 coerce to `"False"`/`"True"` and the int binder throws) |
| `teams\{teamFolderName}` | `REG_SZ` | Each value name is a team folder name |
| `Signing\{teamFolderName}\PublicKey` | `REG_SZ` | Base64 SPKI for ECDSA `index.json` verification |
| `Signing\{teamFolderName}\PublicKeyPrevious` | `REG_SZ` | Base64 SPKI — rotation window (optional) |
| `Logging\LogLevel\Default` | `REG_SZ` | Overrides `Logging:LogLevel:Default` (`Trace` \| `Debug` \| `Information` \| `Warning` \| `Error` \| `Critical` \| `None`) |
| `Logging\EventLog\LogLevel\Default` | `REG_SZ` | Overrides `Logging:EventLog:LogLevel:Default` (same values; EventLog provider only) |

`Company` is the build-time constant `SolutionConstants.Company` (authored in `Directory.Build.props`, generated into `NewsCentral.Shared`) — **not** an appsettings value — and is not registry-overridable (it defines the hive path; a mismatch fails silently as `OpenSubKey` returns `null`). The solution segment is likewise the fixed constant `SolutionConstants.SolutionName`, not a config value.

## Poll Cycle — `SyncService.RunCycleAsync`

Executed by `Worker` on every interval tick:

**Step 1 — Index sync (per team)**
- Reads `{teamFolder}/index.json` from the repository
- **ECDSA verification** — calls `SignatureGate.VerifyWithPrecedence(remoteIndex, keys, isDynamic)` where `keys = SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolderName)`, then `SignatureGate.ShouldReject`. Rejection → **always logs Error** and skips the team entirely (security outcomes are never demoted or gated on change). Acceptance follows the cycle-wide "log on change, stay quiet otherwise" rule: it logs at its natural level (`Unsigned` → Warning, otherwise Information) only when the index changed **or** the verification result changed since the previous cycle for that team, and at Debug otherwise. The per-team last result is held by the singleton `SyncService`, so every team's verification state is reported at least once per service start; the verification itself still runs before the cached index is read and before any content is trusted — only the logging is positioned after the hash comparison. Index is deserialized with standard ISO timestamp parsing (no `DateTime` converter) so values match the signed, persisted form.
- Compares `IndexHash` with the cached copy
- If unchanged: no download and no index rewrite. **Cache self-heal** — the images the *cached* index references are checked locally (no repository access) and only the missing ones are re-fetched through the normal per-image sync (covers files removed locally, e.g. by antivirus or a cleanup tool). A heal is logged as one `Warning` (restored N image(s)); when an image cannot be restored it is retried every cycle with the repeats at Debug. A heal is **not** a content change: it raises no `ContentChanged` event.
- If changed: for each `PublishedAssignmentIndex`, checks the locally stored SHA-256 sidecar (`{imagePath}.hash`) against `Content.ImageHash`; downloads only changed or missing images
- **Index last.** The new `index.json` is written to the cache (atomically) only after **every** referenced image is cached. On a partial sync the previously cached index stays in effect (NewsViewer keeps showing the previous content), one `Error` is logged naming the missing images, the sync is retried next cycle (the index hash still differs) and only the still-missing images are fetched again; repeats of the same failure log at Debug.
- **One `ContentChanged` event per cycle.** After all teams have been synced, if at least one team produced an accepted **and complete** change, NewsService writes a single EventLog event — Application log, source `NewsService` (`Logging:EventLog:LogName` / `SourceName`), **EventID 2001** (`ServiceEventIds.ContentChanged`), Information — straight to the Event Log (unaffected by the logging levels). Several teams changing in one cycle still produce one event. NewsViewer's "Content Arrived" scheduled task subscribes to it (`docs/newsviewer-spec.md`, `docs/packaging.md`); the event source must be registered by the installer.

**Step 2 — Display surfaces (lock screen, then wallpaper)**

NewsService manages **two** display surfaces through PersonalizationCSP — the logon/lock screen and, since the wallpaper-ownership migration from NewsViewer, the desktop wallpaper. `Delivery:LockScreenEnabled` and `Delivery:WallpaperEnabled` (bool, default `true` each) independently gate their own surface **entirely**: when a toggle is `false`, nothing for that surface is read, published, swept, written, or cleared that cycle — logged once at Debug. **Neither is a revert** — see "Enable toggles" below.

When at least one surface is enabled:
- **One enumeration pass, two predicates.** Every team's cached index is read **once**, flattening all published assignments into a single list (`SyncService.CollectAssignmentsAsync`). Both surfaces' winners are picked from that same materialized list via the shared pure helper `ActiveAssignmentSelector.PickNewestActive` — lock screen with predicate `a => a.DisplayTypes.IsLogonScreen`, wallpaper with `a => a.DisplayTypes.IsWallpaper` — never two separate walks of the index files. `PickNewestActive` applies the same active-window/day-of-week filter and newest-by-`PresentationLastModified` tie-break NewsViewer's poster selection uses.
- **Lock screen applies first, then wallpaper**, each isolated in its own try/catch (`SyncService.ApplyDisplaySurfacesAsync`) — a failure in one surface is logged and can never prevent the other from applying, fail the cycle, or crash the service.
- For each enabled surface, independently:
  - **Stale-file sweep** — reads the live PersonalizationCSP value for that surface and calls `IImagePublisher.SweepExcept(keepFileName, prefix)` (`prefix` = `"lockscreen"` or `"wallpaper"`) to delete every other previously-published file **under that surface's own prefix** from `Delivery:PublishedImagePath`. Scoped strictly to the prefix — sweeping one surface never deletes the other's files, since both publish into the same folder under different content-derived namespaces. Runs at the **start** of the surface's own step, before anything is published this cycle — never in the same cycle a file was just written, since Windows may still hold it open. (`SweepExcept(null, prefix)` deletes every file matching that prefix — a legitimate steady state once teardown is in play.)
  - **Publish, then apply.** The intended *source* image and its expected hash are computed:
    - Winner exists → source = the winner's cached image path, expected hash = `Content.ImageHash` from the signed index
    - No winner and the surface's default path (`Delivery:DefaultLockScreenPath` / `Delivery:DefaultWallpaperPath`) is configured and the file exists → source = the default path; the default carries no index hash, so its own SHA-256 is computed and passed as the expected hash — a self-consistent no-op verification, not a skipped check
    - No winner and no usable default (unset, or configured-but-missing → Warning logged) → source = `null`

    A non-null source is re-verified and copied into the protected `Delivery:PublishedImagePath` folder by `IImagePublisher.Publish` — SHA-256 re-checked against the expected hash, target file named `{prefix}-{hash16}.ext` (content-derived, so the name changes exactly when the bytes change), written via a temp-file-then-move so a torn copy can never be referenced. A publish failure (hash mismatch, missing source, I/O error) logs a Warning and is treated the same as "no source" — it is **not** a skipped check.
  - **Three-state, registry-driven, stateless apply** — the live PersonalizationCSP value for the surface is the single source of truth; there is no `servicestate.json`. Comparisons use fully-normalized absolute paths (`Path.GetFullPath`, `OrdinalIgnoreCase`), never a raw string prefix match. Dispatches on the pair (published path `intended`, live value `current`):

    | `intended` | `current` | Action |
    |---|---|---|
    | non-null | equals `intended` | No-op — steady state |
    | non-null | differs (or absent) | Write the trio — log Information on success, Error on failure |
    | `null` | absent | No-op, silent |
    | `null` | present, **inside** `Delivery:PublishedImagePath` (i.e. one **we** published) | **Clear** the trio — log Information |
    | `null` | present, **outside** `Delivery:PublishedImagePath` (foreign — GPO, Intune, a manual admin change) | Left alone — log Debug |

    The "inside `PublishedImagePath`" ownership test — shared by both surfaces, since they publish into the same protected folder — is the safety mechanism for the clear branch: a misconfigured or unresolvable `Delivery:PublishedImagePath` (empty, invalid characters, `Path.GetFullPath` throws) makes the test fail **closed** — every live value is then treated as foreign and is **never** cleared.
  - A failed write or clear is **not** recorded as applied: the live value still won't match, so the next cycle re-evaluates and retries naturally.

  The dispatch core is genuinely shared code, not parallel copies: `SyncService.ApplyIntendedSurface` is parameterised by the surface's get/set/clear operations, and `SyncService.ApplyIntendedLockScreen` / `ApplyIntendedWallpaper` are thin, directly-testable wrappers over it. The per-surface entry points `SyncService.ApplyLockScreenAsync` / `ApplyWallpaperAsync` (each independently testable, gate-check included) are what `ApplyDisplaySurfacesAsync` drives with the pre-collected one-pass assignment list. Unit-tested end to end: `LockScreenApplyTests` and `WallpaperApplyTests` cover each surface's dispatch and toggle gate with an `IPersonalizationService` test double; `DisplaySurfacesTests` covers the cross-surface behavior (one-pass collection, independent sweeps, independent toggles, try/catch isolation) with a real `CacheManager` + `ImagePublisher` against temp directories; `ImagePublisherTests` covers the publish step, including that a sweep never crosses prefixes.

> **This replaced an earlier sticky-on-null behavior.** Before the teardown milestone, `intended == null` always left the current value untouched, unconditionally. That is no longer universally true: a value NewsService itself previously published is **cleared** when there is nothing left to show, returning the machine to Windows' own default at the next lock/logon rather than displaying stale content indefinitely. Only a **foreign** value (or a disabled toggle) is still left untouched — genuinely sticky, because NewsCentral never manages a value it did not write.

> **Prerequisite — Windows Enterprise.** Personalization CSP (the mechanism behind both surfaces' apply) is documented by Microsoft as supported on Windows Enterprise and Education SKUs, and on Pro only under Shared PC / Cloud Config (BootToCloud) configurations. The raw registry writes this service performs are widely observed to work on Pro outside those configurations too, but that is undocumented behavior — do not rely on it for a production fleet running Pro.

> **Verified on Windows 11 Enterprise — CSP enforces the image, HKCU still controls the fit.** PersonalizationCSP's `DesktopImagePath`/`DesktopImageStatus` enforce **which** image is the desktop wallpaper (Windows Settings greys the picker out), but they do **not** cover **how** that image is fitted — the per-user HKCU `WallpaperStyle`/`TileWallpaper`/`Colors\Background` values still control Fill/Fit/Stretch/Center/Tile framing, confirmed by testing an 800×600 image against an applied CSP wallpaper: changing `WallpaperStyle` visibly changed the framing while the image itself stayed CSP-pinned. Since style is per-user and NewsService runs in session 0 (no per-user HKCU reach), NewsViewer retains a small style-only residue that re-asserts on every run — see "Wallpaper Application" in `docs/newsviewer-spec.md`.

**Enable toggles — `Delivery:LockScreenEnabled` / `Delivery:WallpaperEnabled`.** Both `bool`, default `true`, and **fully symmetric** — each independently gates its own surface exactly as described above. **Neither toggle is a revert.** Setting one to `false` does not clear whatever is currently applied for that surface — it freezes it in place, since the surface is no longer touched at all. Provision the toggle in the GPO baseline **before** the machine's first sync cycle for any machine where a surface should never be managed; flipping it off after content has already been applied requires clearing the value by hand (or flipping it back on so the ordinary teardown path can do it).

**Session-host advisory.** At startup, `Program.cs` logs a `Warning` when this machine looks like a Windows Server with Remote Desktop connections allowed (`fDenyTSConnections == 0`) and either toggle is still at its default `true` — a best-effort, conservative heuristic (never fires on a client Windows workstation) described in full in the code comment on `WarnIfSessionHostSurfaceUnmanaged`. **RDS session hosts, VDI templates, and RemoteApp hosts should set both toggles to `0`**: one machine-wide registry value cannot correctly serve many concurrent sessions, RDS policy can suppress the desktop background outright, and non-persistent VDI rebuilds every boot, making any applied state meaningless past the next reboot.

> **Verified on Windows 11 Enterprise — changes take effect at the next session boundary, not immediately.** Both surfaces are applied by NewsService from session 0, which has no way to broadcast `WM_SETTINGCHANGE` into a user session; Windows re-evaluates PersonalizationCSP only at a lock/unlock or a logon. A wallpaper change written correctly to `DesktopImagePath` did not appear on the desktop until the session was cycled. This is a genuine behavior change from before the wallpaper-ownership migration, when NewsViewer called `SystemParametersInfo` with `SPIF_SENDCHANGE` and the desktop repainted within seconds. Content published mid-day will not appear on an unlocked machine until it is next locked or the user logs in again — this is expected, not a fault. The maximum delay before NewsService *writes* the change is still one `Service:PollIntervalSeconds`; the session boundary is additional on top of that, not instead of it.

**Step 3 — status.json**
- Writes `LastSyncTime`, `IsOnline`, `SyncSource` (`Share` / `Azure` / `None`) to cache root

**Step 4 — Telemetry upload & local retention**
- **Upload gate:** `Telemetry:UploadEnabled` (bool, default `true`; registry `Telemetry\UploadEnabled` DWORD). When `false`, nothing is forwarded to the repository (logged once per cycle at Debug) — this expresses a read-only deployment that consumes content without writing anything back. NewsViewer is deliberately unaffected: it always writes session files, and NewsService alone decides what becomes of them (a single point of control) — re-enabling upload immediately flushes whatever is still inside the retention window.
- Deserializes each `uploads\session-*.json` as `SessionTelemetry` and calls `HmacService.Verify`
- Treats every record alike: poster-session and notification records (`recordType: "notification"`) are verified and forwarded unchanged — the uploader neither counts nor filters by record type. Consumers of the uploaded files must filter on `recordType` (see `docs/data-model.md`).
- Files with `Invalid` signature are logged and deleted without forwarding
- `Valid` and `Unsigned` files are copied to `{SharePath}\uploads\`; the local copy is deleted after a successful copy
- **Unconditional retention sweep** — after the upload stage, in the same enumeration pass, remaining local files older than `TelemetryDefaults.RetentionDays` (**30 days**, fixed) are deleted, judged by the file's `LastWriteTime` (no deserialization and no HMAC check just to age a file — the files are written locally and never moved, so the filesystem timestamp is reliable). The sweep runs on **every** path — upload disabled, `SharePath` unconfigured, unreachable destination folder, failed copies — precisely the cases where files previously accumulated without bound. A failed delete logs Warning and continues; one locked file never aborts the sweep. Logging follows the log-on-change rule: Information with the count and window when files were deleted, Debug otherwise. The rationale is **data hygiene, not disk space**: volume is tiny (roughly one small file per user per logical day), but the records describe what a specific user saw and when, and should not persist indefinitely on a workstation. The window is deliberately a **constant, not a setting**: there is no operational need to tune it at this volume, and a configurable `0` would be dangerously ambiguous between delete-everything and keep-forever.

## Storage Abstraction

`IRepositoryReader` has two implementations, selected at DI registration time based on effective storage mode:

| Implementation | Mode | Notes |
|---|---|---|
| `LocalShareRepositoryReader` | `Share` | Reads from UNC/local path; `IsAvailable` checks `Directory.Exists` |
| `AzureBlobRepositoryReader` | `Azure` | Certificate, ClientSecret, or ClientSecretEnv auth; reads index.json and images from blob container |

## CacheManager — Hash Sidecar Pattern

When an image is written to cache, `CacheManager.WriteBytesAsync` also writes `{imagePath}.hash` containing `sha256:{hex}`. On the next cycle, `ReadStoredHash` reads this file instead of re-hashing the image, making per-image change detection O(1).

## Display surfaces — `PersonalizationService`

NewsService applies **both** display surfaces machine-wide through PersonalizationCSP: the logon/lock screen, and — since the wallpaper-ownership migration — the desktop wallpaper. No `IDesktopWallpaper`/COM code exists in NewsService; both surfaces are plain registry values.

| Target | Values | API | Session constraint |
|---|---|---|---|
| Lock screen | `LockScreenImagePath`, `LockScreenImageUrl`, `LockScreenImageStatus` | `PersonalizationCSP` registry keys (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP`) | Works from SYSTEM — no desktop access needed. NewsService runs as LocalSystem. Enterprise/MDM-grade mechanism used by Intune. |
| Wallpaper (image) | `DesktopImagePath`, `DesktopImageUrl`, `DesktopImageStatus` | Same `PersonalizationCSP` key | Same as above — machine-wide, no desktop access needed. |

`IPersonalizationService` (`PersonalizationService.cs`, renamed from `LockScreenService.cs` when the wallpaper surface was added) exposes two mechanically parallel trios of methods, one per surface:

- `bool SetLockScreen(string)` / `bool SetWallpaper(string)` — writes the three CSP values for that surface, in order (path, url, then status last); returns `false` on a missing image or a caught write failure.
- `string? GetCurrentLockScreenPath()` / `string? GetCurrentWallpaperPath()` — reads the live `*ImagePath` value for that surface, or `null` if absent/unreadable.
- `void ClearLockScreen()` / `void ClearWallpaper()` — deletes that surface's three values if present, tolerating their absence; never removes the `PersonalizationCSP` key itself, since the other surface's (or other CSP consumers') values may live there. Any failure is a Warning, never a throw.

`SyncService` owns the Information-level "applied"/"cleared" logs for each surface; the write/clear methods log their own registry operations at Debug. `SetLockScreen`/`SetWallpaper` are always called with a path already published under `Delivery:PublishedImagePath` by `ImagePublisher` — CSP never points directly into the `%ProgramData%` cache; see `IImagePublisher` below and `docs/anti-tamper.md`.

A configurable **default image** is applied per surface when no matching content is active: `Delivery:DefaultLockScreenPath` for the lock screen, `Delivery:DefaultWallpaperPath` for the wallpaper (both absolute paths, default `""` = no default). `DefaultWallpaperPath` **moved here from the NewsViewer hive** when wallpaper-image ownership migrated to NewsService — a value left behind in NewsViewer's own hive is simply never read; a GPO update removes it in due course. Empty falls through to the same three-state dispatch as "no active content" — a value NewsService previously published is **cleared**, a foreign value is left alone. See Step 2 of the poll cycle for the full dispatch table.

## `IImagePublisher` — protected-folder publish before apply

`ImagePublisher` re-verifies and copies the winning image for **each** surface (or that surface's configured default) into `Delivery:PublishedImagePath` (default `C:\Windows\Web\NewsCentral`, shared by both surfaces under separate content-derived namespaces) before `SetLockScreen`/`SetWallpaper` is ever called, closing two gaps that existed when CSP pointed directly at the cache:

1. **No re-verification at apply.** The image's SHA-256 was checked once, at download time, against the signed index — never again when the path was actually handed to the OS. `Publish` re-checks it every time.
2. **A writable cache folder.** `%ProgramData%\NewsCentral\` inherits `C:\ProgramData`'s ACL, under which a standard user can pre-create a team's `images\generated\` folder and, as `CREATOR OWNER`, retain delete-child rights over everything later written into it. Both surfaces are machine-wide, system-enforced surfaces, so that exposure matters for either.

`Publish(sourcePath, expectedHash, prefix)` hashes `sourcePath`, compares it (accepting both the bare-hex and `sha256:`-prefixed forms, case-insensitively) against `expectedHash`, and on a match copies it into the publish folder as `{prefix}-{hash16}{ext}` (`prefix` is `"lockscreen"` or `"wallpaper"`) — a content-derived name, so an existing target of that name is known-correct and is never re-copied, and the CSP path changes exactly when the image content changes. A mismatch, a missing source, or an I/O error logs and returns `null`; the caller must treat that as "no usable image," never as success. The publish root is created (inheriting its parent's ACL) if absent; no explicit DACL is ever set — inheriting from `C:\Windows` (Users read-only, no writable-down inheritance) is the entire point.

`SweepExcept(keepFileName, prefix)` deletes every other previously-published file matching `{prefix}-*`. `SyncService` calls it once per surface, at the **start** of that surface's own step — reading the live CSP value first — so a file is never removed in the same cycle it was published, when Windows may still hold it open. Scoping the glob to `prefix` means the lock-screen sweep and the wallpaper sweep can never touch each other's files even though both publish into the same folder.

A startup check (`ImagePublisher.CheckPublishFolderAcl`, called once from `Program.cs` after the host is built) logs a `Warning` if the configured publish folder already exists and grants `Write`/`Modify`/`FullControl` to `Users` or `Authenticated Users` — a security warning, not a functional failure; `Publish` still succeeds against a writable folder, it simply no longer has the ACL protection this design relies on. Separately, an **empty or unresolvable** `Delivery:PublishedImagePath` makes every `Publish` call fail *and* makes the ownership test in Step 2's three-state apply fail closed — the net effect is that nothing is ever cleared, because nothing can be identified as one of ours (genuinely sticky, in that specific misconfiguration). Neither case is fatal to the service. See `docs/anti-tamper.md` for the full threat model and `docs/configuration.md` for `Delivery:PublishedImagePath`.

## NewsViewer task provisioning — `ViewerTaskProvisioningService`

NewsService owns the NewsViewer launch surface: the two machine-wide scheduled tasks `\{Company}\NewsCentral\NewsViewer Launch` (logon + 15 s, unlock, RemoteConnect, ConsoleConnect) and `\{Company}\NewsCentral\NewsViewer Content Arrived` (event 2001, `--trigger content`). There is no HKLM Run entry. `deploy/scheduled-tasks/*.xml` remain the **only** task definitions: `NewsService.csproj` embeds them (linked, not copied; logical names `NewsService.TaskDefinitions.<file>`) and no task XML is generated in code. Configuration: `ViewerTasks:Manage`, `ViewerTasks:NewsViewerPath` (`docs/configuration.md`).

- **When.** A `BackgroundService` registered after the sync `Worker`: it yields at once, waits ~10 s, then runs a pass; a failed pass is retried every 5 minutes until one fully succeeds, then the service exits. Every service start runs it again — that is the verify-and-repair loop. It never delays or blocks the sync Worker and never throws out of the host.
- **`TaskDefinitionBuilder`** (pure, unit-tested against the real embedded XMLs) applies the substitutions `scripts/Register-NewsViewerTasks.ps1` applies: `RegistrationInfo/URI` → the task name, `Exec/Command` → `"<exe>"`, `Exec/WorkingDirectory` → the exe's folder, and for Content Arrived the `EventTrigger/Subscription` → the configured `Logging:EventLog:LogName`/`SourceName` with `ServiceEventIds.ContentChanged`. The Description gets the suffix ` [nc-hash:<hex>]`; the SHA-256 is computed over the final XML with the suffix absent, so output is deterministic for identical inputs. A missing or malformed stamp counts as a mismatch. An invalid log/source (empty, or containing a quote, `<`, `>` or `&`) skips the Content Arrived task only, with an Error.
- **`ITaskSchedulerClient`** wraps `%SystemRoot%\System32\schtasks.exe` (no Task Scheduler COM): `/Query /XML`, `/Create /XML <tmp> /F` (UTF-16 LE BOM temp file under `Path.GetTempPath()`, always deleted) and `/Delete /F`, with exit code and output captured and a 30 s timeout.
- **Pass.** (1) `Manage` false → Debug, nothing else (no scheduler or registry call). (2) The exe path (`NewsViewerPath`, else `..\NewsViewer\NewsViewer.exe` from `AppContext.BaseDirectory`, `Path.GetFullPath`) must exist; if not: Error naming the path, nothing registered or removed, retry. (3) Per task: query, compare the `nc-hash`; equal → Debug "unchanged"; otherwise `/Create /F` and Information "registered"/"updated". (4) **Only when both tasks are in place**: delete the legacy Workstation Unlock task and the old dev-box tasks (`\NewsCentral\NewsViewer Launch|Content Arrived`) if present, and remove the HKLM Run value `NewsViewer` — read through the 64-bit view, only when its data contains `NewsViewer.exe`, never any other value. A machine therefore never has neither a Run entry nor a logon trigger.
- **Failures** are logged, never thrown: schtasks non-zero → Error with task name, exit code and trimmed output; "access denied" (or a non-elevated token) while not LocalSystem — the dev-box console case — → Warning, retried after 5 minutes. No new `ServiceEventIds` are defined; those are reserved for events other components key off.

## Azure Authentication

- Machine certificate from local machine certificate store
- Proactive token refresh; exponential backoff on transient failures; cert-expiry logging
- Handles weeks-long uptime without restart

**ClientSecretEnv mode (NewsService only).** A third `AzureBlob:AuthMode` alongside Certificate and ClientSecret: `AzureCredentialFactory` builds the same `ClientSecretCredential`, but the secret comes from the **machine-scope environment variable `NEWSSERVICE_AZURE_CLIENTSECRET`** (constant `SolutionConstants.NewsServiceAzureClientSecretEnvVar`) instead of `AzureBlob:ClientSecret`. **Fail-closed:** a missing or empty variable throws `InvalidOperationException` (message names the variable) and the callers log at Error — no fallback to the registry secret, no unauthenticated run. Read behaviour: the code reads with `EnvironmentVariableTarget.Machine`, which reads the value **live from the registry**, so a running service picks up a newly set or changed value on its next authentication attempt — a restart is not strictly required. A process-inherited (non-Machine-scope) read, by contrast, would see only the environment captured at service start and would require a restart (the general Windows expectation for env-var changes); restarting after setting the variable therefore remains the recommended, unambiguous practice, robust if the read behaviour ever changes. **Convenience, not security** — a machine env var is clear text readable by any SYSTEM process, exactly like the registry `REG_SZ` secret; Azure Key Vault remains the intended secure path. NewsCentral is excluded by design (interactive per-user app, no LocalSystem context) and its manifest does not offer the mode. Hermetic unit tests inject the env read through the factory's `readEnv` seam (`NewsService.Tests/AzureCredentialFactoryTests.cs`).

**Optional WinHTTP transport (`AzureProxyTransportFactory`).** A singleton, gated by `AzureBlob:UseWinHttpProxy` (default `false`). When on, it builds one shared `WinHttpHandler`-backed transport for Azure.Core (`HttpClientTransport`, used by the credential + blob reader) and one Graph `HttpClient` (`GraphClientFactory.Create(finalHandler: WinHttpHandler{UseWinHttpProxy})`, preserving Graph's retry/redirect/throttling middleware, used by the device + group clients) — so all blob + Graph traffic rides the machine WinHTTP proxy under Local System (see `docs/configuration.md` / `docs/azure-setup.md`). When off, both members are null, `WinHttpHandler` is never instantiated, and every SDK is constructed exactly as before. Only transport construction is affected — credential selection, resolvers, `checkMemberGroups`, and team selection are unchanged.
