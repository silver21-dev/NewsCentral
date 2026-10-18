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
| `IndexGenerationService` | Generates and writes `index.json` for each team; signs the index via `EcdsaSignatureService` using the team's private key from `team-signing.json`; missing key → published unsigned with a warning. Public methods: `LoadTeamSigningKeysAsync`, `SaveTeamSigningKeysAsync` (used by `KeyManagement.razor`). |
| `EcdsaSignatureService` | Signs `TeamIndexFile` with per-team ECDSA P-256; stateless singleton; index is normalized through `GetIndexJsonOptions` before signing |
| `HmacService` | Registered singleton; no longer used for index signing in NewsCentral — session telemetry signing is done by NewsViewer |
| `LocalStorageService` / `IStorageService` | File I/O abstraction |
| `LocalBlobDistributionService` / `AzureBlobDistributionService` | Distribution backends |
| `TeamContextService` | Current team scope for the session |
| `AuthenticationService` | Login, UPN detection, role resolution |
| `DataSeederService` | M5b: no longer seeds anything — memoized `config/users.json` existence check (`Ready` / `NotInitialized`) plus `Invalidate(dataPath)` for the setup wizard to force a re-check |

## Key UI Pages

| Page | Path | Purpose |
|---|---|---|
| `EditPresentation.razor` | `/presentations/edit/{id}` | Edit name, description, URL, display types; set `UseVirtualDesktop` checkbox (defaults to `true` for new presentations) and `VirtualDesktopBackgroundColor` color picker; optionally generate a poster |
| `CreateAssignment.razor` | `/presentations/{id}/assign` | Set schedule dates (default start = today, all 7 days selected), select target teams, set approval requirement. Display-type badges (News-of-Week / Wallpaper / Logon Screen) are shown **read-only** here — display types are set on the presentation (see `EditPresentation.razor`), never on the assignment. |
| `KeyManagement.razor` | `/key-management` | TeamAdmin-gated (SystemAdmin passes automatically). View current public key (full, copyable for GPO/registry deployment) and truncated private key hint. Generate a new ECDSA P-256 key pair (held in page state). Apply — sets `PublicKeyPrevious = old PublicKey` for rotation-window continuity and writes `team-signing.json` via `IStorageService` (never through `IBlobDistributionService`). Republish — separate deliberate action: re-signs and saves `index.json` via `GenerateAndSaveIndexAsync` (self-verify guard runs). Nav item hidden when no team is selected or user lacks TeamAdmin. Import-from-authority (paste/derive) deferred. |
| `Approvals.razor` | `/approvals` | Pending assignments grouped into batches (by source team, presentation and schedule; collapsed by default past 5 rows). Bulk Approve and bulk Reject, each sequential with a Stop control and a confirmation dialog; a cached poster thumbnail per batch, full image only in its lightbox. |
| `Assignments.razor` | `/assignments` | Pending/Approved/Published, each batched the same way. Bulk Publish (Approved only) and bulk Delete (all three sections), sequential with Stop; every delete — bulk or single-row — confirms first, and a Published delete's confirmation names that the poster is withdrawn from target devices at their next sync. Delete is offered only where the current user is eligible — see Role Model. |
| `IndexManagement.razor` | `/index-management` | SystemAdmin only. Regenerate one team's index, or all teams'. **Repair published assignment copies** (PUB-1) scans every team for a cross-team copy of an assignment and, per copy: **Sync** (source is Published, copy is stale → rewrite from source), **Remove** (source record no longer exists → move copy to `deleted`), **InSync** (copy already matches → no write), or **Left** (source exists but isn't Published → report only, untouched). The same repair also removes orphaned **presentation** copies (and their schedules) in a target team whose source presentation no longer exists (Duplicate-created presentations are never touched). Every team whose copies changed gets its index regenerated once at the end. |

## Storage Backend

- `DistributionMode` in `appsettings.json`: `Local` (file share) or `Azure` (Blob Storage)
- Authentication to Azure: interactive MSAL with token cache for subsequent non-interactive authentication

## Statistics processing

`PosterStatisticsService` (wrapping `PosterStatisticsEngine` in NewsCentral.Shared) turns the flat telemetry inbox on the distribution share into per-presentation view/click counts. There is no server, so the authoring app is the aggregator. No UI yet (M2). Storage layout, definitions and file shapes: `docs/data-model.md` ("Poster statistics").

One run, in order: **guards** (exclusive `telemetry\.lock`, stale after 2 h; `telemetry\owner.json` binds the distribution to one DataPath) → **sort** (stream `uploads\session-*.json`; invalid → `_invalid\`, re-upload of a counted file → deleted, else moved to `{team}\{presentation}\new\`) → **count** (batches of 500 folded into day buckets; each batch is saved with `PendingMove` before its files are moved) → **prune** (13-month raw retention).

- **Cancellable and resumable.** The sort move is the commit, so stopping loses nothing; a stopped or failed run is simply re-run. `PendingMove` guarantees a crash between "counted" and "moved" never double counts.
- **Not available** for Azure Blob distribution, disabled distribution, or unreadable environment settings (the service returns `NotAvailable` and touches nothing).
- Moves are plain same-share `File.Move`; a failed move is skipped, never copy+delete. A lost share fails the run, keeping the counts so far.
- The HMAC is not re-verified (NewsService already discarded invalid records). No new registry or appsettings keys.

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
- `team-signing.json` — ECDSA P-256 key pair (`TeamSigningKeys`); authoring tier only; never distributed via `IBlobDistributionService`

---

# NewsCentral.Shared — Component Specification

**Type:** .NET 10 class library  
**Status:** Implemented.

Contains all shared domain models. Referenced by NewsCentral, NewsService, and (future) NewsViewer and NewsTester. No platform dependencies — targets plain `net10.0` so it is compatible with any .NET 10 project regardless of platform target.

See `docs/solution-structure.md` for the full file layout and `docs/data-model.md` for all model definitions.
