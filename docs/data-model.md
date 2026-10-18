# Data Model

All models are defined in `NewsCentral.Shared` and implement `IEntity`.

## IEntity

```csharp
public interface IEntity
{
    string GetId();
    void SetId(string id);
}
```

## Signing Infrastructure

`ISignable` (in `NewsCentral.Security`) marks any type whose JSON payload is covered by a signature. Two signing services exist; both live in `NewsCentral.Shared/Security/`.

```csharp
public interface ISignable { string? Signature { get; set; } }

public enum VerifyResult { Disabled, Unsigned, Valid, Invalid }
```

**Canonical serialization** (identical across both services): non-indented camelCase JSON with `JsonStringEnumConverter`; `Signature` temporarily nulled before serialization so it is excluded from its own payload.

**Verification behavior** (shared semantics):

| Result | Meaning | Action taken |
|---|---|---|
| `Disabled` | No key configured | Accept and pass through |
| `Unsigned` | `Signature` is null | Accept with a warning log |
| `Valid` | Signature matches | Accept |
| `Invalid` | Signature present but does not match | Reject — content discarded / team skipped |

### HmacService

Symmetric shared-key signing; used for session telemetry (`session-*.json`).

```csharp
public class HmacOptions { public string SecretKey { get; set; } = string.Empty; }

public sealed class HmacService
{
    public bool IsEnabled { get; }                    // false when SecretKey is empty
    public string? Sign<T>(T entity) where T : ISignable;
    public VerifyResult Verify<T>(T entity) where T : ISignable;
}
```

`SecretKey` is a Base64-encoded 32-byte key in `Hmac:SecretKey`. An empty key disables HMAC — `Sign` returns `null`, `Verify` returns `Disabled`. Timing-safe comparison via `CryptographicOperations.FixedTimeEquals`.

### EcdsaSignatureService

Stateless asymmetric signing; foundation for per-team `index.json` signing. Key material is passed per call.

```csharp
public sealed class EcdsaSignatureService
{
    // Returns Base64 IEEE P1363 (64-byte r‖s). Does not modify entity.Signature.
    public string Sign<T>(T entity, string privateKeyBase64Pkcs8) where T : ISignable;

    // Pass [PublicKey, PublicKeyPrevious] for rotation-window support.
    // No non-empty keys → Disabled. Null Signature → Unsigned. Any key match → Valid. All fail → Invalid.
    public VerifyResult Verify<T>(T entity, params string?[] publicKeysSpkiBase64) where T : ISignable;
}
```

Algorithm: ECDSA nistP256, SHA-256, IEEE P1363 fixed-field concatenation (64-byte r‖s, Base64). A key attempt that throws is treated as that key failing.

### SigningKeyTool

Static helpers for key management UI and offline tooling.

```csharp
public static class SigningKeyTool
{
    public static (string PrivateKeyBase64, string PublicKeyBase64) GenerateKeyPair();
    public static string DerivePublicKey(string privateKeyInput);   // PEM or Base64 PKCS#8; throws ArgumentException on bad input
    public static string Truncate(string? key, int head = 12, int tail = 6);
}
```

## Presentation

The core content unit distributed to NewsViewer.

```csharp
public class Presentation : IEntity
{
    public string PresentationID { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }

    public string TeamID { get; set; }
    public string TeamFolderName { get; set; }

    public string OriginalImagePath { get; set; }
    public string? GeneratedImagePath { get; set; }
    public string ImageOriginalName { get; set; }
    public string ImageName { get; set; }

    public bool IsNewsOfWeek { get; set; }
    public bool IsWallpaper { get; set; }
    public bool IsLogonScreen { get; set; }

    public int DisplayDurationSeconds { get; set; }     // Countdown timer; no registry default
    public bool UseVirtualDesktop { get; set; }
    public string VirtualDesktopBackgroundColor { get; set; } = "#000000";

    public string ContentImageBase64 { get; set; }      // Base64-encoded image
    public string? PosterText { get; set; }
    public string MoreUrl { get; set; }

    public string CreatedBy { get; set; }
    public DateTime DateCreated { get; set; }
    public string ModifiedBy { get; set; }
    public DateTime LastModified { get; set; }
    public int Version { get; set; }
}
```

`DisplayDurationSeconds` defaults to `PresentationDefaults.DisplayDurationSeconds` (see below). `Presentation` is a plain `IEntity` — it no longer declares `ISignable` (its dead `Signature` property was removed; only `TeamIndexFile` and `SessionTelemetry` are signable).

## PresentationDefaults

Single source of truth for display-duration semantics (in `NewsCentral.Shared`, referenced by both the model default and the NewsViewer fallback so `30` is never hardcoded elsewhere).

```csharp
public static class PresentationDefaults
{
    public const int DisplayDurationSeconds = 30;   // default poster lifetime (seconds)
    public const int NeverAutoClose = -1;           // reserved sentinel (no authoring UI yet)

    // -1 → -1 (never auto-close); <= 0 → 30 (unset); > 0 → raw. The NeverAutoClose
    // check MUST precede the unset check so it is not swallowed.
    public static int ResolveDuration(int raw);
}
```

`ResolveDuration` is the only place a raw duration is interpreted; NewsViewer calls it once in `Program.cs`. See `docs/newsviewer-spec.md` → Display Duration.

## Schedule

```csharp
public class Schedule : IEntity
{
    public string ScheduleID { get; set; }
    public string PresentationID { get; set; }
    public string Version { get; set; }
    public DateTime ScheduleStart { get; set; }
    public DateTime ScheduleEnd { get; set; }
    public string DaysOfWeek { get; set; } = "1,2,3,4,5,6,7";  // Mon–Sun
    public bool IsActive { get; set; }
    public string CreatedBy { get; set; }
    public DateTime LastModified { get; set; }
}
```

The `DisplayMode` enum and `ShowMode` property were removed in the `6010cd1` schema break — there is only one display behaviour now (show once per logical day; see `docs/newsviewer-spec.md`).

## Assignment

```csharp
public class Assignment : IEntity
{
    public string AssignmentID { get; set; }
    public string PresentationID { get; set; }
    public string PresentationVersion { get; set; }
    public string ScheduleID { get; set; }
    public string SourceTeam { get; set; }
    public string TargetTeam { get; set; }
    public AssignmentStatus Status { get; set; }        // see enum below
    public bool RequiresApproval { get; set; }

    public int Priority { get; set; } = 0;              // RESERVED — see below

    // Audit trail
    public string CreatedBy { get; set; }
    public DateTime DateCreated { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public string? ApprovalNotes { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? PublishedDate { get; set; }
    public List<string> PublishedPaths { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledDate { get; set; }
    public string? CancellationReason { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedDate { get; set; }
    public string? RejectionReason { get; set; }
}

public enum AssignmentStatus
{
    Draft, PendingApproval, Approved, Published, Cancelled, Rejected
}
```

The display-type flags (`IsNewsOfWeek` / `IsWallpaper` / `IsLogonScreen`) were removed from `Assignment` in `91923e0`. **Display types are owned by `Presentation` and nothing else** — the assignment surfaces only read them. `Priority` is the reserved wire field described below.

### Reserved `Priority`

`Priority` is **RESERVED**: `0` = normal, ascending = more urgent. It is emitted by `IndexGenerationService` **inside the signed index payload** (so its bytes are signature-covered) and **read by nothing** today. It is deliberately an `int` and **not** an enum, because `JsonStringEnumConverter` throws on an unknown enum string — adding a member later would make older clients reject the *entire* index. Reserving it in the `6010cd1` schema break means the future priority-display feature ships as a **pure behaviour change with no wire break and no fleet coordination**.

### Publish and Delete (PUB-1)

**Publish S → T.** `PublishingService.PublishAssignmentAsync` copies the presentation, schedule and
images to T, then writes T's own copy of the assignment **already in its final `Published` state**
(`Status`, `PublishedBy`, `PublishedDate`, `PublishedPaths` all set) before updating S's record —
there is no window where T's copy exists but is not yet `Published`. S's record is updated next;
T's index is regenerated last. When S and T are the same team, this collapses to a single write.
(Before PUB-1, T's copy was written while still `Approved` and never updated afterward, so it never
reached T's index or T's devices.)

**Delete a Published assignment.** `AssignmentService.DeleteAssignmentAsync` moves S's record to
`S/deleted`; when S ≠ T, it also moves T's copy to `T/deleted` if present (absent is not an error);
T's index is regenerated, so T's devices drop the content at their next sync. The presentation,
schedule and image files stay in T untouched — other assignments there may still reference them.

**Delete a presentation.** `PresentationService.DeletePresentationAsync` soft-deletes S's presentation,
assignments and schedules, then (via `PresentationDeletePlanner`) moves every target team T's own
authoring-tier copies — `pres_{id}`, the related `sched_*` and `assign_*` — to `T/deleted` (a missing
file is a no-op; a failed move is logged and non-fatal). Blob cleanup for T's presentation, schedule
and images follows; the assignment copy is never uploaded to blob, so nothing is deleted there. T's
index is regenerated **after** the moves, so it no longer lists the entry. T's images are left alone.
Index Management repair removes copies orphaned by earlier deletes (`PresentationCopyRepairPlanner`:
a presentation whose `TeamFolderName` names another team whose source file is gone).

## Team / User

```csharp
public class Team : IEntity
{
    public string TeamID { get; set; }
    public string Name { get; set; }
    public string FolderName { get; set; }
    public string Description { get; set; }
    public bool IsActive { get; set; }
    // ...
}
public class TeamsCollection : IEntity { public List<Team> Teams { get; set; } }

public class User : IEntity
{
    public string UserID { get; set; }
    public string Username { get; set; }
    public string Email { get; set; }
    public string? UPN { get; set; }
    public bool IsSystemAdmin { get; set; }
    public List<TeamRole> TeamRoles { get; set; }
    // ...
}
public class TeamRole { public string TeamID; public List<string> Roles; }
public class UsersCollection : IEntity { public List<User> Users { get; set; } }
```

## TeamSigningKeys

Stored at `{teamFolderName}/team-signing.json` on the authoring tier only. Must never be written through `IBlobDistributionService` or synced to client machines.

```csharp
public sealed class TeamSigningKeys
{
    public string? PrivateKey { get; set; }         // Base64 PKCS#8 — authoring side only
    public string? PublicKey { get; set; }           // Base64 SubjectPublicKeyInfo
    public string? PublicKeyPrevious { get; set; }   // Base64 SPKI; optional; retained for the rotation window
}
```

Trust assumption: team content authors are trusted, and their processes are granted access to the team private key by design. `PublicKey` and `PublicKeyPrevious` are distributed to verifying components (NewsService, NewsViewer); `PrivateKey` stays on the authoring tier.

## TeamIndexFile (index.json per team)

The `index.json` written to each team folder by NewsCentral and consumed by NewsService and NewsViewer.

```csharp
public class TeamIndexFile : ISignable, IDeliveredKeyCarrier
{
    public string TeamFolderName { get; set; }
    public string TeamName { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string IndexHash { get; set; }               // SHA256 of content for change detection
    public List<PublishedAssignmentIndex> PublishedAssignments { get; set; }
    public IndexStatistics Statistics { get; set; }
    public string? Signature { get; set; }              // ECDSA P-256 per-team (Phases B1+C complete)

    // Phase 3a — key-with-content: the team's public key (Base64 SubjectPublicKeyInfo, the same
    // form as the registry PublicKey) travels with the index for dynamic (Entra-resolved) teams.
    // [JsonIgnore(WhenWritingNull)] + null-and-restore in EcdsaSignatureService.Canonicalize
    // EXCLUDE it from the canonical signing input exactly like Signature, so the signature does
    // not cover this field and a null value leaves the canonical bytes bit-identical to the
    // pre-key form (every existing static-team signature still verifies).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SigningPublicKey { get; set; }
}
```

**Index inclusion (PUB-2).** `PublishedAssignments` lists only assignments with `Status == Published`
**and** `TargetTeam` equal to the team folder this index is for (`IndexInclusion.Includes`, case-
insensitive). A team's own folder also holds its SOURCE records for assignments it published to
OTHER teams (`TargetTeam != this team`) — those are excluded; they describe content authored FOR
someone else, not content for this team's own devices. (Before PUB-2, a team's index also listed
everything it had published elsewhere.)

```csharp
public class PublishedAssignmentIndex
{
    public string AssignmentId { get; set; }
    public string PresentationId { get; set; }
    public string ScheduleId { get; set; }
    public string PresentationName { get; set; }
    public string PresentationDescription { get; set; }
    public int PresentationVersion { get; set; }
    public DateTime PresentationLastModified { get; set; }
    public DateTime ScheduleStart { get; set; }         // Client local time (no Z suffix)
    public DateTime ScheduleEnd { get; set; }           // Client local time (no Z suffix)
    public string DaysOfWeek { get; set; }
    public DateTime PublishedDate { get; set; }
    public string PublishedBy { get; set; }
    public DisplayTypeInfo DisplayTypes { get; set; }
    public ContentInfo Content { get; set; }
    public string SourceTeamFolderName { get; set; }
    public string SourceTeamName { get; set; }

    public string? PosterText { get; set; }             // From Presentation.PosterText
    public int DisplayDurationSeconds { get; set; }     // From Presentation.DisplayDurationSeconds
    public int Priority { get; set; } = 0;              // RESERVED — see Assignment.Priority
    public bool UseVirtualDesktop { get; set; }
    public string VirtualDesktopBackgroundColor { get; set; } = "#000000";
}

public class ContentInfo
{
    public string ImagePath { get; set; }               // Relative path from team root
    public string? ImageUrl { get; set; }               // Azure Blob URL
    public string ImageHash { get; set; }               // SHA256 for cache invalidation
    public long ImageSizeBytes { get; set; }
    public DateTime ImageLastModified { get; set; }     // From Presentation.LastModified
    public string MoreInfoUrl { get; set; }
}

public class DisplayTypeInfo   // sourced from the Presentation — the single source of truth
{
    public bool IsNewsOfWeek { get; set; }
    public bool IsWallpaper { get; set; }
    public bool IsLogonScreen { get; set; }
}

public class IndexStatistics
{
    public int TotalPublishedAssignments { get; set; }
    public int ActiveAssignments { get; set; }
    public int UpcomingAssignments { get; set; }
    public int ExpiredAssignments { get; set; }
}
```

## status.json (NewsService → NewsViewer)

Written by NewsService to `%programdata%\NewsCentral\` after each poll cycle.

```json
{
  "LastSyncTime": "2025-05-23T08:12:00Z",
  "IsOnline": true,
  "SyncSource": "Share"
}
```

`SyncSource` values: `Azure`, `Share`, `None`

## viewerstate.json (NewsViewer internal)

Location: `%LOCALAPPDATA%\NewsCentral\viewerstate.json` — **per-user state, NOT part of the
`%ProgramData%\NewsCentral\` machine cache.** Consequence for testing: **wiping the `%ProgramData%`
cache does NOT reset the daily gate** — delete `viewerstate.json` (or set `BypassDailyGate`) to force
a re-display.

```json
{
  "lastShownDate": "2025-05-23",
  "lastShownPresentationId": "abc-123",
  "lastContentKey": "abc-123|v1|sha256:2d286bb1..."
}
```

- **`LastShownDate`** holds a **logical-day key** (`"yyyy-MM-dd"`), **not** a calendar date and **not**
  a timestamp. The `LogicalDayStartHour` boundary shift is already baked into the key by
  `LogicalDayCalculator.LogicalDay`, so the gate (`AlreadyShownToday`) is a plain **string-equality**
  test. There is deliberately **no time component** — the file's `LastWriteTime` supplies that for
  debugging.
- **`LastShownPresentationId`** is **write-only** — `RecordShown` writes it; nothing reads it
  (`GetLastShownPresentationId` was removed in `04d6379`). This is **deliberate, not dead code**: it is
  retained for diagnostics and as the seed of the Priority-phase **shown-ID set** (a high-priority
  poster firing mid-day must be suppressed **per-presentation**, not per-day). **Do not remove it.**
- **`lastContentKey`** is the per-user **content marker** of the new-content notification: the content key
  (`presentationId|v<version>|<imageHash>`, `ContentKeys.For`) of the item this user last saw or was notified
  about. It is written when a full poster is displayed, silently on the first launch after upgrade
  (`MarkerInitialized`), and when a notification form is actually on screen — never on a decision alone. An
  empty/absent value means "not initialized yet". `RecordShown` preserves it (read-modify-write), and the file
  is written atomically (temp file + move). Deleting the file resets both the gate and the marker. See
  `docs/newsviewer-spec.md` ("Launch decision", "New-content notification").

## Lock-screen apply state (NewsService)

There is no lock-screen state file. The lock-screen apply is **registry-driven**: the live
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP\LockScreenImagePath` value is the
single source of truth. Each cycle NewsService compares the intended image against this value and
writes only on a difference. `servicestate.json` (which previously tracked the last-applied
wallpaper/lock screen) has been retired — the wallpaper split left it tracking nothing, and the
registry comparison self-heals failed applies without a separate state record.

## ResolvedTeams (resolved-teams.json)

Model: `NewsCentral.Models.ResolvedTeamsFile` (in `NewsCentral.Shared`). Written by NewsService to `{CacheRootPath}\resolved-teams.json` each poll cycle; read by NewsViewer, which **unions** these entries with the registry static team list.

Lists **only** dynamic teams — resolved from either the device's Entra `extensionAttributes` (attribute schemes, see `EntraTeamNameResolver`) or group membership (group teams, see `docs/entra-dynamic-teams.md`); never static (registry-configured) teams. It is a **local-tier file**, **unsigned**, and protected by cache ACLs. It carries no signature fields because dynamic-team `index.json` verification uses a public key **delivered with the team content** (key-with-content model — `TeamIndexFile.SigningPublicKey`, verified via `SignatureGate.VerifyWithPrecedence`; see `docs/security.md`).

```json
{
  "GeneratedUtc": "2026-06-12T08:12:00Z",
  "Teams": [
    {
      "TeamFolderName": "newscentral-prague-its",
      "LastConfirmedUtc": "2026-06-12T08:12:00Z",
      "State": "Active",
      "Source": "Group",
      "SourceId": "newscentral-prague-its"
    }
  ]
}
```

This example is a **group** team: `SourceId` and `TeamFolderName` are both `Canonicalize(InclusionGroup)`, so for a group entry the two fields are always identical by construction (see "Group teams" in `docs/entra-dynamic-teams.md`). An **attribute** entry's `SourceId` is instead the authored scheme name (e.g. `"fat"`), independent of `TeamFolderName`.

`State` values: `Active`, `Grace`. `Source` values: `Attribute` (resolved from device `extensionAttributes`) or `Group` (resolved from group membership — see `docs/entra-dynamic-teams.md`); it **defaults to `Attribute`** so pre-feature files with no `Source` field deserialize correctly. `SourceId` identifies **which configured instance** of `Source` produced the entry — empty string `""` is the legacy single-instance source, and defaults to `""` so pre-feature files (which carry no `sourceId`) deserialize unchanged. Together `(Source, SourceId)` — **not `Source` alone** — is the grace partition key in `EntraResolvedTeamsMerger`, so grace is per **instance**: several configured instances of the same source (e.g. multiple attribute schemes, multiple group-team instances) each carry an independent grace window. **NewsViewer ignores both `Source` and `SourceId`** — it reads only `TeamFolderName` and unions by name. Serializes with the existing camelCase + `JsonStringEnumConverter` options used across NewsService/NewsViewer; no new serializer options are introduced.

## Session Telemetry (NewsViewer → uploads folder)

Model: `NewsCentral.Models.SessionTelemetry` (in `NewsCentral.Shared`; implements `ISignable`). Shared so both NewsViewer (writer) and NewsService (verifier) can deserialize and verify without model duplication.

```json
{
  "sessionId": "guid",
  "presentationId": "abc-123",
  "teamId": "cz-its",
  "sessionStartTime": "2025-05-23T08:15:00Z",
  "sessionEndTime": "2025-05-23T08:15:34Z",
  "closeReason": "Timeout",
  "signature": "base64-hmac-sha256"
}
```

Property names are camelCase on disk (the general cache/telemetry option set below). `CloseReason` values: `Timeout`, `UserClose`, `UrlLaunch`

**Optional fields (omitted from the JSON when null, so an ordinary poster record's signed form is unchanged):**

| Field | Meaning |
|---|---|
| `RecordType` | `"notification"` for a "News arrived!" notification record; absent for a poster display session |
| `Source` | `"notification"` on a poster display opened by expanding a notification; absent otherwise |
| `ContentKey` | content key (`presentationId\|vN\|imageHash`) of a notification record |

A notification record reuses `SessionStartTime` (shown), `SessionEndTime` (outcome) and `CloseReason` (`Expanded`, `Dismissed` or `TimedOut`), so it is distinguishable **only** by `RecordType`. **Any consumer that counts or reports poster sessions must exclude `RecordType == "notification"`**, otherwise notifications are counted as poster displays and an Expanded notification is counted twice (its notification record plus the poster display record with `Source == "notification"`). NewsService's `TelemetryUploader` only verifies the HMAC and copies the file unchanged; the authoring app aggregates the records (see "Poster statistics" below). See `docs/newsviewer-spec.md` ("Notification records").

**Example — notification record** (`recordType` set; `sessionStartTime` = when the form was shown, `sessionEndTime` = when the outcome was decided, `closeReason` = the outcome). As written by NewsViewer (camelCase property names):

```json
{
  "sessionId": "3f0c2a52-6a0e-4a5e-9d2f-5a7a1f1c9b11",
  "presentationId": "4dd97030-61df-42d2-a89d-f57a7e171b2d",
  "teamId": "cz-its",
  "sessionStartTime": "2026-10-12T08:15:00Z",
  "sessionEndTime": "2026-10-12T08:15:12Z",
  "closeReason": "Expanded",
  "recordType": "notification",
  "contentKey": "4dd97030-61df-42d2-a89d-f57a7e171b2d|v1|sha256:2d286bb1fa5bba7b87586a5d10e302336ae289cee236cad924fa0d93be4367d3",
  "signature": "base64-hmac-sha256"
}
```

`closeReason` is `Expanded`, `Dismissed` or `TimedOut`; time to outcome is `sessionEndTime - sessionStartTime` (hover and lock pauses included).

**Example — poster record opened from a notification** (an Expanded notification also produces this normal poster display record, so it is counted once as a poster display and once as a notification record — filter on `recordType`):

```json
{
  "sessionId": "b7d4f9c0-2d1e-4a07-8f0b-0c6e2f4d7a22",
  "presentationId": "4dd97030-61df-42d2-a89d-f57a7e171b2d",
  "teamId": "cz-its",
  "sessionStartTime": "2026-10-12T08:15:12Z",
  "sessionEndTime": "2026-10-12T08:16:40Z",
  "closeReason": "UserClose",
  "source": "notification",
  "signature": "base64-hmac-sha256"
}
```

`Signature` is set by `TelemetryWriter` in NewsViewer immediately before writing the file. `TelemetryUploader` in NewsService verifies it before forwarding to the repository; files with `Invalid` signatures are discarded and logged.

## Poster statistics (authoring app, `{DataPath}\statistics\`)

Aggregated by the authoring app from the telemetry inbox (see "Session Telemetry" above). Nothing is written into `uploads\`; NewsService and NewsViewer are unaffected. Models and engine: `NewsCentral.Shared/Statistics/`.

```
{dist}\uploads\session-*.json                          inbox: read + moved, never written
{dist}\telemetry\{teamId}\{presentationId}\            counted raw files (moved here straight from uploads\)
{dist}\telemetry\_invalid\                             unparseable / rejected records
{dist}\telemetry\owner.json                            { dataPath, claimedUtc }
{dist}\telemetry\.lock                                 run lock { startedUtc }
{DataPath}\statistics\{teamId}\{presentationId}.json   aggregate (PresentationStats)
{DataPath}\statistics\_run.json                        last run summary (StatsRunSummary)
```

`{dist}` is the environment's effective local distribution path (`Distribution.LocalPath`, else `{DataPath}\_distribution`). Raw files stay on the share so every move is a same-volume rename; the aggregates live in the authoring store. All of these are camelCase, indented, unsigned, and outside the signing contract.

**Keys.** `teamId` in a session record is the **source (authoring) team** of the presentation — NewsViewer writes `assignment.SourceTeamFolderName`, which index generation copies from `assignment.SourceTeam` — **not** the target team whose index the device consumed. `presentationId` is the presentation's single GUID (publishing copies keep the same id). Consequently one presentation published to several target teams aggregates into one `(source team, presentation)` bucket; per-target-team figures are not derivable from the records.

**`PresentationStats`**: `schemaVersion` (1), `teamId`, `presentationId`, `days` (`yyyy-MM-dd` UTC of `SessionStartTime` → `{ views, clicks }`), `pendingMove` (file names whose counts are saved but whose move to the counted folder is not yet confirmed — the crash-recovery guard), `updatedUtc`.

**Batch protocol** (each batch = up to `StatisticsDefaults.BatchSize` = 500 input files, each read once and moved once):
1. **Read + classify**, `ReadParallelism` (8) at a time: *Invalid* (unparseable, bad team/presentation id, missing `SessionStartTime`), *Duplicate* (file name already in its counted folder, or seen twice in the batch), *Valid* (team, presentation, UTC day, isView, isClick — a notification is Valid but not a view), or *Skipped* (locked/vanished; left in place for the next run).
2. **Save stats**, serially, one atomic write per stats file: the deltas plus `pendingMove` = the group's file names. This save is the commit point.
3. **Move**: Valid files to the counted folder, Invalid files to `_invalid\`, Duplicates deleted. Sequential (`MoveParallelism` = 1; parallel renames into one folder measured ~7× slower on local NTFS). A failed move is logged and the file left in place.
4. **Clear** `pendingMove` (serially), keeping only names whose move failed.

**Recover phase.** Before any new input is read, every stats file with a non-empty `pendingMove` has its listed files moved (from `uploads\`, or a legacy `new\`) to the counted folder — never re-counted; names already counted or gone are skipped. A move that still fails ends the run Failed *before* reading new input, because that file could otherwise be counted twice. Cancellation is checked between batches and during the read step (only already-read files are kept); once step 2 has begun for a batch, steps 3–4 always complete.

**Legacy `new\`.** M1 staged files in `{team}\{presentation}\new\`. Those are drained as uncounted input after `uploads\` and the emptied `new\` folders are removed.

**Definitions.**
- **Views** = poster display sessions (every non-notification record).
- **Clicks** = poster sessions with `CloseReason == "UrlLaunch"` (the Intranet button); each click is also a view.
- Records with `RecordType == "notification"` are moved but excluded from both counts.

**`StatsRunSummary`** (`_run.json`): `startedUtc`, `finishedUtc`, `outcome` (`Completed`, `Stopped`, `Failed`, `NotAvailable`), `processed` (input files handled; formerly `sorted`, which is still accepted when reading an old file), `counted` (files that produced a view), `invalid`, `duplicatesDropped`, `pruned`, `recoverMs`, `processMs`, `pruneMs`, `message`. Written on Completed / Stopped / Failed; a run refused by the lock or owner guard does not overwrite it.

**Retention.** Counted raw files and `_invalid\` are deleted after `StatisticsDefaults.RawRetentionMonths` (13) by LastWriteTime — a constant, not a setting. Aggregates are kept.

## Cache Folder Structure

```
%programdata%\NewsCentral\              ← machine-level; shared across all users
├── cz-its\
│   ├── index.json                     TeamIndexFile
│   └── images\generated\              downloaded presentation images
├── de-prod\
│   └── ...
├── uploads\
│   └── session-{guid}.json            written by NewsViewer, uploaded by NewsService
└── status.json                        sync state — written by NewsService

%localappdata%\NewsCentral\             ← per-user; one copy per Windows user account
├── viewerstate.json                   daily gate + last-notified content marker — written by NewsViewer
└── launch.log                         one line per NewsViewer launch/outcome (decision, reason, content key) — written by NewsViewer, 64 KB cap
```

`viewerstate.json` is intentionally per-user so that in multi-session environments (Citrix RDSH, Windows Server RDS) each user's shown-today state is independent. All other cache files remain machine-level.

Repository / network share mirrors the same team folder structure as the `%programdata%` cache.

## JSON serialization option sets

Two option sets; use the right one for the context.

*NewsService / NewsViewer* - general cache and telemetry I/O:
```csharp
new JsonSerializerOptions {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
}
```

*NewsCentral* - index file generation and signing (`JsonConfiguration.GetIndexJsonOptions()`):
```csharp
new JsonSerializerOptions {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(), new SmartDateTimeConverter() }
}
```
`SmartDateTimeConverter` writes UTC `DateTime` with a `Z` suffix and `Unspecified` (schedule times) without one, and truncates to whole seconds. **This truncation is what keeps the ECDSA signature byte-consistent with the persisted file** - without it, sub-second precision present in memory is stripped on write, causing every verifier to return `Invalid`. Verifiers (NewsService, NewsViewer) deserialize with standard ISO parsing, which faithfully round-trips whole-second timestamps.
