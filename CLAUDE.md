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
| NewsService | Cache agent — syncs content from repository to local machine, applies **both** display surfaces machine-wide via PersonalizationCSP — the lock screen and the desktop wallpaper image (three-state: writes when content changes, clears its own stale content, never touches a value it did not write; re-verified and published to a protected non-user-writable folder before apply; each independently disabled via `Delivery:LockScreenEnabled`/`Delivery:WallpaperEnabled` — RDS/VDI opt-out, not a revert), uploads telemetry | .NET 10 Windows Service |
| NewsViewer | End-user presentation layer — displays scheduled content from local cache; re-asserts the per-user desktop wallpaper **style** only (HKCU, no `SystemParametersInfo`) — the wallpaper image itself is owned by NewsService | .NET 10 WinForms desktop app |
| NewsTester | Content preview tool for authors and approvers | Future — independent desktop app |

---

## Architecture Principles

- All components are configurable via `appsettings.json`. Registry values (`HKLM\Software\[Company]\NewsCentral\`) override `appsettings.json`. Each component has its own subkey (sole exception: NewsViewer reads NewsService's `Delivery` flags, read-only).
- Storage backend (local file share vs. Azure Blob Storage) is switchable via registry without code changes.
- Local/file share mode is the **primary development and testing configuration**. No Azure dependency is required for full functional testing.
- HMAC-SHA256 anti-tamper for session telemetry — NewsViewer signs, NewsService verifies; `index.json` signing moved to per-team ECDSA (see below).
- ECDSA P-256 per-team `index.json` signing — Phases A, B1 & C complete: signing core in `NewsCentral.Shared`, `IndexGenerationService` signs with team key, `SyncService` and `PresentationSelector` verify via `SigningKeyConfigurationReader`. B2 (Key Management page) complete; D (registry scripts) implemented in `scripts/Set-RegistryOverrides.ps1` at dev/pilot scope, GPO owns fleet configuration; see `docs/security.md`.
- All domain models live in **NewsCentral.Shared** — no model duplication across projects.
- NativeAOT migration path is preserved for NewsViewer.
- Fleet components ship **framework-dependent** — .NET 10 Desktop Runtime (x64) required on every target machine (declared prerequisite, not bundled). See `docs/packaging.md`.
- **Entra dynamic-team resolution (NewsService-only, behind `Entra:Enabled`; default off)** — resolves dynamic teams from named **instances** of two source kinds (attribute schemes, group-membership instances + a fleet-wide global exclusion), unioning them with the static registry team list on both consuming tiers; each instance graces independently. See the **Entra Device Team Resolution** section below and `docs/entra-dynamic-teams.md`.

### Data Flow

```
[Network Repository] → [NewsService] → [%programdata%\NewsCentral\ cache] → [NewsViewer]
                                ↑                                                    |
                                └──────────── uploads\session-*.json ───────────────┘
```

---

## Entra Device Team Resolution

NewsService-only feature, gated by `Entra:Enabled` (default `false`). Each poll cycle NewsService reads **this machine's** Entra device via Microsoft Graph and resolves dynamic teams from named **instances** of two source kinds, then writes `{CacheRootPath}\resolved-teams.json`. Both NewsService and NewsViewer **union** that set with their static (registry) team list; NewsViewer ignores `Source`/`SourceId` entirely and reads only `TeamFolderName`. Grace and identity are keyed `(Source, SourceId)` — **per instance**, not merely per source kind — so removing (or retargeting) an instance from configuration prunes it **without grace** on the next cycle: the configured instance set is read locally and is authoritative even during a Graph outage. Pure logic lives in `NewsCentral.Shared` and is unit-tested. Full detail, including the reasoning behind each fail-closed choice: `docs/entra-dynamic-teams.md`.

**Attribute schemes** (`Entra:AttributeSchemes`) — zero or more named schemes, each with its own selector attribute (`extensionAttribute1`..`15`) and selector→rule mapping table. A rule may reference `extensionAttribute1..15` except its own scheme's selector; an empty referenced attribute or a self-referencing rule aborts that scheme (no team). One team max per scheme; `SourceId` = the scheme name as authored.

**Group teams** (`Entra:GroupTeams`) — zero or more named `Instances`, plus one fleet-wide `ExclusionGroup`. Per instance: `inclusion ∧ ¬instanceExclusion ∧ ¬globalExclusion` → one team. Both `SourceId` and the team folder name are `Canonicalize(InclusionGroup)` — **derived, never authored** — so they cannot drift apart; the registry subkey is an operator-facing label only (diagnostics/logs), and renaming it retargets nothing. Colliding derived ids: first by label (ordinal-ignore-case) wins, warned. The global exclusion **fails closed**: an unresolvable name suppresses every group instance, with one unconditional `Error` per cycle — the safe failure mode for a kill switch is to keep killing, not silently stop. One batched `checkMemberGroups` call covers every active instance's names, chunked at 20 ids per call; a per-name failure isolates to the instance(s) referencing it, but a transport-level failure (unreachable/403) fails the whole batch rather than risk partial membership.

**Auth / Graph.** Single app registration shared with Azure Blob — same credential via `AzureCredentialFactory`; `AzureBlob:*` must be populated even when `StorageMode=Share`. Admin-consented permissions: `Device.Read.All`, plus `GroupMember.Read.All` for group teams (`Directory.Read.All` is a broad fallback). DeviceId must be a GUID or Graph is skipped. `Microsoft.Graph` v6 fluent SDK; extensionAttributes read from `AdditionalData` (v1.0 has no typed property; beta deliberately avoided). **Optional:** `AzureBlob:UseWinHttpProxy` routes blob **and** Graph through the machine WinHTTP proxy for reliable proxying under Local System; off = no behavior change.

**Grace, cap, logging.** `GraphFailureClassifier`: 403 or an authoritative "no team" answer is persistent (clean removal, 403 also logs `Error`); only transient Graph failures ride grace, per instance against its own `GracePeriodMinutes`/last-confirmed time. `Entra:MaxDynamicTeams` (default `16`, `0` = no cap) bounds the total written across both sources, truncating deterministically (every Attribute entry by `SourceId`, then every Group entry by `SourceId`) with a `Warning` naming the dropped ids — applied after the grace merge, never inside it. One change-gated summary line per cycle (`Information` on a changed written set, including the transition to empty; `Debug` otherwise) is the primary operator signal; per-instance `Debug` lines and every `Warning`/`Error` above are unconditional and unaffected by this gating. (→ `docs/configuration.md`)

**Trust model — Option A (key-with-content).** The team signing **public** key is embedded in `index.json` (`SigningPublicKey`) for **all** teams — static-vs-dynamic is a property of the *consuming machine*, not the team, so the key is inert for static consumers. Precedence (`SignatureGate.VerifyWithPrecedence`): registry key wins → else delivered key (**dynamic teams only**) → else unsigned per `RequireSignedIndex`. The delivered key is never used for static teams and never overrides a registry key. Dynamic teams are **rotation-free** (the key travels with the content). (→ `docs/security.md`)

**Status.** Structurally complete and offline/fake-tested, shipping behind `Entra:Enabled=false`. **Live validation on an Entra-joined box is still pending** — the unproven seams are the DeviceId read, the device fetch HTTP, and `EntraGroupClient`'s name→id + `checkMemberGroups` calls. Operational prerequisites before enabling: `Device.Read.All` consent (plus `GroupMember.Read.All` for group teams), device data provisioned across the fleet, verified cache ACLs, GPO deployment of the Entra registry config.

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

**JSON serialization** — two option sets; use the right one for the context.

*NewsService / NewsViewer* — general cache and telemetry I/O:
```csharp
new JsonSerializerOptions {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
}
```

*NewsCentral* — index file generation and signing (`JsonConfiguration.GetIndexJsonOptions()`):
```csharp
new JsonSerializerOptions {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(), new SmartDateTimeConverter() }
}
```
`SmartDateTimeConverter` writes UTC `DateTime` with a `Z` suffix and `Unspecified` (schedule times) without one, and truncates to whole seconds. **This truncation is what keeps the ECDSA signature byte-consistent with the persisted file** — without it, sub-second precision present in memory is stripped on write, causing every verifier to return `Invalid`. Verifiers (NewsService, NewsViewer) deserialize with standard ISO parsing, which faithfully round-trips whole-second timestamps.

**Assignments are immutable after creation.** There is no edit path. To change a scheduled assignment, delete it and create a new one.

**Numeric registry overrides that could legitimately be `0` or `1` MUST be provisioned as `REG_SZ`, never `REG_DWORD`.** `RegistryConfigurationProvider` coerces `REG_DWORD` `0`→`"False"` and `1`→`"True"`; the configuration binder then throws converting that string to `int`, crashing the component at startup. Applies to `Display:LogicalDayStartHour`; latent for `Service:PollIntervalSeconds`. See `docs/configuration.md`.

**Display types (`IsNewsOfWeek`/`IsWallpaper`/`IsLogonScreen`) are owned by `Presentation` and nothing else.** `Assignment` must never carry copies — the index's `DisplayTypeInfo` is sourced from the presentation, the single source of truth. Virtual Desktop does **not** exclude wallpaper or lock screen; those applies are independent of poster presentation.

**Registry `teams\` naming** — value names must match the generated folder name exactly, i.e. the sanitized team name (e.g. `cz-its`, not `CZ_ITS`). Folder names carry no `team-` prefix.

**`Company` is a single build-time constant, not configuration.** Authored once in `Directory.Build.props` (`<Company>`), surfaced to code as `SolutionConstants.Company` (generated into `NewsCentral.Shared`), and consumed by all three components; an MSBuild target fails the build if it is empty. It is **absent from every `appsettings.json`** — no runtime default, no fallback — and is **not** registry-overridable (it defines the hive path `HKLM\Software\{Company}\NewsCentral\{Component}`). A `Company` **mismatch fails silently**: `OpenSubKey` returns `null` with no error, so every GPO/registry override is quietly ignored and the component runs on shipped defaults (the exact drift that motivated making it one constant). See `docs/configuration.md`.

**NewsViewer startup** — `appsettings.json` is required (`optional: false`); the `appsettings.Development.json` overlay is `optional: true`. Missing base file = hard startup failure. `Main()` checks the `Active` master switch first (registry DWORD 0/1, default `true`) and returns silently if `false` — no poster, no wallpaper style re-assert, no state write, no dialog. `Main()` then validates `CacheRootPath` (not `Company` — that is the build constant) and exits with `MessageBox` if it is empty.

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

No direct inter-process communication. All coordination is via the shared cache folder structure.

---

## Operational Requirements

| Component | Requirement |
|---|---|
| NewsCentral | User-initiated start and shutdown; interactive Azure authentication |
| NewsService | Automatic start as Windows Service; runs unattended; handles weeks-long uptime |
| NewsViewer | Auto-start via HKLM Run and Task Scheduler (Workstation Unlock); optional Start menu entry |
| All | Configurable via `appsettings.json` with registry override under `HKLM\Software\[Company]\[NewsCentral]\` |

---

## Non-Goals

- No third-party UI frameworks for wallpaper or lockscreen management; Microsoft APIs only
- No peer-to-peer communication between components; all coordination via shared file system
- No real-time push from server to client; polling-based model throughout
- No Azure dependency required for local development and testing

---

## Reference Documents

These are not auto-loaded. Read the ones a task touches; a prompt's READ FIRST list is authoritative.

| Document | Read when |
|---|---|
| docs/anti-tamper.md | index signing, integrity values, key trust |
| docs/azure-setup.md | Azure/Entra app regs, RBAC, Graph consent |
| docs/config-review-spec.md | the config review page in NewsCentral |
| docs/configuration.md | any registry/appsettings key or its type |
| docs/data-model.md | models, index.json, resolved-teams.json schema |
| docs/entra-dynamic-teams.md | Entra attribute/group team resolution |
| docs/future.md | deferred work, NewsTester |
| docs/newscentral-spec.md | authoring app services, pages, roles |
| docs/newsservice-spec.md | sync cycle, display surfaces, telemetry |
| docs/newsviewer-spec.md | poster, daily gate, wallpaper style |
| docs/packaging.md | installer, uninstall, upgrade behavior |
| docs/production-deployment.md | GPO rollout and pilot validation |
| docs/security.md | signing phase history, auth per component |
| docs/solution-structure.md | project/file layout and references |
