# NewsCentral — Component Specification

**Type:** .NET 10 MAUI Blazor Hybrid desktop application  
**Status:** Active development. Admin page, assignments, publishing workflow, Azure blob distribution, UseVirtualDesktop/VirtualDesktopBackgroundColor UI, and Phase B2 Key Management page implemented.

## Responsibilities

- Content creation: Presentations, Schedules, Assignments
- Approval and publishing workflow
- Team, user, and role management
- Index generation (`index.json` per team) via `IndexGenerationService`
- Uploading published content to the network repository (file share or Azure Blob, switchable)

## Key Services

| Service | Purpose |
|---|---|
| `PresentationService` | CRUD for presentations |
| `AssignmentService` | Assignment lifecycle and approval workflow |
| `PublishingService` | Publishes approved assignments to the repository |
| `IndexGenerationService` | Generates and writes `index.json` for each team; signs the index via `EcdsaSignatureService` using the team's private key from `team-signing.json`; key file ABSENT → published unsigned with a warning; key file present but UNREADABLE → generation and publish fail with an error naming the team and reason (`SigningKeyUnreadableException`, `SigningKeyGate`). Keys are read from the secure store via `TeamKeyService` (see `docs/anti-tamper.md` §9). Public methods: `LoadTeamSigningKeysAsync` (returns a `TeamKeyLoadResult`), `SaveTeamSigningKeysAsync`, `EnsureSigningKeyUsableAsync` (publish pre-flight; used by `KeyManagement.razor`). |
| `EcdsaSignatureService` | Signs `TeamIndexFile` with per-team ECDSA P-256; stateless singleton; index is normalized through `GetIndexJsonOptions` before signing |
| `HmacService` | Registered singleton; no longer used for index signing in NewsCentral — session telemetry signing is done by NewsViewer |
| `LocalStorageService` / `IStorageService` | File I/O abstraction |
| `LocalBlobDistributionService` / `AzureBlobDistributionService` | Distribution backends |
| `TeamContextService` | Current team scope for the session |
| `AuthenticationService` | Login, UPN detection, role resolution. The `LastLogin` write is best effort (a read-only users.json does not block login); the preferred UI language lives in the per-user local store (`UserPreferenceStore`), with the old users.json value read once as a fallback. Records `LoginSucceeded` / `LoginFailed` / `Logout`. |
| `TeamKeyService` | Loads/saves team signing keys in the secure store (`SecurePath`), migrates them from the old DataPath location (verified copy, then delete), and keeps migration problems for administrators. See `docs/anti-tamper.md` §9 |
| `AuditLog` (`IAuditLog`) | Append-only audit writer; see **Audit log** below |
| `DataSeederService` | M5b: no longer seeds anything — memoized `config/users.json` existence check (`Ready` / `NotInitialized`) plus `Invalidate(dataPath)` for the setup wizard to force a re-check |

## Key UI Pages

| Page | Path | Purpose |
|---|---|---|
| `EditPresentation.razor` | `/presentations/edit/{id}` | Edit name, description, URL, display types; set `UseVirtualDesktop` checkbox (defaults to `true` for new presentations) and `VirtualDesktopBackgroundColor` color picker; optionally generate a poster |
| `CreateAssignment.razor` | `/presentations/{id}/assign` | Set schedule dates (default start = today, all 7 days selected), select target teams, set approval requirement. Display-type badges (News-of-Week / Wallpaper / Logon Screen) are shown **read-only** here — display types are set on the presentation (see `EditPresentation.razor`), never on the assignment. |
| `KeyManagement.razor` | `/key-management` | TeamAdmin-gated (SystemAdmin passes automatically). View current public key (full, copyable for GPO/registry deployment) and truncated private key hint. Generate a new ECDSA P-256 key pair (held in page state). Apply — sets `PublicKeyPrevious = old PublicKey` for rotation-window continuity and writes `team-signing.json` to the secure store (never through `IStorageService` or `IBlobDistributionService`). An UNREADABLE key shows the reason and hides Generate/Apply (replacing it would orphan the fleet); migration problems and folder-permission drift warnings are shown here. Republish — separate deliberate action: re-signs and saves `index.json` via `GenerateAndSaveIndexAsync` (self-verify guard runs). Nav item hidden when no team is selected or user lacks TeamAdmin. Import-from-authority (paste/derive) deferred. |
| `Approvals.razor` | `/approvals` | Pending assignments grouped into batches (by source team, presentation and schedule; collapsed by default past 5 rows). Bulk Approve and bulk Reject, each sequential with a Stop control and a confirmation dialog; a cached poster thumbnail per batch, full image only in its lightbox. |
| `Assignments.razor` | `/assignments` | Pending/Approved/Published, each batched the same way. Bulk Publish (Approved only) and bulk Delete (all three sections), sequential with Stop; every delete — bulk or single-row — confirms first, and a Published delete's confirmation names that the poster is withdrawn from target devices at their next sync. Delete is offered only where the current user is eligible — see Role Model. The Published section also carries the poster-statistics bar and per-batch view/click figures — see "Statistics processing". |
| `IndexManagement.razor` | `/index-management` | SystemAdmin only. Regenerate one team's index, or all teams'. **Repair published assignment copies** (PUB-1) scans every team for a cross-team copy of an assignment and, per copy: **Sync** (source is Published, copy is stale → rewrite from source), **Remove** (source record no longer exists → move copy to `deleted`), **InSync** (copy already matches → no write), or **Left** (source exists but isn't Published → report only, untouched). The same repair also removes orphaned **presentation** copies (and their schedules) in a target team whose source presentation no longer exists (Duplicate-created presentations are never touched). Every team whose copies changed gets its index regenerated once at the end. |

## Storage Backend

- `DistributionMode` in `appsettings.json`: `Local` (file share) or `Azure` (Blob Storage)
- Authentication to Azure: interactive MSAL with token cache for subsequent non-interactive authentication

## Statistics processing

`PosterStatisticsService` (wrapping `PosterStatisticsEngine` in NewsCentral.Shared) turns the flat telemetry inbox on the distribution share into per-presentation view/click counts. There is no server, so the authoring app is the aggregator. The UI is in the Assignments page (below). Storage layout, definitions and file shapes: `docs/data-model.md` ("Poster statistics").

One run, in order: **guards** (exclusive `telemetry\.lock`, stale after 2 h; `telemetry\owner.json` binds the distribution to one DataPath) → **recover** (finish the moves a crashed run had already counted, before reading anything new) → **process** (stream `uploads\session-*.json`, then any legacy M1 `new\` files, in batches of 500: bounded-parallel read and classify, serial stats save with `PendingMove`, move straight to `{team}\{presentation}\`, clear `PendingMove`; invalid → `_invalid\`, re-upload of a counted file → deleted) → **prune** (13-month raw retention). Each file is read once and moved once.

- **Cancellable and resumable.** Cancellation is checked between batches and during the read step; once a batch's stats are saved its moves always complete. A stopped or failed run is simply re-run. `PendingMove` plus the Recover phase guarantee a crash between "counted" and "moved" never double counts.
- **Not available** for Azure Blob distribution, disabled distribution, or unreadable environment settings (the service returns `NotAvailable` and touches nothing).
- Moves are plain same-share `File.Move`; a failed move is skipped, never copy+delete. A lost share fails the run, keeping the counts so far.
- The HMAC is not re-verified (NewsService already discarded invalid records). No new registry or appsettings keys.

### Statistics in the Assignments panel (Published section)

- **Statistics bar** (shown when at least one Published batch exists): the last-run status (from `_run.json`: Completed with the file count, "Stopped, run again to continue", Failed with its message such as an owner mismatch or "already running", or "Statistics not processed yet"), a **Process statistics** button, and a **Process on open** checkbox. For Azure Blob or disabled distribution the bar reads "Statistics are available only for file-share distribution" and offers no button.
- **Running.** The button becomes a panel with the phase, the number of files handled (the total is unknown, so the bar is indeterminate) and **Stop**; Stop cancels the pass and the panel reads "Stopping…" until the engine returns. Progress renders are throttled to about 4 per second. A pass is independent of the bulk Publish/Delete run: neither sets or checks the other's running state.
- **Process on open** is a per-operator, per-machine boolean (default off) kept in the local `user-environments.json` (`ProcessStatisticsOnOpen`), not a registry or appsettings key. When on, a pass starts automatically once the page has loaded and statistics are available.
- **Batch figures.** Each Published batch header shows `1,234 views · 56 clicks (4.5%)`, "0 views" (data exists but none since the date) or "No data yet", with an ellipsis until loaded; figures load asynchronously after the page's data and are re-read after every pass that ends Completed or Stopped. Statistics are per presentation (source team + presentation id), **not per target team**, so a batch shows the presentation's figures across all target teams, "since" the UTC date of the **earliest** `PublishedDate` among its assignments. Day granularity: views earlier on that publish day are included. Views are poster displays, not unique viewers; clicks are Intranet-button clicks.
- **Leaving the page** cancels a running pass (without waiting for it). The engine resumes safely from where it stopped, so nothing is lost or double counted.

## Audit log

An append-only record of security and content actions, written by the **services** (not the pages), so every UI
path is covered. Failed operations are recorded too, with outcome `Failure`.

**Settings.** `environment.json`: `auditPath` (default `{DataPath}\audit`) and `audit.enabled` (default **true**),
edited on Environment Management (administrators). The registry value
`HKLM\Software\{Company}\NewsCentral\NewsCentral\Audit\Enabled` (REG_DWORD, `Set-RegistryOverrides.ps1
-AuditEnabled`) overrides the environment value; when it is present the toggle is shown locked ("set by policy").

**File.** `{AuditPath}\{yyyy-MM}\{yyyy-MM-dd}_{MACHINE}_{windowsUser}.jsonl` (UTC date; the user name is sanitized for
file names). Each event opens the file, appends **one** JSON line and closes it again, requesting
`AppendData | Synchronize` only (never `WriteData`), `FileShare.ReadWrite`, serialized within the process — so it keeps
working under the append-only ACL that `Set-AuthoringAcl.ps1` applies.

**Line fields** (camelCase; optional ones are omitted): `utc`, `machine`, `windowsAccount`, `appUser` (or
`attemptedUser` for a failed login), `environment` (display name, else DataPath), `event`, `target` (team /
presentation id / user id / setting name), `outcome` (`Success` | `Failure`), `detail` (short reason, cut at 200
characters, single line). Never passwords, hashes, private keys or full content; key events carry only a **public-key
fingerprint**.

**Events.** `LoginSucceeded`, `LoginFailed`, `Logout`; `UserCreated`, `UserUpdated`, `UserDeleted`,
`UserRolesChanged`, `UserTeamAssigned`, `UserTeamRemoved`, `UserUpnChanged`, `PasswordChanged`; `KeyGenerated`
(team had no key), `KeyRotated` (a key was replaced), `KeyMigrated`; `PresentationCreated`,
`PresentationDuplicated`, `PresentationUpdated`, `PresentationDeleted`; `AssignmentPublished`, `AssignmentDeleted`
(there is no unpublish operation); `IndexRegenerationFailed` (outcome Failure, target = team, detail = reason: the index after a delete or edit was not updated because the signing key was unreadable); `EnvironmentAdded`, `EnvironmentDeleted` (removed from the environment list),
`EnvironmentInitialized`, `EnvironmentSettingsChanged` (changed field names only); `AuditEnabled`, `AuditDisabled`.

**Toggling.** `AuditDisabled` is written **before** logging stops and `AuditEnabled` **after** it starts
(`AuditToggle`). If saving the setting fails after `AuditDisabled` was written, a second `AuditDisabled` line with
outcome `Failure` ("not applied") is added.

**Failure.** When enabled and a write fails, the action still completes (auditing never blocks anything); the
failure is logged and a persistent banner in the main layout reads "Audit log not writable — actions are not being
recorded" until a later write succeeds.

## Unreadable signing key: where it is shown

`UserNotices` (singleton) carries messages from work done behind an operation that itself succeeded; `MainLayout`
renders them as dismissible banners on every page. `IndexGenerationService.RegenerateInBackgroundAsync` (used after an
assignment delete and after a presentation update or delete) never throws: an unreadable signing key becomes the
warning "The distributed index for <team> was not updated because its signing key could not be read." plus an
`IndexRegenerationFailed` audit line, the old index stays and no unsigned index is written; other failures are logged
as before. Publishing fails up front instead (`EnsureSigningKeyUsableAsync`) with "Signing key for <team> could not be
read: <reason>. Nothing was published ...", shown at the top of the Assignments page (sticky) or in the bulk result.
The Index Management repair summary and the Regenerate All result list per-team failures themselves.

## users.json write restriction

After provisioning, `config/users.json` is writable by administrators only (`docs/anti-tamper.md` §9). What an
author without write access sees: login works (the last-login timestamp is simply not recorded); changing the UI
language works and survives a restart (per-user local store `user-preferences.json`, next to
`user-environments.json` in `%LOCALAPPDATA%`); the Change Password page says "Your password can only be changed by
an administrator."

**User management is SystemAdmin-only (S02a).** A TeamAdmin who could write users.json could make themselves a
SystemAdmin, so every `UserService` method that writes it (create user, update user, change UPN, assign/remove a
team member, change team roles, delete user) requires `IsSystemAdmin` and throws `UnauthorizedAccessException`
otherwise; the refusal is recorded in the audit log with outcome Failure. The check lives in the service, not only
in the UI. On the Users page a non-SystemAdmin sees the list read-only, with no write controls and the message
"User changes can only be made by a system administrator." When users.json cannot be read at all (access denied),
the **login page** says "You don't have access to this environment's user list. Ask an administrator to add you to
the NewsCentral authors group." instead of the raw .NET message (`UsersFileNotReadableException`). The self-service paths are unchanged (best-effort `LastLogin`, language in the local store, own password
change explained as above).

## Shared environment directory (`config/environments.json`)

Authors read, administrators write. The file decides which environments every user's picker offers, so an
unauthenticated write path would let anyone add an entry pointing at a DataPath they control, and would bypass the rule
that Add/Remove is admin-only. `EnvironmentDirectoryService.SyncSharedDirectoryAsync` therefore always merges the file
into the machine's local cache (`user-environments.json`), but writes the merged file back (temp file plus move) only
while a **SystemAdmin is signed in**. The login page runs the sync when it loads (nobody signed in: read only) and
`CompleteLoginAsync` runs it again after a SystemAdmin signs in, which is when the write-back happens. A non-admin
session never writes the file and never leaves a temp file behind. With `Set-AuthoringAcl.ps1` the whole `config\`
folder is also read-only for authors (`docs/anti-tamper.md` §9), so the rule holds at the file system as well.

## Role Model

- `SystemAdmin` — full access across all teams
- `ContentAuthor` — creates and edits content within their team; edit/delete restricted to creator or SystemAdmin

**Assignment delete eligibility (any status)** — SystemAdmin, the assignment's creator, or a
ContentAuthor of the assignment's **source** team (`AssignmentService.DeleteAssignmentAsync`'s own
check; the Assignments page mirrors it exactly). This is **not** the currently viewed team — a
ContentAuthor of a team that only *receives* an assignment cannot delete it.

**Assignments are immutable after creation** — there is no edit path. To change a scheduled assignment, delete it and create a new one.

## Poster Thumbnails

Approvals and Assignments render one small cached thumbnail per distinct presentation
(`PosterThumbnailCache`, an LRU-bounded cache of JPEGs scaled from the presentation's embedded
image) rather than embedding the full image per row — the pattern that previously made a large
batch (~200 assignments) exceed Blazor's render-batch size. The full-size image is produced only
when a thumbnail's lightbox is opened, and discarded again when it closes.

## Folder Convention

Each team folder in the repository contains:
- `content\presentations\` — presentation JSON files
- `content\schedules\` — schedule JSON files
- `content\assignments\` — assignment JSON files
- `images\original\` — uploaded source images
- `images\generated\` — AI-generated or processed images (reserved)
- `index.json` — generated team index
- ~~`team-signing.json`~~ — no longer in the team folder: the ECDSA P-256 key pair (`TeamSigningKeys`) lives in the secure store at `{SecurePath}\keys\{team}\team-signing.json` (authoring tier only; never distributed via `IBlobDistributionService`)

---

# NewsCentral.Shared — Component Specification

**Type:** .NET 10 class library  
**Status:** Implemented.

Contains all shared domain models. Referenced by NewsCentral, NewsService, and (future) NewsViewer and NewsTester. No platform dependencies — targets plain `net10.0` so it is compatible with any .NET 10 project regardless of platform target.

See `docs/solution-structure.md` for the full file layout and `docs/data-model.md` for all model definitions.
