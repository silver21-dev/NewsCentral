# NewsViewer — Component Specification

**Type:** WinForms (.NET 10) desktop application  
**Status:** Phase 2 complete. All Phase 2 features implemented and tested; the side panel was reworked from a hover-reveal to a fixed Fluent gray panel in the v2.7 UI pass, and to the themed 220px branded panel (adaptive image-fit window, Dark/Light themes, resx strings) in the UI v3 pass. Wallpaper-**image** ownership migrated to NewsService (PersonalizationCSP, machine-wide); NewsViewer retains only a per-user HKCU wallpaper **style** re-assert (no `SystemParametersInfo`; see Wallpaper Application). NativeAOT migration path preserved; Win32 P/Invoke via `DllImport` with simple types — no unsafe code required.

NewsViewer is a **one-shot process**: launch → evaluate the daily gate → render the poster (or not) → re-assert the wallpaper style → exit. There is no resident process, no `FileSystemWatcher`, and no held-open message pump. Each display decision is made afresh at launch.

## Launch Conditions

- Registered in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` for system startup
- Registered in Task Scheduler triggered by Workstation Unlock event
- Optionally launchable from the Windows Start menu (shortcut target `NewsViewer.exe --manual`; see Manual launch below)
- `appsettings.json` must be declared in `NewsViewer.csproj` as `<Content Include="appsettings.json">` with `CopyToOutputDirectory = PreserveNewest` so it is deployed alongside the executable
- **Configuration layers** (registry always wins): `appsettings.json` (shipped, neutral, **committed** — a tracked artifact, never gitignored — loaded `optional: false`, so a missing file is a hard startup failure) → `appsettings.Development.json` (optional, gitignored dev overlay, loaded `optional: true`, **never published** via `CopyToPublishDirectory=Never`; documented shape in `appsettings.Development.json.example`) → registry (GPO). `appsettings.json` is the base layer the MSI installs and GPO sits on top of. See `docs/configuration.md` → appsettings.json — NewsViewer.
- `Main()` evaluates the `Active` master switch (registry `Active`, DWORD 0/1 at the hive root; default `true`) first, ahead of every other startup check, and returns immediately when `false` — no poster, no wallpaper style re-assert, no `viewerstate.json` write, no telemetry, no dialog. The last-applied wallpaper style is left as-is (the wallpaper image is NewsService's concern, unaffected by NewsViewer's `Active` switch either way).
- `Main()` validates `CacheRootPath` after config load; if it is empty a `MessageBox` is shown and the process exits. (`Company` is **not** validated here — it is the build-time constant `SolutionConstants.Company`, enforced at build time, not read from config.)
- Registry `teams\` value names must match the generated folder name exactly — the sanitized team name with no `team-` prefix (e.g. `cz-its`, not `CZ_ITS`)
- **An empty static `teams\` list does not stop NewsViewer.** `Main()` reads the static list via `TeamConfigurationReader.GetTeams` but does not gate on its length — the effective team set used for presentation selection is static ∪ dynamic (`resolved-teams.json`, resolved inside `PresentationSelector`; see Presentation Selection below), so a machine configured purely with Entra-resolved teams still needs to reach that union. An early-return guard here once defeated that on a dynamic-only machine; mirrors the equivalent fix in `NewsService/Worker.cs` (`docs/newsservice-spec.md`).

### Launch surface — two complementary triggers

The two triggers are **complementary, not redundant**:

- **HKLM `…\Run`** fires once at **session start** (logon). It does **not** fire on workstation unlock.
- **Task Scheduler / Workstation Unlock** fires on every **unlock** thereafter. It does **not** fire at logon.

On always-on machines that are locked rather than logged off, **Unlock is the only trigger** for every logical day after the first. There is deliberately **no repeating/timer trigger** in this release — re-evaluation is event-driven (logon or unlock), and the daily gate below decides whether each launch actually shows anything.

## The daily gate

The gate is **date-only** and evaluated once per launch. `ViewerStateService.AlreadyShownToday()` takes **no presentation id** — it disturbs the user at most once per **logical day**, regardless of which presentation is active.

A **logical day** runs from `Display:LogicalDayStartHour` (default `0` = calendar day) to the same hour the next day, in **local** time. `AlreadyShownToday()` compares the stored `LastShownDate` against the current logical day; `RecordShown` stamps the logical day after a display.

**State file.** The gate state lives in `%LOCALAPPDATA%\NewsCentral\viewerstate.json` — **per-user, and NOT part of the `%ProgramData%\NewsCentral\` machine cache.** Consequence for testing: **wiping the `%ProgramData%` cache does not reset the daily gate** — delete `viewerstate.json` (or set `BypassDailyGate`) to force a re-display. `LastShownDate` stores the logical-day key (the `LogicalDayStartHour` boundary is already baked in), so the gate is a plain string-equality check with no time component. See `docs/data-model.md` → viewerstate.json.

**Rationale.** A night-shift worker who starts at 22:00 and unlocks again at 01:00 is still inside the *same* logical day and must not be shown the poster twice. Worked example — `LogicalDayStartHour = 5` (05:00 boundary) cleanly covers morning, afternoon, and night shifts: everything from 05:00 through 04:59 the next morning counts as one day, so a night-shift unlock after midnight does not re-trigger.

### Manual launch - `--manual`

`NewsViewer.exe --manual` (case-insensitive; unknown arguments are ignored) is the on-demand launch used by the Start menu shortcut. `Active` is still evaluated first, so `Active = false` exits silently even with `--manual`. A manual launch skips the daily-gate check and does **not** write the gate marker, so it neither depends on nor disturbs the scheduled once-per-day display. Everything else - the single-instance mutex, remote/virtual-session exit, poster selection, the no-poster behavior, session telemetry and the wallpaper style re-assert - follows the scheduled path unchanged. A manual launch is noted with a `[Manual]` debug line.

### Deliberate gap — no same-day delivery (design decision)

Content published **after** the day's poster has already shown **waits for the next logical day**. There is **no same-day delivery path** in this release, and this is **intended** ("do not disturb users much") — it is a design decision, not an omission or a bug to be fixed. The reserved `Priority` field (see `docs/data-model.md`) is the future, author-controlled escape hatch for urgent same-day content.

## Presentation Selection

- Reads `index.json` from every team in the **effective** team set — the static `teams` registry configuration unioned with the dynamic (Entra-resolved) set read from `resolved-teams.json` (`EffectiveTeams.Union`, de-duplicated ordinal-ignore-case) — not the static list alone
- **ECDSA verification** — calls `EcdsaSignatureService.Verify(index, keys)` where `keys = SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolderName)`: `Invalid` → skips the team entirely (no presentations shown from that team); `Unsigned` / `Disabled` / `Valid` → all logged distinctly, team accepted. Index is deserialized with standard ISO timestamp parsing (no `DateTime` converter) so values match the signed, persisted form.
- Selects the most recent active **News-of-the-Week** presentation by `PresentationLastModified` timestamp. Only `DisplayTypes.IsNewsOfWeek` assignments are eligible for the poster — a wallpaper-only / lock-screen-only assignment is never shown as a full-screen poster. (Wallpaper and lock-screen selection now happen in NewsService, from the same signed indexes, via the same shared helper — see below.)
- Active-window/day filtering and the "newest by `PresentationLastModified`" pick are the shared pure helper `NewsCentral.Models.IndexFile.ActiveAssignmentSelector` (`IsActive` + `PickNewestActive(assignments, now, predicate)`), unit-tested in `NewsCentral.Shared.Tests`. `SelectActive` uses predicate `a => a.DisplayTypes.IsNewsOfWeek`. The same helper is used by `SyncService` in NewsService for the lock-screen (`IsLogonScreen`) and wallpaper (`IsWallpaper`) winners — see `docs/newsservice-spec.md` — reading from the same signature-verified index enumeration; verification is never bypassed on either side.
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

When `Presentation.UseVirtualDesktop = true`: creates a new Windows desktop via `CreateDesktop` / `SwitchDesktop` / `SetThreadDesktop`. Taskbar not visible. Background set to `VirtualDesktopBackgroundColor`. Auto-detected and suppressed in RDP / Citrix / VMware Horizon sessions.

## Remote / Virtual Session Suppression (Phase 2)

Checked in `Program.Main` **before** the `PresentationSelector` is constructed — and well before `HasQualifyingMonitor()`, which is evaluated later in the poster-assignment guard — before any file I/O or window creation:

- **RDP and Citrix ICA** — detected via `SystemInformation.TerminalServerSession` (`GetSystemMetrics(SM_REMOTESESSION)`). Both RDP and Citrix ICA sessions set this flag.
- **VMware Horizon** — detected via the `ViewClient_Machine_Name` environment variable, which Horizon sets in every user session.

If either condition is true the process exits immediately and no window is shown.

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
- Skipped entirely on remote/virtual sessions (`IsRemoteOrVirtualSession`); otherwise runs on any local interactive session and is **not** gated by `HasQualifyingMonitor`. This condition is unchanged from before the wallpaper-ownership migration.
- Runs **unconditionally** — regardless of whether any wallpaper content is active, since NewsViewer no longer knows or cares whether wallpaper content exists; the style must be correct for whatever image CSP has applied, always.
- Runs as the **terminal** step, after `ViewerForm` closes and the virtual desktop (if used) is switched back and destroyed — on the main thread / original desktop, never on the temporary VD.

> **Test ritual — stale wallpaper impersonates fresh behaviour.** The wallpaper image lives in `HKLM\...\PersonalizationCSP` (NewsService), and the style lives in **HKCU** (NewsViewer) — either can **survive a `%ProgramData%` cache wipe**, so a wallpaper left over from an earlier test will look like fresh output of the run under test. Reset the wallpaper to a known neutral image and style **before each end-to-end run**.
>
> **Test ritual — an elevated Registry Editor shows a different HKCU.** `HKEY_CURRENT_USER` resolves per process token. A `regedit.exe` elevated under a separate privileged account — normal in enterprises that issue distinct admin accounts — shows *that account's* HKCU, not the logged-in user's, with nothing in the UI indicating it. NewsViewer's style writes land in the interactive user's hive and are invisible in such a window. This produced a false "the style re-assert is broken" during field verification; the writes were correct throughout. Check with `HKEY_CURRENT_USER\Volatile Environment` → `USERNAME`, and verify HKCU values from a non-elevated Registry Editor running as the logged-in user — the same class of trap as the stale-wallpaper one above: the observation method lying, not the code.

Config (`Delivery` section, NewsViewer): `WallpaperStyle` (default `Fit`), `WallpaperBackgroundColor` (default `"0 0 0"`) — style only. `DefaultWallpaperPath` lives on the NewsService side now. See `docs/configuration.md`.

## Session Telemetry

Writes `session-{guid}.json` to `%programdata%\NewsCentral\uploads\` at session end. The record is a `NewsCentral.Models.SessionTelemetry` instance (defined in `NewsCentral.Shared`); `TelemetryWriter` signs it via `HmacService` before serializing to disk.
