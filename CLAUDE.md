# NewsCentral — Solution Specification

**Version:** 2.7  
**Status:** Implementation in progress  
**Scope:** NewsCentral, NewsCentral.Shared, NewsService, NewsViewer, NewsTester

---

## Components at a Glance

| Component | Role | Type |
|---|---|---|
| NewsCentral | Content authoring, approval, scheduling, publishing, user/team management | .NET 10 MAUI Blazor Hybrid desktop app |
| NewsCentral.Shared | Shared domain models referenced by all components | .NET 10 class library |
| NewsService | Cache agent — syncs content from repository to local machine, applies **both** display surfaces machine-wide via PersonalizationCSP — the lock screen and the desktop wallpaper image (three-state: writes when content changes, clears its own stale content, never touches a value it did not write; re-verified and published to a protected non-user-writable folder before apply; each independently disabled via `Delivery:LockScreenEnabled`/`Delivery:WallpaperEnabled` — RDS/VDI opt-out, not a revert), uploads telemetry, writes a ContentChanged EventLog event (2001) once per cycle that accepted new content, and **verifies the NewsViewer scheduled tasks and never creates or repairs them** (read-only `schtasks.exe /Query` against the embedded `deploy/scheduled-tasks` XMLs; `ViewerTasks:Mode` = `VerifyOnly` or `Off`; the installer owns creation and removal; the registration script is a build artifact from `--emit-task-script`), creates the EventLog source explicitly at startup (`ViewerTasks:RegisterEventSource`) | .NET 10 Windows Service |
| NewsViewer | End-user presentation layer — displays scheduled content from local cache; re-asserts the per-user desktop wallpaper **style** only (HKCU, no `SystemParametersInfo`) — the wallpaper image itself is owned by NewsService; a pure ordered launch decision (per-user marker, `launch.log`) chooses the full poster, the small "News arrived!" notification, or nothing, and runs in remote sessions too | .NET 10 WinForms desktop app |
| NewsTester | Content preview tool for authors and approvers | Future — independent desktop app |

---

## Architecture Principles

- All components are configurable via `appsettings.json`. Registry values (`HKLM\Software\[Company]\NewsCentral\`) override `appsettings.json`. Each component has its own subkey (sole exception: NewsViewer reads NewsService's `Delivery` flags, read-only).
- Storage backend (local file share vs. Azure Blob Storage) is switchable via registry without code changes.
- Local/file share mode is the **primary development and testing configuration**. No Azure dependency is required for full functional testing.
- HMAC-SHA256 anti-tamper for session telemetry — NewsViewer signs, NewsService verifies; `index.json` signing moved to per-team ECDSA (see below).
- ECDSA P-256 per-team `index.json` signing — Phases A, B1 & C complete: signing core in `NewsCentral.Shared`, `IndexGenerationService` signs with team key, `SyncService` and `PresentationSelector` verify via `SigningKeyConfigurationReader`. Static-team key rotation uses `PublicKeyPrevious` (dual-key, `docs/anti-tamper.md`). B2 (Key Management page) complete; D (registry scripts) implemented in `scripts/Set-RegistryOverrides.ps1` at dev/pilot scope, GPO owns fleet configuration; see `docs/security.md`.
- All domain models live in **NewsCentral.Shared** — no model duplication across projects.
- NativeAOT migration path is preserved for NewsViewer: new P/Invoke code uses `LibraryImport`, while the legacy `NativeMethods.cs` and `WallpaperService.cs` still use `DllImport` (blittable, AOT-compatible) until they are migrated; `AllowUnsafeBlocks` is enabled only for the generated stubs; no hand-written unsafe code; no COM.
- Fleet components ship **framework-dependent** — .NET 10 Desktop Runtime (x64) required on every target machine (declared prerequisite, not bundled). See `docs/packaging.md`.
- **Entra dynamic-team resolution (NewsService-only, behind `Entra:Enabled`; default off)** — resolves dynamic teams from named **instances** of two source kinds (attribute schemes, group-membership instances + a fleet-wide global exclusion), unioning them with the static registry team list on both consuming tiers; each instance graces independently. See the **Entra Device Team Resolution** section below and `docs/entra-dynamic-teams.md`.

### Data Flow

```
[Network Repository] → [NewsService] → [%programdata%\NewsCentral\ cache] → [NewsViewer]
                                ↑                                                    |
                                └──────────── uploads\session-*.json ───────────────┘
```

New content also triggers a launch: NewsService writes EventLog event 2001 (`ContentChanged`, once per cycle) → the "Content Arrived" scheduled task starts `NewsViewer.exe --trigger content`.

---

## Entra Device Team Resolution

NewsService-only, gated by `Entra:Enabled` (default `false`). Each poll cycle NewsService reads **this machine's** Entra device via Microsoft Graph, resolves dynamic teams from named **instances** of two source kinds (attribute schemes, group-team instances + one fleet-wide exclusion group), and writes `{CacheRootPath}\resolved-teams.json`. Both NewsService and NewsViewer **union** that set with their static (registry) team list; NewsViewer reads only `TeamFolderName`, never `Source`/`SourceId`. Invariants:

- Grace and identity are keyed `(Source, SourceId)` — **per instance**. The configured instance set is read locally and is authoritative: a removed or retargeted instance is pruned **without grace** on the next cycle, even during a Graph outage.
- Group instances: `SourceId` and team folder name are both `Canonicalize(InclusionGroup)` — **derived, never authored**. The registry subkey is a diagnostic label only.
- The global exclusion group **fails closed**: an unresolvable name suppresses every group instance.
- Key trust (Option A, key-with-content): registry key wins → else the delivered `SigningPublicKey` (**dynamic teams only**) → else unsigned per `RequireSignedIndex`. Dynamic teams are rotation-free.
- Single Azure app registration shared with Blob; `AzureBlob:*` must be populated even when `StorageMode=Share`.
- **Status:** structurally complete and offline/fake-tested, shipping behind `Entra:Enabled=false`; **live validation on an Entra-joined box is still pending**.

Everything else — attribute schemes, group teams, Graph permissions and batching, grace/cap/logging, trust model, status and prerequisites: `docs/entra-dynamic-teams.md` (config keys: `docs/configuration.md`; key trust: `docs/security.md`).

---

## Technologies

| Component | Technology |
|---|---|
| NewsCentral | C# / .NET 10 MAUI Blazor Hybrid — **Windows-only target (`net10.0-windows…`) by decision; do not re-add Android/iOS/MacCatalyst** |
| NewsCentral.Shared | C# / .NET 10 class library |
| NewsService | C# / .NET 10 Windows Service (`Microsoft.NET.Sdk.Worker`) |
| NewsViewer | C# / .NET 10 WinForms; NativeAOT migration path preserved |
| Data files | JSON (`System.Text.Json`); see Critical Rules for the two option sets used across components |
| Images | Base64-encoded and embedded in presentation JSON |
| Azure auth | MSAL interactive (NewsCentral); machine certificate from local store (NewsService) |

---

## Critical Rules

**JSON serialization** — two option sets. NewsService/NewsViewer use the general cache/telemetry set; NewsCentral index generation and signing uses `JsonConfiguration.GetIndexJsonOptions()`, whose `SmartDateTimeConverter` truncates timestamps to whole seconds — **that truncation keeps the ECDSA signature byte-consistent with the persisted file** (without it every verifier returns `Invalid`). Exact option sets: `docs/data-model.md`.

**Assignments are immutable after creation.** There is no edit path. To change a scheduled assignment, delete it and create a new one.

**Numeric registry overrides that could legitimately be `0` or `1` MUST be provisioned as `REG_SZ`, never `REG_DWORD`.** `RegistryConfigurationProvider` coerces `REG_DWORD` `0`→`"False"` and `1`→`"True"`; the configuration binder then throws converting that string to `int`, crashing the component at startup. Applies to `Display:LogicalDayStartHour`; latent for `Service:PollIntervalSeconds`. See `docs/configuration.md`.

**Display types (`IsNewsOfWeek`/`IsWallpaper`/`IsLogonScreen`) are owned by `Presentation` and nothing else.** `Assignment` must never carry copies — the index's `DisplayTypeInfo` is sourced from the presentation, the single source of truth. Virtual Desktop does **not** exclude wallpaper or lock screen; those applies are independent of poster presentation.

**Registry `teams\` naming** — value names must match the generated folder name exactly, i.e. the sanitized team name (e.g. `cz-its`, not `CZ_ITS`). Folder names carry no `team-` prefix.

**`Company` is a single build-time constant, not configuration.** Authored once in `Directory.Build.props` (`<Company>`), surfaced to code as `SolutionConstants.Company` (generated into `NewsCentral.Shared`), and consumed by all three components; an MSBuild target fails the build if it is empty. It is **absent from every `appsettings.json`** — no runtime default, no fallback — and is **not** registry-overridable (it defines the hive path `HKLM\Software\{Company}\NewsCentral\{Component}`). A `Company` **mismatch fails silently**: `OpenSubKey` returns `null` with no error, so every GPO/registry override is quietly ignored and the component runs on shipped defaults (the exact drift that motivated making it one constant). See `docs/configuration.md`.

**NewsViewer startup** — `appsettings.json` is required (`optional: false`) and `Main()` checks the `Active` switch first; details in `docs/newsviewer-spec.md`.

**Razor comments must never contain an `@` token or an HTML tag.** `@* … *@`/`<!-- … -->` must avoid `@media`/`@code` and tags like `<style>` — Razor parses comments too, and this has broken the build before. Write "media query"/"style block" in prose instead.

---

## Communication

| Path | Mechanism |
|---|---|
| NewsCentral → Repository | File share write or Azure Blob upload (switchable) |
| NewsService → Repository | File share read/write or Azure Blob read/write (switchable) |
| NewsService → Cache | Local file write to `%programdata%\NewsCentral\` |
| NewsViewer → Cache | Local file read from `%programdata%\NewsCentral\` |
| NewsViewer → NewsService | Via `uploads\` folder (session telemetry JSON files) |
| NewsService → NewsViewer | EventLog event 2001 (`ContentChanged`) → Task Scheduler "Content Arrived" task launches NewsViewer (`--trigger content`) |

No direct inter-process communication. Coordination is via the shared cache folder structure; the one signal is NewsService's `ContentChanged` EventLog event, which a scheduled task turns into a NewsViewer launch.

---

## Operational Requirements

| Component | Requirement |
|---|---|
| NewsCentral | User-initiated start and shutdown; interactive Azure authentication |
| NewsService | Automatic start as Windows Service; runs unattended; handles weeks-long uptime |
| NewsViewer | Auto-start via two Task Scheduler tasks registered by the installer (generated script) and verified by NewsService: logon/unlock/connect, and content-changed (no HKLM Run entry); optional Start menu entry |
| All | Configurable via `appsettings.json` with registry override under `HKLM\Software\[Company]\[NewsCentral]\` |

---

## Non-Goals

- No third-party UI frameworks for wallpaper or lockscreen management; Microsoft APIs only
- No peer-to-peer communication between components; all coordination via shared file system
- No real-time push from server to client; polling-based model throughout
- No Azure dependency required for local development and testing

---

## Working Rules

- Never stage `.gitignore` or `NewsViewer/appsettings.json`; stage by explicit path.
- Build/test: `dotnet build` and `dotnet test` at the repo root; both test suites must pass before a commit.
- NewsService may be running as a Windows service from `NewsService\bin\Debug`; it locks `NewsCentral.Shared.dll`. If a build fails with MSB3027 "locked by NewsCentral", ask the user to stop the service. Don't build into an alternate output path.
- Commit with `git commit -F <tempfile>` (not a long inline `-m`); report build and test results before committing.
- Never edit source files through PowerShell `Get-Content`/`Set-Content` without an explicit encoding: Windows PowerShell 5.1 reads them in the ANSI codepage and corrupts non-ASCII characters (emoji, box-drawing lines, dashes) and drops BOMs. Read and write UTF-8 with `[IO.File]::ReadAllText`/`WriteAllText` and a `UTF8Encoding`, and preserve an existing BOM. Prefer the Edit tool for source changes.

---

## Reference Documents

These are not auto-loaded. Read the ones a task touches; a prompt's READ FIRST list is authoritative.

| Document | Read when |
|---|---|
| docs/anti-tamper.md | index signing, integrity values, key trust |
| docs/authorization.md | roles, permission checks, folder ACLs and Azure RBAC for the authoring app |
| docs/azure-setup.md | Azure/Entra app regs, RBAC, Graph consent |
| docs/config-review-spec.md | the config review page in NewsCentral |
| docs/configuration.md | any registry/appsettings key or its type |
| docs/data-model.md | models, index.json, resolved-teams.json schema, JSON serialization option sets |
| docs/entra-dynamic-teams.md | Entra attribute/group team resolution, Graph mechanics, grace/cap/logging, key-trust and status |
| docs/future.md | deferred work, NewsTester |
| docs/newscentral-spec.md | authoring app services, pages, roles |
| docs/newsservice-spec.md | sync cycle, display surfaces, telemetry |
| docs/newsviewer-spec.md | launch decision, notification, poster, daily gate, remote sessions, frame colours, wallpaper style |
| docs/packaging.md | installer, uninstall, upgrade behavior |
| docs/production-deployment.md | GPO rollout and pilot validation |
| docs/security.md | signing phase history, auth per component |
| docs/solution-structure.md | project/file layout and references |
| deploy/scheduled-tasks/README.md, scripts/Register-NewsViewerTasks.ps1 | NewsViewer scheduled tasks (task XMLs, reference installer; the packaged script is a build artifact, docs/packaging.md) |
