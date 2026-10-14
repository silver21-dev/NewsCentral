# NewsViewer — Component Specification

**Type:** WinForms (.NET 10) desktop application  
**Status:** Phase 2 complete. All Phase 2 features implemented and tested; the side panel was reworked from a hover-reveal to a fixed Fluent gray panel in the v2.7 UI pass, and to the themed 220px branded panel (adaptive image-fit window, Dark/Light themes, resx strings) in the UI v3 pass. Wallpaper-**image** ownership migrated to NewsService (PersonalizationCSP, machine-wide); NewsViewer retains only a per-user HKCU wallpaper **style** re-assert (no `SystemParametersInfo`; see Wallpaper Application). The new-content notification ("News arrived!" mini form), the ordered launch decision with its per-user marker and launch log, remote-session support and the fleet-wide poster frame colours were added after Phase 2. NativeAOT migration path preserved; new Win32 P/Invoke code uses `LibraryImport`, while the legacy `NativeMethods.cs` and `WallpaperService.cs` still use `DllImport` (blittable, AOT-compatible) until they are migrated (`AllowUnsafeBlocks` is enabled only for the generated stubs — no hand-written unsafe code, no COM).

NewsViewer is a **one-shot process**: launch → run the launch decision (`LaunchDecider`: environment checks, the daily gate, the per-user content marker) → show the full poster, the small "News arrived!" notification, or nothing → re-assert the wallpaper style (not in remote sessions) → exit. There is no resident process, no `FileSystemWatcher`, and no held-open message pump. Each display decision is made afresh at launch.

## Launch Conditions

- Launched by two Task Scheduler tasks, registered by the installer and verified (never created or repaired) by NewsService (`ViewerTasks`): one on logon (15 s delay), unlock, RDP connect and console connect, one on the NewsService content-changed event. There is no HKLM Run entry (see Launch surface below, `deploy/scheduled-tasks/README.md`, `docs/newsservice-spec.md`, `scripts/Register-NewsViewerTasks.ps1` for dev/pilot boxes)
- Optionally launchable from the Windows Start menu (shortcut target `NewsViewer.exe --manual`; see Manual launch below)
- `appsettings.json` must be declared in `NewsViewer.csproj` as `<Content Include="appsettings.json">` with `CopyToOutputDirectory = PreserveNewest` so it is deployed alongside the executable
- **Configuration layers** (registry always wins): `appsettings.json` (shipped, neutral, **committed** — a tracked artifact, never gitignored — loaded `optional: false`, so a missing file is a hard startup failure) → `appsettings.Development.json` (optional, gitignored dev overlay, loaded `optional: true`, **never published** via `CopyToPublishDirectory=Never`; documented shape in `appsettings.Development.json.example`) → registry (GPO). `appsettings.json` is the base layer the MSI installs and GPO sits on top of. See `docs/configuration.md` → appsettings.json — NewsViewer.
- `Main()` evaluates the `Active` master switch (registry `Active`, DWORD 0/1 at the hive root; default `true`) first, ahead of every other startup check, and returns immediately when `false` — no poster, no wallpaper style re-assert, no `viewerstate.json` write, no telemetry, no dialog. The last-applied wallpaper style is left as-is (the wallpaper image is NewsService's concern, unaffected by NewsViewer's `Active` switch either way).
- `Main()` validates `CacheRootPath` after config load; if it is empty a `MessageBox` is shown and the process exits. (`Company` is **not** validated here — it is the build-time constant `SolutionConstants.Company`, enforced at build time, not read from config.)
- Registry `teams\` value names must match the generated folder name exactly — the sanitized team name with no `team-` prefix (e.g. `cz-its`, not `CZ_ITS`)
- **An empty static `teams\` list does not stop NewsViewer.** `Main()` reads the static list via `TeamConfigurationReader.GetTeams` but does not gate on its length — the effective team set used for presentation selection is static ∪ dynamic (`resolved-teams.json`, resolved inside `PresentationSelector`; see Presentation Selection below), so a machine configured purely with Entra-resolved teams still needs to reach that union. An early-return guard here once defeated that on a dynamic-only machine; mirrors the equivalent fix in `NewsService/Worker.cs` (`docs/newsservice-spec.md`).

### Launch surface — logon, unlock, connect, content

The triggers are **complementary, not redundant**, and all of them are scheduled tasks registered by the installer and verified by NewsService:

- **Scheduled task "NewsViewer Launch"** fires at **logon** (any user, `LogonTrigger` with a 15 s delay so the shell is up), on workstation **unlock**, RDP connect and console connect, no arguments. The logon trigger replaces the former HKLM `Run` value (removed by NewsService once both tasks are in place); a logon that is also reported as a console connect just starts a second instance that exits as `AlreadyRunning` — the mutex absorbs overlapping triggers.
- **Scheduled task "NewsViewer Content Arrived"** fires on the NewsService `ContentChanged` event (below) with the argument **`--trigger content`**. Unknown `--trigger` values are ignored (treated as absent).

Both tasks run as `BUILTIN\Users` in each interactive session (parallel instances), so the single-instance mutex is session-local (`Local\NewsCentral.NewsViewer`). On always-on machines that are locked rather than logged off, unlock/connect is how every logical day after the first is reached. There is deliberately **no repeating/timer trigger** — re-evaluation is event-driven, and the launch decision below decides whether each launch actually shows anything.

**Why the idle check applies only to content launches:** an event-triggered launch can fire while the user is away from an unlocked workstation, where a window would only be seen later; an unlock/logon/connect launch means the user has just arrived — and a Windows Hello unlock may not register as keyboard/mouse input, so an idle test there would wrongly suppress the morning poster.

### Launch triggers — content-changed event

NewsService writes a dedicated EventLog entry whenever it has accepted a changed team index and the content is fully in the local cache (`ServiceEventIds.ContentChanged` in `NewsCentral.Shared`; written directly via `EventLog`, independent of `ILogger` levels, always on):

| | |
|---|---|
| **Log** | `Application` (`Logging:EventLog:LogName`) |
| **Source** | `NewsService` (`Logging:EventLog:SourceName`) |
| **EventID** | `2001` (`ContentChanged`), Information |

NewsService does not filter by content type — NewsViewer decides relevance. The event is emitted **once per sync cycle**, after all teams have finished, when at least one team produced an accepted and complete change (the index is written last, only once every referenced image is cached). It is never emitted for an unchanged, rejected or incomplete sync; an incomplete sync leaves the previous index in effect and is retried next cycle.

### Launch decision

`LaunchDecider.Decide` (pure, in `NewsCentral.Shared`; the Win32 probes are injected and evaluated lazily) runs these rules **in order**; the first match wins:

| # | Condition | Result |
|---|---|---|
| 1 | `Active` = false | exit silently (not logged) |
| 2 | single-instance mutex not acquired | `Exit(AlreadyRunning)` |
| 3 | `--manual` | `FullPoster` — bypasses the gate, the remote-session switch and every check below (still needs a usable item, else `Exit(NoContent)`, and a qualifying monitor, else `Exit(NoQualifyingMonitor)`) |
| 4 | remote session **and** `Display:ShowInRemoteSessions` = false | `Exit(RemoteSession)` |
| 5 | no selected poster item (or unusable image) | `Exit(NoContent)` |
| 5b | no monitor of at least 1920×1080 (typical for a small RDP/Citrix/Horizon client window) | `Exit(NoQualifyingMonitor)` |
| 6 | session not visible: not `WTSActive`, not unlocked, or the input desktop is not `Default` (any failure counts as not visible) | `Exit(NotVisible)` |
| 7 | no shell: no `Shell_TrayWnd` window in this session (a published-app / seamless session; a failed probe counts as "has a shell") | `Exit(NoShell)` |
| 8 | `--trigger content` and idle > 2 minutes (`LaunchDecider.IdleThreshold`; `GetLastInputInfo`, wrap-safe) | `Exit(Idle)` |
| 9 | busy: `SHQueryUserNotificationState` is NOT_PRESENT, BUSY, RUNNING_D3D_FULL_SCREEN, PRESENTATION_MODE or APP (`QUIET_TIME` and `ACCEPTS_NOTIFICATIONS` are not busy; a failed call is not busy), **or** the full-screen-foreground heuristic below | `Exit(Busy)` |
| 10 | daily gate not hit, or `BypassDailyGate` | `FullPoster` (gate consumed as before) |
| 11 | `Ui:NotifyNewContent` = false | `Exit(NotifyDisabled)` |
| 12 | marker empty (first run after upgrade) | marker := current key, `Exit(MarkerInitialized)` — no notification for a poster the user already saw |
| 13 | marker = current key | `Exit(AlreadySeen)` |
| 14 | otherwise | `MiniForm(key, title)` — the "News arrived!" notification (see below) |

Rules 6–9 also guard the full poster: it is never shown behind a locked screen, on the secure desktop, in a session without a shell, to an away user on content arrival, or over a presentation. The gate is not consumed in those cases, so a later trigger retries. Every exit except those in a remote session performs the wallpaper style re-assert (see "Remote sessions"). Every exit is logged; rule 1 (`Active`) is the only silent one.

The `NoQualifyingMonitor` threshold (`HasQualifyingMonitor`, at least 1920×1080 on any monitor) is unchanged; it only has its own log reason now instead of being folded into `NoContent`. A remote client window smaller than that never gets a poster or notification.

**Marker.** `ContentKey` = presentation id + version + image hash (`ContentKeys.For`), so it changes when an item is republished or its image is replaced. The per-user marker ("last content this user saw or was notified about") is stored as `lastContentKey` in `%LOCALAPPDATA%\NewsCentral\viewerstate.json` next to the gate state (atomic temp-file write; the gate update preserves it). It is written when the full poster is displayed (including `--manual`), silently for `MarkerInitialized`, and — for the notification (rule 14) — in the form's `Shown` handler, i.e. only once the form is actually on screen; the decision alone never writes it.

**Busy rule — full-screen foreground heuristic.** `SHQueryUserNotificationState` does not report browser full screen (F11, full-screen video) as busy, so `IsBusy()` also checks the foreground window (`FullScreenHeuristic`, pure, in `NewsCentral.Shared`; the P/Invoke gathering is in `LaunchProbes`): busy when the window rectangle covers the **whole monitor** (`rcMonitor` of `MonitorFromWindow(MONITOR_DEFAULTTONEAREST)`, not the work area) **and** `IsZoomed` is false. Not busy: no foreground window, a window of this process, the desktop/shell classes `Progman`, `WorkerW` and `Shell_TrayWnd`, a **maximized** window (it can cover the monitor when the taskbar auto-hides), a window that only fills the work area, and any failure of the calls. Busy = the notification-state rule OR this rule; it applies to every non-manual launch (full poster and notification alike).

**Launch log.** `%LOCALAPPDATA%\NewsCentral\launch.log` — one line per launch: ISO timestamp, `trigger=content|none`, `decision=`, `reason=`, `key=`, and `title=` (MiniForm only). `key=` is the content key exactly as stored in `viewerstate.json` (its own `|` separators intact). When a notification form closes, a second line is appended: `decision=MiniForm | reason=Outcome | key=… | outcome=Expanded|Dismissed|TimedOut` — `Expanded` becomes `Expanded (activation refused)` when Windows refused the foreground switch, and `Closed` means the form was closed by something other than the user or the timer. A failure before the form is shown logs `decision=MiniForm | reason=Error(<exception type>)` and writes no marker. A launch whose poster wanted a Virtual Desktop but runs in a remote session carries `| note=VirtualDesktopFallback`. Capped at 64 KB (the newest half is kept); I/O never throws; no user names or paths.

**Debug builds only:** `--reset-launch-state` clears this user's gate state, marker and launch log, then exits. `--test-notify` shows the notification for the currently selected item, bypassing the decision; it writes no marker and no telemetry (an Expanded poster included), logs `decision=MiniForm reason=Test` plus the outcome line, and combines with `--culture`. `--simulate-remote` makes the session count as remote (see "Remote sessions").

### Command-line switches

Arguments are case-insensitive; unknown arguments are ignored. The Debug-only switches are compiled out of Release builds, where they are simply unknown arguments and do nothing.

| Switch | Release | Debug | Effect |
|---|---|---|---|
| `--manual` | yes | yes | On-demand launch (Start menu shortcut): bypasses the daily gate and the launch checks, does not stamp the gate. See "Manual launch". |
| `--trigger content` | yes | yes | Set by the "Content Arrived" task: enables the idle check. Other `--trigger` values are ignored. |
| `--culture <name>` | — | yes | Forces the UI culture (localization testing). |
| `--reset-launch-state` | — | yes | Clears the gate state, content marker and launch log, then exits. |
| `--test-notify` | — | yes | Shows the notification for the selected item, bypassing the decision; no marker, no telemetry. |
| `--simulate-remote` | — | yes | The session counts as remote (tests the remote switch and the Virtual Desktop fallback). |

`--culture`, `--test-notify` and `--simulate-remote` combine with each other and with `--manual`.

### New-content notification

When the decision is `MiniForm`, NewsViewer shows a small "News arrived!" form instead of the full poster (`NotificationForm`, countdown/outcome rules in the pure `NotificationCountdown`).

- **Appearance.** About 320×72 DIPs (scaled for the system DPI), borderless, accent stripe on the leading edge, the poster palette (`Theme.Poster`: the theme, with the fleet-wide frame colours applied when configured — see "Poster frame colours") and the standard fonts. Line 1: the localized heading `NotifyHeading` ("News arrived!", bold). Line 2: the poster title with an ellipsis (`NotifyTitleFallback`, "Today's news", when empty). A close × at the top-right; the cursor is a hand over the whole form.
- **Placement.** Primary monitor's working area, 5 DIPs from the bottom-right corner (`NotificationPlacement`). Under a right-to-left UI culture (Arabic) the form is mirrored: bottom-left, stripe and × on the mirrored sides.
- **No focus.** `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, `ShowWithoutActivation`, `ShowInTaskbar = false`: the form never takes focus or keystrokes and has no taskbar button.
- **Timing.** 60 s (`NotificationCountdown.NotifyDurationSeconds`, a code constant). The countdown is paused while the mouse is over the form (polled against the form bounds on each one-second tick) and while the session is locked (`WTSRegisterSessionNotification` + `WM_WTSSESSION_CHANGE` lock/unlock, unregistered on close). It disappears silently on timeout.
- **Outcomes.** Click anywhere except × → **Expanded**: the standard poster form for the **same item the decision chose** (no re-selection) opens with the countdown already stopped (the path the delivered-image buttons use; the daily gate is not stamped again). × → **Dismissed**. Countdown reaches 0 → **TimedOut**.
- **Expanded brings the poster to the front.** The click is user input to this process, so Windows' foreground rules allow activation: the poster form is created, shown and **activated** (`Form.Activate()` + `SetForegroundWindow`) from inside the click handler *before* the notification closes — closing first could let Windows hand activation to another window. Both forms share one message loop, which ends only when the last of them closes (ending the notification's own loop would dispose the poster). If `SetForegroundWindow` is refused the form is still brought to the front of the z-order and the outcome line reads `outcome=Expanded (activation refused)`. No `AttachThreadInput`, synthetic keystrokes or other foreground-lock workarounds are used. A poster that wants a Virtual Desktop (console session) cannot be built on this thread; it opens after the notification closes, on its own desktop. The gate and `--manual` paths add no explicit activation.
- **Accessibility.** The form's `AccessibleName` is `"<heading>: <title>"` (the localized "News arrived!" heading plus the poster title) and its `AccessibleRole` is `Alert`, so screen readers announce it without it taking focus.
- **Marker.** `lastContentKey` is written in the form's `Shown` handler, i.e. only once the form is actually on screen. A form that fails to show leaves the marker untouched, so the notification is retried by the next trigger.
- **Process lifetime.** The process, and with it the single-instance mutex, stays alive while the notification (and, after Expanded, the poster) is open; a launch in that window logs `AlreadyRunning`. The wallpaper style re-assert runs after the form closes.
- **Tracking.** See "Session Telemetry" below.

**Behavior note — the marker holds only the last item.** The marker is the single most recently notified/seen content key, not a history. If an older item becomes the selected poster again (newly assigned or republished) it is a different key from the marker and notifies again.

### Known limitations

The notification is shown on the **primary monitor only**.

If new content arrives while the user is away from an unlocked workstation, the arrival notification is skipped. It is picked up at the next workstation unlock, session connect, or new content arrival, whichever comes first. A user who never locks their workstation may therefore not be notified of an item until the next one arrives; the notification then shows the newest item.

**Rollout note (poster ranking change).** On the first launch after upgrading to the newest-to-team ranking, a client may select a different presentation than before. If the daily gate was already hit that day, the user gets one "News arrived!" notification for it. This is expected and happens once.

## The daily gate

The gate is **date-only** and evaluated once per launch. `ViewerStateService.AlreadyShownToday()` takes **no presentation id** — it disturbs the user at most once per **logical day**, regardless of which presentation is active.

A **logical day** runs from `Display:LogicalDayStartHour` (default `0` = calendar day) to the same hour the next day, in **local** time. `AlreadyShownToday()` compares the stored `LastShownDate` against the current logical day; `RecordShown` stamps the logical day after a display.

**State file.** The gate state lives in `%LOCALAPPDATA%\NewsCentral\viewerstate.json` — **per-user, and NOT part of the `%ProgramData%\NewsCentral\` machine cache.** Consequence for testing: **wiping the `%ProgramData%` cache does not reset the daily gate** — delete `viewerstate.json` (or set `BypassDailyGate`) to force a re-display. `LastShownDate` stores the logical-day key (the `LogicalDayStartHour` boundary is already baked in), so the gate is a plain string-equality check with no time component. See `docs/data-model.md` → viewerstate.json.

**Rationale.** A night-shift worker who starts at 22:00 and unlocks again at 01:00 is still inside the *same* logical day and must not be shown the poster twice. Worked example — `LogicalDayStartHour = 5` (05:00 boundary) cleanly covers morning, afternoon, and night shifts: everything from 05:00 through 04:59 the next morning counts as one day, so a night-shift unlock after midnight does not re-trigger.

### Manual launch - `--manual`

`NewsViewer.exe --manual` (case-insensitive; unknown arguments are ignored) is the on-demand launch used by the Start menu shortcut. `Active` is still evaluated first, so `Active = false` exits silently even with `--manual`. A manual launch skips the daily-gate check and does **not** write the gate marker, so it neither depends on nor disturbs the scheduled once-per-day display. Everything else - the single-instance mutex, poster selection, the no-poster behavior, session telemetry and the wallpaper style re-assert - follows the scheduled path unchanged. The one exception: `--manual` also bypasses the remote-session switch and the shell and visibility checks (decision rule 3), while the Virtual Desktop fallback and the no-op wallpaper style re-assert still apply in a remote session. A manual launch is noted with a `[Manual]` debug line.

### Deliberate gap — no same-day delivery (design decision)

Content published **after** the day's poster has already shown **waits for the next logical day**. There is **no same-day delivery path** in this release, and this is **intended** ("do not disturb users much") — it is a design decision, not an omission or a bug to be fixed. The reserved `Priority` field (see `docs/data-model.md`) is the future, author-controlled escape hatch for urgent same-day content.

## Presentation Selection

- Reads `index.json` from every team in the **effective** team set — the static `teams` registry configuration unioned with the dynamic (Entra-resolved) set read from `resolved-teams.json` (`EffectiveTeams.Union`, de-duplicated ordinal-ignore-case) — not the static list alone
- **ECDSA verification** — calls `EcdsaSignatureService.Verify(index, keys)` where `keys = SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolderName)`: `Invalid` → skips the team entirely (no presentations shown from that team); `Unsigned` / `Disabled` / `Valid` → all logged distinctly, team accepted. Index is deserialized with standard ISO timestamp parsing (no `DateTime` converter) so values match the signed, persisted form.
- Selects the active **News-of-the-Week** presentation that is *newest to the team*: the later (UTC) of `PresentationLastModified` and the assignment's `PublishedDate`. A missing `PublishedDate` (older indexes) falls back to `PresentationLastModified`; ties break on `PresentationLastModified` descending, then `AssignmentId` ordinal ascending. A newly published assignment of an older presentation therefore surfaces as the poster and the arrival notification, and so does a presentation that was just edited. Only `DisplayTypes.IsNewsOfWeek` assignments are eligible for the poster — a wallpaper-only / lock-screen-only assignment is never shown as a full-screen poster. (Wallpaper and lock-screen selection now happen in NewsService, from the same signed indexes, via the same shared helper — see below.)
- Active-window/day filtering and the ranking pick are the shared pure helper `NewsCentral.Models.IndexFile.ActiveAssignmentSelector` (`IsActive`, `PickNewestToTeam(assignments, now, predicate)` for the poster, and `PickNewestActive` — newest by `PresentationLastModified` only — for the lock screen and wallpaper), unit-tested in `NewsCentral.Shared.Tests`. `SelectActive` uses predicate `a => a.DisplayTypes.IsNewsOfWeek`. The same helper class is used by `SyncService` in NewsService for the lock-screen (`IsLogonScreen`) and wallpaper (`IsWallpaper`) winners — see `docs/newsservice-spec.md` — reading from the same signature-verified index enumeration; verification is never bypassed on either side.
- **Image integrity verification** — after the winning assignment is selected, computes SHA-256 of the cached image file and compares it against `Content.ImageHash` from the signed index. Missing hash → warning logged, continues. Mismatch → error logged, returns `(best, null)` so the caller declines to display/apply tampered content. Disabled by `BypassImageIntegrityCheck = true`.
- If no valid presentation found: **the poster is skipped** (but the wallpaper style re-assert still runs — see Wallpaper Application)
- If no qualifying monitor (Full HD or better): **do not show the poster window** (the wallpaper style re-assert is not gated by this)

## Window Layout

Borderless, top-most, centered frame: **adaptive image area + 220px side panel + 44px caption
bar**, wrapped in a 5px solid theme-colored frame. The image area takes the **content's exact
aspect ratio**, fitted within **1600×900** (upscaling smaller images to fit is intended),
clamped at **960×540** for extreme ratios — the PictureBox stays `SizeMode.Zoom`, so clamped
cases letterbox gracefully. A missing or undecodable image falls back to a 1600×900 area.
Client size = `(imageW + 220 + 2×5) × (imageH + 44 + 2×5)`: 16:9 content yields the classic
1600×900 area (window **1830×954**); 4:3 content (e.g. 1200×900) gets a 1200×900 area with
**no letterbox bars**.

┌─ 5px frame ──────────────────────────────────┬────────────┐
│                                              │ NewsCentral│
│            Image — adaptive size             │ (● Online) │
│            (Zoom, themed stage mat)          │ [ Close  ] │
│                                              │ [ More.. ] │
│                                              │ [  card  ] │
├──────────────────────────────────────────────┤            │
│  Caption bar — PosterText / PresentationName │            │
└──────────────────────────────────────────────┴────────────┘

### Side panel (220px)

A **fixed, always-visible column** (the earlier hover-reveal / slide-in trigger and the 8px
trigger strip have been removed); 16px outer margins, 12px between items. Top to bottom: the
**NewsCentral wordmark** (centered in the side panel; 13pt SemiBold-weight — Segoe UI Variable Display, Segoe UI fallback;
GDI+ has no SemiBold `FontStyle`, so Bold is the nearest weight), the **status pill** (centered; rounded
surface chip showing `● Online` / `● Offline` in the theme's OnlineFg/OfflineFg), **Close** —
the accent-filled primary action (`RoundedButton.IsPrimary`), **More information** — a
ghost/surface secondary button (opens `Content.MoreInfoUrl`), and the auto-close card.

### Auto-close card

A centered row of checkbox + caption ("Form closes in") + countdown number + "seconds" label + a
progress bar (5px), laid out in a `TableLayoutPanel` with auto-sizing rows (rows cannot
overlap regardless of font/DPI). The checkbox is checked by default; unchecking
**stops** the countdown and grays the number, unit, and bar; re-checking resumes
from the current value. Counts down from the **resolved** `DisplayDurationSeconds`
(see Display Duration below), closes at zero with reason `Timeout`.

### Delivered image buttons - Ui\ShowDeliveredImages

`HKLM\Software\{Company}\NewsCentral\NewsViewer\Ui\ShowDeliveredImages`, **REG_DWORD** 0/1 (`Ui:ShowDeliveredImages`
on `ViewerConfiguration`), default **`false`**. When false, no buttons are created and the form is unchanged. When true,
the side panel gains two buttons below the auto-close card - **Wallpaper** and **Lock screen** (`UiStrings` keys
`ShowWallpaperButton` / `ShowLockScreenButton`, EN/ES/FR/DE) - that switch the image shown in the same form between the News
poster and the wallpaper / lock screen image NewsService currently delivers through PersonalizationCSP. The buttons exist
on the form whether it was opened by a scheduled launch or `--manual`; form creation and launch logic are otherwise unchanged.

- **Availability** is decided by `NewsCentral.Configuration.DeliveredImageResolver` (in `NewsCentral.Shared`, unit tested). A
  surface is available only if NewsService's own flag for it is on (`Delivery\WallpaperEnabled` / `Delivery\LockScreenEnabled`;
  when the value is absent the NewsService default from `ConfigManifests` applies, not a copy of the literal), AND
  PersonalizationCSP (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP`, 64-bit view) holds a non-empty
  `DesktopImagePath` / `LockScreenImagePath`, AND that file exists. It never throws; any failure is a warning and the surface
  counts as unavailable. Each button's `Enabled` state comes from the resolver when the form opens, and the surface is
  re-resolved on every click - if it has become unavailable the button is disabled and the current image stays, with no dialog.
- **Behavior.** A click replaces the displayed image (scaled to fit, aspect ratio preserved); clicking the active button
  again restores the poster; switching directly between wallpaper and lock screen works. The active button is drawn in the
  accent style.
- **No file locks.** Images are read with `FileShare.ReadWrite | FileShare.Delete` into memory and the bitmap is built from
  that copy (never `Image.FromFile`), and the previous delivered bitmap is disposed on every switch - so NewsService can
  republish or delete the image while it is on screen.
- **Countdown.** The first click on either button stops the auto-close countdown for the rest of the form's lifetime: the
  timer is stopped and the auto-close card is hidden. Closing is then manual (Close).
- **No telemetry.** Viewing the wallpaper or lock screen emits no telemetry and no tracking; the poster session record is
  unchanged.
- **Registry-access exception.** Every NewsViewer registry access here is read-only. Reading
  `HKLM\Software\{Company}\NewsCentral\NewsService\Delivery` is a **documented exception to the per-component-subkey
  invariant** (each component otherwise reads only its own subkey); PersonalizationCSP is a Windows key, not a component subkey.

### Theme selection — Ui\Theme

Two `Theme` palettes are defined in `FluentControls.cs`: **Dark (the default)** and **Light**
(the classic Fluent gray look). Selection is per machine via registry —
`HKLM\Software\{Company}\NewsCentral\NewsViewer\Ui\Theme`, REG_SZ `"Dark"` | `"Light"`
(`"light"` case-insensitive → Light; anything else, including an absent value → Dark). It is
resolved **once** in `Program.cs` into `Theme.Current` before any Form is constructed — the
same resolve-once-then-pass pattern as display duration. Registry-only; there is **no**
appsettings key.

### Poster frame colours — Ui\FrameColor, Ui\FrameTextColor

Admins can brand the poster window fleet-wide (not per presentation) with two registry values under
`HKLM\Software\{Company}\NewsCentral\NewsViewer\Ui`, both REG_SZ in the form `#RRGGBB` (hex digits in either
case), default empty:

| Value | Meaning |
|---|---|
| `FrameColor` | Colour of the poster window's **side panel, title bar (the caption strip under the image) and outer border**. Empty = the theme. |
| `FrameTextColor` | Text colour on those surfaces. Empty = black or white by contrast when `FrameColor` is set, otherwise the theme. Ignored while no `FrameColor` is set. |

**Affected:** the side panel and everything painted on it that is text or a card (wordmark, the "Click to see
more information" button, the countdown card with its label, number and progress bar, the Wallpaper and Lock
screen buttons), the title bar and its poster text, the thick outer border and its thin edge lines, the
Online/Offline status pill (its background follows the card shade; see below) and the **"News arrived!"
notification form** (background = the frame colour, heading and title = the primary text colour, the close ×
and its hover shade and the border from the derived shades; the accent stripe, placement, size and behaviour
are unchanged).
**Not affected:** the Close (accent) button, the image area, the green/red status dot, the accent colours, and
the Virtual Desktop background colour.

**Derivation** (`FramePaletteBuilder`, pure, in `NewsCentral.Shared`; the same rules apply on every poster
path: scheduled, `--manual`, the Expanded notification, on a Virtual Desktop and on its remote-session
fallback; RTL layout is unaffected):

- **Surfaces** — side panel, title bar and outer border = `FrameColor`.
- **Primary text** — `FrameTextColor` if valid, otherwise white or black, whichever has the higher WCAG
  contrast ratio against `FrameColor` (e.g. `#F0E6D2` → black, `#1E3A5F` → white).
- **Cards and secondary buttons** (info card, countdown card, Wallpaper, Lock screen) — `FrameColor` shifted
  toward the better-contrast extreme by a fixed small amount: **lighter on dark frames, darker on light
  frames** (10%; hover 18%, press 5%). The direction follows the frame, not an explicit text colour. With the
  automatic text colour the shift is trimmed where needed so text on a card keeps at least 4.5:1 contrast
  (an explicit `FrameTextColor` is the admin's choice and is not adjusted).
- **Secondary text** ("seconds", disabled button text) — primary text blended 35% toward the card colour.
- **Countdown progress bar** — track = card blended 30% toward the text colour; fill = the text colour; the
  paused state = text blended 60% toward the card.
- **Hairlines and edge** — the separator line, card borders and the thin outer edge lines are `FrameColor`
  shifted 22% / 30% toward the same extreme.
- **Notification form** — `NotificationColors`: background = `FrameColor`, border = the hairline shade, heading and
  title = primary text (with no frame colour the title keeps the theme's dimmer secondary colour, exactly as
  before), × = secondary text and primary text on hover, hover fill = the card hover shade, accent stripe =
  the accent. With no usable frame colour it is exactly the theme look.
- **Online/Offline pill** — `PillColors`: background and border follow the card shade and hairline; the dot keeps
  its green/red status colour, and so does the text unless that colour has less than 4.5:1 contrast on the card,
  in which case the text uses the primary text colour (the dot stays green/red). With no frame colour the pill is
  drawn exactly as before, whatever its contrast.
**Fallbacks** — nothing throws, and a problem never blocks the poster:

- No `FrameColor` (or whitespace): the poster looks exactly as before; the palette **is** the theme.
- Invalid `FrameColor` (anything but `#RRGGBB`: `#12345`, `123456`, `#GGGGGG`, a name): the **whole frame**
  falls back to the theme, and the launch's `launch.log` line gets `note=FrameColorInvalid`.
- Invalid `FrameTextColor` with a valid `FrameColor`: the automatic black/white colour is used and the line
  gets `note=FrameTextColorInvalid`. Both notes can appear together (joined with `,`, after
  `VirtualDesktopFallback` if present). The notes are appended to every decision line of that launch, notification (`MiniForm`) lines included.

**Setting it** (`Set-RegistryOverrides.ps1`, NewsViewer only; values are validated as `#RRGGBB`, an empty
string removes the value):

```powershell
.\scripts\Set-RegistryOverrides.ps1 -Company "<Company>" -ComponentName NewsViewer -FrameColor '#1E3A5F'
.\scripts\Set-RegistryOverrides.ps1 -Company "<Company>" -ComponentName NewsViewer -FrameColor '#1E3A5F' -FrameTextColor '#FFD700'
.\scripts\Set-RegistryOverrides.ps1 -Company "<Company>" -ComponentName NewsViewer -FrameColor '' -FrameTextColor ''   # back to the theme
```

In production deliver the same two REG_SZ values by GPO. The setting is read once at launch.

### Strings — Resources/UiStrings.resx

All user-facing viewer strings live in `NewsViewer/Resources/UiStrings.resx` (default EN),
read through the `UiStrings` accessor class (`ResourceManager` + `CurrentUICulture`) — adding
`UiStrings.<culture>.resx` satellite files localizes the viewer with no code change. Culture
satellites **de / es / fr / pt / ar / zh-Hans / zh-Hant** exist (`UiStrings.es.resx` etc.); the culture is resolved from
`CurrentUICulture` at each lookup, and a further culture is added by dropping in another
`UiStrings.<culture>.resx` — no code change.

**Languages:** EN (default), de, es, fr, pt (Brazilian wording, serves pt-PT too), ar, zh-Hans, zh-Hant.
**Fallback chains:** zh-CN/zh-SG → zh-Hans; zh-TW/zh-HK/zh-MO → zh-Hant; pt-BR/pt-PT → pt; ar-* → ar; anything else → EN.
**CJK fonts:** when `CurrentUICulture` reaches zh-Hans the UI font family is `Microsoft YaHei UI`; zh-Hant uses `Microsoft JhengHei UI` (both fall back to `Segoe UI` if missing via `Ui.Resolve`). Latin, Cyrillic and Arabic cultures keep Segoe UI Variable.

**Right-to-left (Arabic):** when `CurrentUICulture.TextInfo.IsRightToLeft` is true the viewer mirrors its layout explicitly (`Form.RightToLeftLayout` is not used). The side panel sits on the left with its separator on its right edge, the image and poster strip move right, the side panel is `RightToLeft.Yes` (inherited by its children), and the auto-close header (inherited `RightToLeft.Yes` with a `LeftToRight` flow) places the checkbox right of the caption. `RoundedButton` and the status pill add `TextFormatFlags.RightToLeft` when their effective `RightToLeft` is `Yes`. The poster text label is forced `RightToLeft.No` — it is authored content, not UI text. LTR cultures are laid out exactly as before.

**Debug-only `--culture <name>`:** in Debug builds only, `--culture <name>` (e.g. `--culture ar-SA`) forces `CurrentUICulture`/`DefaultThreadCurrentUICulture` before any form is built, for localization testing; an invalid name is ignored (logged via `Debug.WriteLine`), `CurrentCulture` is untouched, and it combines with `--manual` in any order.

## Display Duration

`DisplayDurationSeconds` carries three meanings via a sentinel model:

| Value | Meaning |
|---|---|
| `-1` | **Never auto-close** — RESERVED. No authoring UI can set it yet. |
| `0` | Unset → resolves to `PresentationDefaults.DisplayDurationSeconds` (**30**). |
| `> 0` | That many seconds. |

Resolution happens **once**, in `Program.cs`, via `PresentationDefaults.ResolveDuration`. `ViewerForm` performs **no** resolution of its own — it receives an already-resolved value. Any **negative value other than `-1`** is treated as unset (→ 30), **not** as never-close (the `NeverAutoClose` check precedes the unset check in `ResolveDuration`).

> **`-1` = never auto-close.** On a virtual desktop this is a modal takeover with the Close button as the sole exit. Any future UI exposing `-1` must address the VD interaction.

### Styling — FluentControls.cs

`NewsViewer/Forms/FluentControls.cs` defines `Theme` — an instance palette with the static
**Light** (classic Fluent gray) and **Dark** (default) instances and the startup-selected
`Theme.Current` — plus two custom-painted controls: `RoundedPanel` and `RoundedButton`
(`Radius = 0` → square; buttons have hover/press fill and an accent hover/press border;
`IsPrimary = true` renders the accent-filled primary style — hover/press shaded ±12%, white
text, no contrasting border). Every control font comes from one private helper
(`Ui.Font` in `ViewerForm`): Segoe UI Variable Text (Display at ≥13pt) with Segoe UI fallback —
no scattered `new Font(...)`. All public control properties carry
`[DesignerSerializationVisibility(Hidden)]` to satisfy analyzer WFO1000.

## Virtual Desktop (overview)

When `Presentation.UseVirtualDesktop = true`: creates a new Windows desktop via `CreateDesktop` / `SwitchDesktop` / `SetThreadDesktop`. Taskbar not visible. Background set to `VirtualDesktopBackgroundColor`. In a remote session (RDP / Citrix / VMware Horizon) desktop switching is not reliable, so the poster falls back to the standard desktop — see "Remote sessions".

## Remote sessions

NewsViewer runs in remote sessions (RDP, Citrix, VMware Horizon): posters and notifications are shown there like on the console. The exclusion that earlier phases applied (a silent early exit) was a simplification and is gone; a remote session now goes through the normal launch decision and **every exit is logged**.

**Detection** (`RemoteDetection.IsRemote`, gathered in `LaunchProbes.IsRemoteSession()`): a session is remote when **any** of these is true —

- `GetSystemMetrics(SM_REMOTESESSION)` is non-zero (RDP and Citrix ICA both set it);
- the `ViewClient_Machine_Name` environment variable is set (VMware Horizon sets it in every user session);
- the WTS client protocol type of the current session is not console (`WTSClientProtocolType` ≠ 0).

An unreadable signal simply does not count. The result is cached for the life of the process.

**Escape hatch — `Display:ShowInRemoteSessions`** (bool, REG_DWORD, default `true`; `Set-RegistryOverrides.ps1 -ShowInRemoteSessions`, NewsViewer only). `false` restores the previous behavior: the launch exits with `reason=RemoteSession` (rule 4 of the decision). `--manual` bypasses the switch.

**Virtual Desktop fallback.** A poster whose presentation has `UseVirtualDesktop` is shown on the **standard desktop**, through the normal path, whenever the session is remote — gate launches, `--manual` and the Expanded notification alike (`LaunchDecider.UseVirtualDesktop`). Desktop switching (`CreateDesktop` / `SwitchDesktop`) is not reliable over a remote protocol. The launch-log line of such a launch carries `note=VirtualDesktopFallback`. The console session is unchanged.

**NoShell guard.** In a published-app (seamless) session — Citrix/RDS RemoteApp — applications run without Explorer, so there is no taskbar window and no desktop to put a poster on. `HasShell()` is `FindWindowW("Shell_TrayWnd", null) != 0`; without a shell the launch exits with `reason=NoShell` (after `NotVisible`, before the idle and busy checks). A failed probe counts as "has a shell": a probe error never blocks the poster. `--manual` bypasses it.

**Wallpaper style re-assert.** `ApplyWallpaperStyle()` is a **no-op whenever the session is remote**, regardless of `Display:ShowInRemoteSessions` (`LaunchDecider.ShouldAssertWallpaperStyle`). On a session host NewsService's wallpaper surface may be switched off (the documented RDS/VDI opt-out), and each user's own HKCU wallpaper style and background colour must not be rewritten there. This is the one part of the old exclusion that is kept.

**Minimum monitor.** The Full-HD-or-better monitor requirement is unchanged. A remote client window smaller than 1920×1080 exits with `reason=NoQualifyingMonitor` (it used to be folded into `NoContent`). Whether to relax the threshold for remote sessions is a later decision.

**Debug builds only:** `--simulate-remote` makes `IsRemoteSession()` return true, to exercise the switch and the Virtual Desktop fallback on a console session. Combinable with the other switches (`--manual`, `--test-notify`, `--culture`).

## Virtual Desktop — Full Details (Phase 2)

Virtual Desktop governs **how the poster is presented** — it does not gate or exclude NewsViewer's independent wallpaper style re-assert, nor NewsService's out-of-process lock-screen/wallpaper-image applies. When `assignment.UseVirtualDesktop = true`:

1. `Program.Main` spawns a **fresh STA thread** (`uiThread`) for all virtual-desktop UI. This is required because `Application.EnableVisualStyles()` and other WinForms startup calls on the main thread create hidden internal windows (the WinForms parking window, etc.). `SetThreadDesktop` silently returns `false` once a thread owns any window handle; using a fresh thread that has never touched WinForms guarantees the call succeeds.
2. On the new thread: `VirtualDesktopManager` is constructed — saves the original desktop handle (`GetThreadDesktop`) and creates a new named desktop (`CreateDesktop("NewsViewer", ...)`).
3. `SwitchToNew()` is called — `SwitchDesktop` makes the new desktop visible; `SetThreadDesktop` binds the new thread to it. **This must happen before any window handle is created on the thread.**
4. `BackgroundForm` (borderless, maximised, `VirtualDesktopBackgroundColor`) is shown, filling the new desktop.
5. `ViewerForm` is shown on top (`TopMost = true`).
6. On `ViewerForm.FormClosed`: `BackgroundForm` is closed first (still on new desktop context), then `SwitchToOriginal()` returns the user to the default desktop.
7. `VirtualDesktopManager.Dispose()` calls `CloseDesktop` to release the handle.
8. The main thread blocks on `uiThread.Join()` until the viewer closes, then the process exits.

Win32 P/Invoke declarations are in `NativeMethods.cs` (`DllImport`, `CharSet.Unicode`, no unsafe blocks).

## Wallpaper Application

**NewsViewer no longer selects or applies a wallpaper image.** Wallpaper-image ownership migrated to NewsService: the image is enforced machine-wide through PersonalizationCSP (`DesktopImagePath`/`DesktopImageUrl`/`DesktopImageStatus`), published, verified, and three-state applied/cleared exactly like the lock screen — see `docs/newsservice-spec.md` → "Display surfaces". `PresentationSelector.SelectActiveWallpaper` has been **removed**; `SelectActive` (the poster path) is unaffected.

> **Verified on Windows 11 Enterprise — the change is no longer instant.** Before this migration, NewsViewer called `SystemParametersInfo` with `SPIF_SENDCHANGE` and the desktop repainted within seconds of a change. NewsService applies the wallpaper image from session 0, which cannot broadcast `WM_SETTINGCHANGE` into a user session — Windows only re-evaluates PersonalizationCSP at a lock/unlock or a logon, so content published mid-day will not appear on an already-unlocked machine until the next session boundary. This is expected, not a fault; see `docs/newsservice-spec.md` → Step 2 for the full timing note.

NewsViewer retains a small, deliberate **style-only** residue. Verified on Windows 11 Enterprise: PersonalizationCSP enforces **which** image is shown (Windows Settings greys out the wallpaper picker), but it does **not** cover **how** that image is fitted — the per-user HKCU `WallpaperStyle`/`TileWallpaper`/`Colors\Background` values still control Fill/Fit/Stretch/Center/Tile framing, and NewsService cannot reach per-user HKCU from its session-0 context. `NewsViewer/Services/WallpaperService.cs` exists solely to keep that style correct for whatever image CSP currently has applied — it is a thin, AOT-clean shell, **HKCU only, no COM/`IDesktopWallpaper`, no `SystemParametersInfo`** (`Microsoft.Win32.Registry` only, no extra packages, no unsafe code).

`void ApplyWallpaperStyle()`:
- Writes `HKCU\Control Panel\Desktop`: `WallpaperStyle`/`TileWallpaper` from the configured style (default **Fit** → `WallpaperStyle="6"`, `TileWallpaper="0"`; also Fill `10`/`0`, Stretch `2`/`0`, Center `0`/`0`, Tile `0`/`1`).
- Sets a uniform desktop background colour so Fit letterbox bars are even: `HKCU\Control Panel\Colors\Background = "R G B"` plus `SetSysColors(COLOR_DESKTOP)` (configurable, default `"0 0 0"`).
- Takes no image parameter and performs no image-existence check — there is no image to apply here.

**Control flow (terminal step, re-asserted every run, unconditional — no selection, no viewerstate):**
- **A no-op in a remote session** (`LaunchDecider.ShouldAssertWallpaperStyle`), regardless of `Display:ShowInRemoteSessions`: on a session host NewsService's wallpaper surface may be off, and each user's HKCU style and background colour must not be rewritten there (see "Remote sessions"). On a local interactive session it runs and is **not** gated by `HasQualifyingMonitor`.
- Runs **unconditionally** — regardless of whether any wallpaper content is active, since NewsViewer no longer knows or cares whether wallpaper content exists; the style must be correct for whatever image CSP has applied, always.
- Runs as the **terminal** step, after `ViewerForm` closes and the virtual desktop (if used) is switched back and destroyed — on the main thread / original desktop, never on the temporary VD.

> **Test ritual — stale wallpaper impersonates fresh behaviour.** The wallpaper image lives in `HKLM\...\PersonalizationCSP` (NewsService), and the style lives in **HKCU** (NewsViewer) — either can **survive a `%ProgramData%` cache wipe**, so a wallpaper left over from an earlier test will look like fresh output of the run under test. Reset the wallpaper to a known neutral image and style **before each end-to-end run**.
>
> **Test ritual — an elevated Registry Editor shows a different HKCU.** `HKEY_CURRENT_USER` resolves per process token. A `regedit.exe` elevated under a separate privileged account — normal in enterprises that issue distinct admin accounts — shows *that account's* HKCU, not the logged-in user's, with nothing in the UI indicating it. NewsViewer's style writes land in the interactive user's hive and are invisible in such a window. This produced a false "the style re-assert is broken" during field verification; the writes were correct throughout. Check with `HKEY_CURRENT_USER\Volatile Environment` → `USERNAME`, and verify HKCU values from a non-elevated Registry Editor running as the logged-in user — the same class of trap as the stale-wallpaper one above: the observation method lying, not the code.

Config (`Delivery` section, NewsViewer): `WallpaperStyle` (default `Fit`), `WallpaperBackgroundColor` (default `"0 0 0"`) — style only. `DefaultWallpaperPath` lives on the NewsService side now. See `docs/configuration.md`.

## Session Telemetry

Writes `session-{guid}.json` to `%programdata%\NewsCentral\uploads\` at session end. The record is a `NewsCentral.Models.SessionTelemetry` instance (defined in `NewsCentral.Shared`); `TelemetryWriter` signs it via `HmacService` before serializing to disk.

### Notification records

The "News arrived!" notification is tracked through the same session-file mechanism and the same gates (HMAC signing in `TelemetryWriter`; `Telemetry:UploadEnabled` and the retention sweep in NewsService) — there is no new upstream channel or setting. `SessionTelemetry` gained three **optional** fields that are omitted from the JSON when null, so the signed form of an ordinary poster record is unchanged:

| Field | Meaning |
|---|---|
| `recordType` | `"notification"` for a notification record; absent for a poster display session |
| `source` | `"notification"` on a poster display that was opened by expanding a notification; absent otherwise |
| `contentKey` | the content key (`presentationId|vN|imageHash`) of a notification record |

- **Notification record** (one per *shown* form): `recordType = "notification"`, `presentationId`, `teamId`, `contentKey`, `sessionStartTime` = shown, `sessionEndTime` = outcome time (time to outcome = the difference, hover and lock pauses included), `closeReason` = `Expanded` | `Dismissed` | `TimedOut`.
- **Expanded** additionally produces the normal poster display record (same shape, its own `closeReason` as today) with `source = "notification"`.
- `--test-notify` and `--manual` produce no notification records (`--manual` keeps its existing tracking).
- **Rollout order.** An older NewsService verifying the HMAC of a notification record or a `source`-marked poster record deserializes without the new fields and computes a different signature, so it discards those records as invalid. Roll NewsService out before (or with) NewsViewer when HMAC is configured; ordinary poster records are unaffected either way.
- **Consumers must filter.** Notification records are told apart from poster sessions only by `recordType`. Anything that counts or reports poster sessions must exclude `recordType == "notification"`, or notifications are counted as poster displays and an Expanded notification is counted twice (its notification record and the `source: notification` poster record). At present nothing in the solution aggregates the files (NewsService only verifies and forwards them; the authoring app does not read `uploads\`), so this rule is for future reporting. Format reference: `docs/data-model.md`.
