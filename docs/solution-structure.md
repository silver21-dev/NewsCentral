# Solution Structure

## Projects in Solution

```
NewsCentral.sln
├── NewsCentral.Shared\          .NET 10 class library — shared domain models
├── NewsCentral.Shared.Tests\    .NET 10 xUnit test project — ECDSA signing core + config reader + SignatureGate + Entra resolver/merger + key-with-content precedence + shared readers + active-assignment selector (all passing)
├── NewsCentral\                 .NET 10 MAUI Blazor Hybrid — authoring app (Phase B2 complete)
├── NewsService\                 .NET 10 Windows Service — cache sync agent (implemented; Entra device team resolution end-to-end, phases 1–3b)
├── NewsService.Tests\           .NET 10 xUnit test project — Entra extension-attribute mapper + resolution orchestrator + effective-team union + registry-gated three-state apply for both display surfaces (lock screen + wallpaper), cross-surface isolation (offline seams; all passing)
└── NewsViewer\                  .NET 10 WinForms — end-user presentation viewer (Phase 2 complete; desktop wallpaper STYLE re-assert only — the wallpaper image and the lock screen are both owned by NewsService)
```

## Project References

```
NewsCentral.Shared   (no project references)
       ↑
       ├── NewsCentral
       ├── NewsService
       └── NewsViewer
```

NewsCentral and NewsService both reference **only** `NewsCentral.Shared`. NewsService does **not** reference NewsCentral (a MAUI project cannot be referenced from a non-MAUI project without MAUI build tasks propagating into the referencing project).

## NewsCentral.Shared — Model Layout

```
NewsCentral.Shared\
├── Models\
│   ├── IEntity.cs                     interface IEntity { GetId(); SetId(); }
│   ├── Presentation.cs                implements ISignable
│   ├── Schedule.cs                    implements ISignable
│   ├── Assignment.cs                  implements ISignable
│   ├── SessionTelemetry.cs            cross-component DTO; implements ISignable
│   ├── Team.cs                        Team, TeamsCollection
│   ├── TeamSigningKeys.cs             ECDSA key pair for a team: PrivateKey (PKCS#8), PublicKey, PublicKeyPrevious (SPKI)
│   ├── User.cs                        User, TeamRole, UsersCollection
│   ├── ResolvedTeams.cs               ResolvedTeamsFile, ResolvedTeamEntry, ResolvedTeamState, ResolvedTeamSource — resolved-teams.json (Entra dynamic teams)
│   └── IndexFile\
│       ├── TeamIndexFile.cs           root structure for index.json; implements ISignable
│       ├── PublishedAssignmentIndex.cs one entry per published assignment
│       ├── ContentInfo.cs             image path, hash, size, last-modified, URL
│       ├── DisplayTypeInfo.cs         IsNewsOfWeek, IsWallpaper, IsLogonScreen
│       ├── ActiveAssignmentSelector.cs pure IsActive + PickNewestToTeam(predicate) (NewsViewer poster: later of PresentationLastModified and PublishedDate) + PickNewestActive(predicate) (NewsService lock screen / wallpaper: newest by PresentationLastModified)
│       └── IndexStatistics.cs         summary counts
└── Security\
    ├── ISignable.cs                   interface ISignable { string? Signature { get; set; } }
    ├── HmacOptions.cs                 POCO: SecretKey (Base64 string)
    ├── HmacService.cs                 Sign<T>, Verify<T>, VerifyResult enum
    ├── EcdsaSignatureService.cs       stateless ECDSA P-256/SHA-256 Sign<T>/Verify<T>; IEEE P1363; key-list rotation
    ├── SignatureGate.cs               ShouldReject(result, requireSignedIndex, out reason) — centralizes accept/reject for index verification
    └── SigningKeyTool.cs              GenerateKeyPair, DerivePublicKey, Truncate — key-management helpers
```

All model namespaces are `NewsCentral.Models` and `NewsCentral.Models.IndexFile` — identical to their previous location in the NewsCentral project, so no using-directive changes were required in NewsCentral when the shared library was extracted.

## NewsCentral.Shared — Configuration Layout

```
NewsCentral.Shared\
└── Configuration\
    ├── RegistryConfigurationProvider.cs   IConfigurationProvider/IConfigurationSource + AddRegistryOverrides() extension
    ├── TeamConfigurationReader.cs         GetTeams(IConfiguration) helper
    ├── SigningKeyConfigurationReader.cs   GetPublicKeys(IConfiguration, teamFolderName) — reads Signing:{team}:PublicKey / :PublicKeyPrevious for ECDSA verification
    ├── SolutionConstants.cs               SolutionName = "NewsCentral" — fixed hive segment for AddRegistryOverrides(company, SolutionName, component)
    ├── EntraTeamNameResolver.cs           pure per-scheme attribute resolution: selector → rule → canonicalized team folder name
    ├── EntraResolvedTeamsMerger.cs        pure grace state machine keyed (Source, SourceId); EntraSourceKey, EntraSourceOutcome
    ├── GroupTeamDecision.cs               pure group-team membership decision: inclusion ∧ ¬instanceExclusion ∧ ¬globalExclusion
    ├── TeamFolderNameCanonicalizer.cs     shared canonicalization used by attribute schemes, group teams, and group-instance id derivation
    ├── ResolvedTeamsReader.cs             ReadDynamicTeamFolders(cacheRootPath) — reads resolved-teams.json
    ├── EffectiveTeams.cs                  Union(staticTeams, dynamicTeams) — de-duplicated, ordinal-ignore-case
    ├── ServiceEventIds.cs                 ContentChanged = 2001 — the EventLog event ID NewsService writes and the NewsViewer task subscribes to
    ├── ConfigManifests.cs                 per-component manifest of every registry/appsettings key (name, REG_ type, default, hint) — drives the Config Review page and the manifest-coverage test
    └── AppConfiguration.cs                typed accessor over IConfiguration for the NewsCentral authoring app (DataPath, distribution, Azure/HMAC; no admin creds — M5b removed the Initialization:* seed keys)
```

## NewsCentral.Shared — Launch and Ui Layout

```
NewsCentral.Shared\
├── Launch\
│   ├── LaunchDecision.cs          pure NewsViewer launch decision: LaunchInputs, ILaunchProbes (IsRemoteSession / IsSessionVisible / HasShell / IdleTime / IsBusy), LaunchDecider.Decide (the ordered rule table), UseVirtualDesktop + ShouldAssertWallpaperStyle, ContentKeys, RemoteDetection, FullScreenHeuristic, ProbeMath, LaunchLogTrimmer
│   └── NotificationLogic.cs       NotificationCountdown (60 s, hover/lock pause, first outcome wins), NotificationPlacement (bottom-right/left, DIP scaling), LaunchLogLine (the launch.log line format)
└── Ui\
    ├── MoreInfoUrlPolicy.cs       MoreInfoUrlPolicy, MoreInfoDecision — whether the poster's More information link may open (https + Ui:AllowedDomains); pure
    └── FramePalette.cs            Rgb, ChromePalette, FramePaletteBuilder — poster frame/text colours (Ui:FrameColor / Ui:FrameTextColor) derived from the theme; pure, no System.Drawing
    (the other files in Ui\ — thumbnail cache, selection and bulk-operation helpers — belong to the authoring app)
```

## NewsService — Service Layout

```
NewsService\
├── Configuration\
│   └── ServiceConfiguration.cs    typed POCOs bound from appsettings.json; includes HmacOptions
├── Models\
│   └── StatusFile.cs              status.json structure
├── Services\
│   ├── IRepositoryReader.cs       abstraction over Share / Azure repository
│   ├── LocalShareRepositoryReader.cs  file share implementation (primary)
│   ├── AzureBlobRepositoryReader.cs   Azure implementation — Certificate / ClientSecret auth
│   ├── CacheManager.cs            all local cache I/O; SHA-256 sidecar hashes
│   ├── ImagePublisher.cs          IImagePublisher — re-verifies + copies the winning image per surface into a protected, non-user-writable folder (prefix "lockscreen"/"wallpaper") before CSP is pointed at it
│   ├── PersonalizationService.cs  IPersonalizationService — read/write/clear PersonalizationCSP for BOTH surfaces (lock screen + desktop wallpaper; SYSTEM context)
│   ├── TelemetryUploader.cs       deserializes and HMAC-verifies session-*.json; forwards Valid/Unsigned, discards Invalid (poster and notification records alike — it does not filter by recordType)
│   ├── IContentChangedNotifier.cs IContentChangedNotifier + EventLogContentChangedNotifier — writes the ContentChanged EventLog event (Application / NewsService / 2001) that triggers NewsViewer's "Content Arrived" task
│   ├── IDeviceIdentityProvider.cs   seam for DeviceIdentityProvider
│   ├── DeviceIdentityProvider.cs    local Entra AD DeviceId read (registry / dsregcmd fallback)
│   ├── IEntraDeviceClient.cs        seam for EntraDeviceClient
│   ├── EntraDeviceClient.cs         Graph device fetch — extensionAttributes via AdditionalData (Kiota UntypedObject)
│   ├── EntraExtensionAttributeMapper.cs   maps device AdditionalData → the 15-attribute dictionary
│   ├── IEntraGroupClient.cs         seam for EntraGroupClient; EntraGroupSnapshot / EntraGroupStatus
│   ├── EntraGroupClient.cs          Graph group name→id resolution + batched checkMemberGroups snapshot
│   ├── GraphFailureClassifier.cs    pure exception → GraphFailureKind (persistent vs transient)
│   ├── GroupOutcomeMapper.cs        pure EntraGroupSnapshot → EntraSourceOutcome, incl. global-exclusion fail-closed
│   ├── GroupMembershipChunker.cs    pure checkMemberGroups 20-id chunking + id→name mapping
│   ├── EntraTeamResolutionService.cs   orchestrates one Entra resolution cycle; writes resolved-teams.json
│   ├── ViewerTaskVerificationService.cs   BackgroundService - verifies shortly after start, then hourly (Mode VerifyOnly); logs drift on change; Off = logs once. Never creates or repairs
│   ├── ViewerTaskVerifier.cs        read-only check of both NewsViewer tasks: nc-hash stamp, else field comparison (TaskDriftComparer)
│   ├── ViewerTaskMode.cs            VerifyOnly | Off resolver (default VerifyOnly)
│   ├── TaskDefinitionBuilder.cs     pure: embedded deploy/scheduled-tasks XML + inputs → final task XML + SHA-256 stamp; hash extraction
│   ├── TaskQueryClient.cs           ITaskQueryClient (read-only) over schtasks.exe /Query (no COM, no write verb)
│   ├── EmitTaskScriptCommand.cs     --emit-task-script <path> [--newsviewer-path exe] - build-time script generation, before host construction
│   ├── TaskScriptGenerator.cs       pure: inputs + embedded template bytes -> the self-contained registration script (stamps nc-hash)
│   ├── TaskScriptEmitter.cs         atomic ASCII write of the generated script (build machine only)
│   ├── TaskDefinitionInputsResolver.cs   one place the exe path / event log / source are resolved (verifier and generator)
│   ├── TaskDriftComparer.cs         field comparison used when a task carries no nc-hash stamp
│   ├── EventSourceGuard.cs          explicit EventLog source creation at startup; RegisterEventSource=false handling
│   └── SyncService.cs             orchestrates the poll cycle; ECDSA-verifies index.json via SignatureGate before caching; applies both display surfaces (lock screen, wallpaper) via a shared three-state dispatch core; writes each team's index.json last (only after every image is cached), self-heals missing cached images on an unchanged index, and raises one ContentChanged notification per cycle
├── JsonDefaults.cs                shared JsonSerializerOptions (WriteIndented + CamelCase + CaseInsensitive + enum converter)
├── Worker.cs                      BackgroundService host; reads interval from configuration
├── Program.cs                     DI wiring; registers HmacService; adds registry override source; storage mode resolved from merged config
└── appsettings.json
```

## NewsViewer — Layout

```
NewsViewer\
├── Configuration\
│   └── ViewerConfiguration.cs        typed POCOs bound from appsettings.json + registry; BypassDailyGate, BypassImageIntegrityCheck, HmacOptions; DisplaySection (LogicalDayStartHour, ShowInRemoteSessions); UiSection (ShowDeliveredImages, NotifyNewContent, FrameColor, FrameTextColor); DeliverySection (wallpaper STYLE only — WallpaperStyle/WallpaperBackgroundColor; DefaultWallpaperPath moved to NewsService)
├── Models\
│   └── ViewerState.cs                viewerstate.json structure (LastShownDate, LastShownPresentationId, lastContentKey marker)
│   (SessionTelemetry lives in NewsCentral.Shared — cross-component DTO)
├── Services\
│   ├── PresentationSelector.cs       reads index.json per team, ECDSA-verifies via SignatureGate; SelectActive (poster only) via ActiveAssignmentSelector.PickNewestToTeam; verifies image SHA-256
│   ├── WallpaperService.cs           desktop wallpaper STYLE-only applier — HKCU only, no SystemParametersInfo, no COM (SetSysColors); the image itself is NewsService/CSP-owned; a no-op in remote sessions
│   ├── ViewerStateService.cs         viewerstate.json: the date-only logical-day gate (AlreadyShownToday / RecordShown) and the per-user content marker (ReadContentMarker / WriteContentMarker); atomic writes
│   ├── LaunchProbes.cs               Win32 environment probes behind ILaunchProbes (LibraryImport): remote session, session visible, shell present, idle time, busy (notification state + full-screen foreground)
│   ├── LaunchLog.cs                  %LOCALAPPDATA%\NewsCentral\launch.log — one line per launch/outcome, 64 KB cap, never throws
│   ├── ForegroundWindow.cs           SetForegroundWindow wrapper used to bring the expanded poster to the front
│   ├── VirtualDesktopManager.cs      CreateDesktop/SwitchDesktop/SetThreadDesktop wrapper
│   └── TelemetryWriter.cs            HMAC-signs and writes session-{guid}.json to uploads\ — poster sessions (WriteSession) and notification records (WriteNotification)
├── Forms\
│   ├── ViewerForm.cs                 adaptive borderless poster window (image AR fitted in 1600×900, clamps 960×540); themed 220px side panel; takes Theme.Poster (frame colours) and can start with the countdown stopped
│   ├── NotificationForm.cs           the "News arrived!" mini form: non-activating, topmost, bottom-right (bottom-left RTL), 60 s with hover/lock pause, Expanded / Dismissed / TimedOut
│   ├── FluentControls.cs             Theme (Light/Dark palettes; Theme.Current from Ui\Theme, default Dark; Theme.Poster = Current + frame colours) + RoundedPanel/RoundedButton
│   ├── Ui.cs                         the single font source (Segoe UI Variable, Segoe UI fallback, CJK families)
│   └── BackgroundForm.cs             fullscreen solid-colour background for virtual desktop
├── Resources\
│   ├── UiStrings.resx                user-facing strings, default EN (satellites de, es, fr, pt, ar, zh-Hans, zh-Hant)
│   └── UiStrings.cs                  static accessor — ResourceManager + CurrentUICulture
├── NativeMethods.cs                  Win32 P/Invoke — desktop, thread, process APIs (DllImport; new code uses LibraryImport)
├── JsonDefaults.cs                   shared JsonSerializerOptions (same standard as NewsService)
├── Program.cs                        entry point: Active guard, mutex, theme + poster palette, selection, LaunchDecider.Decide, then FullPoster / MiniForm / Exit; poster path (Virtual Desktop or standard), terminal wallpaper STYLE re-assert (skipped in remote sessions); one-shot, exits after
└── appsettings.json                  shipped template (tracked; never edit or stage it for feature work — code defaults and the registry cover absent keys, see docs/configuration.md)
```

`ShowNewApplicationContext.cs` has been removed — NewsViewer is a one-shot process with **zero** `FileSystemWatcher` usage and no resident message pump.

## Deployment assets and scripts

```
deploy\scheduled-tasks\
├── NewsViewer-Launch.xml             task \{Company}\NewsCentral\NewsViewer Launch — logon (15 s) / unlock / RDP connect / console connect (UTF-16 LE with BOM; .gitattributes marks it binary); embedded into NewsService, which registers it
├── NewsViewer-ContentArrived.xml     task \{Company}\NewsCentral\NewsViewer Content Arrived — event 2001, --trigger content
└── README.md                         what each task does and why
scripts\
├── Set-RegistryOverrides.ps1         dev/pilot registry provisioning (GPO owns the fleet)
├── Register-NewsViewerTasks.ps1      dev/pilot task registration (+ the event source, legacy task and Run value removal); -Uninstall; the packaged equivalent is generated by NewsService.exe --emit-task-script
└── Remove-DisplaySurfaces.ps1        uninstall remediation for the PersonalizationCSP surfaces
```

## NewsCentral.Shared.Tests — Layout

```
NewsCentral.Shared.Tests\
├── EcdsaRoundTripTests.cs                  GenerateKeyPair → Sign → Verify (Valid / Invalid / Unsigned / Disabled / rotation fallback / tamper detection / PEM input / bad-key rejection)
├── SigningKeyConfigurationReaderTests.cs   GetPublicKeys contract (both keys, empty filter, null filter, no-config, team scoping)
├── SignatureGateTests.cs                   ShouldReject full matrix (VerifyResult × requireSignedIndex) + reason-string assertions
├── ActiveAssignmentSelectorTests.cs        IsActive window/day + PickNewestActive + PickNewestToTeam newest-to-team ranking; predicate independence (K03)
├── LaunchDecisionTests.cs                  every launch-decision rule in order, laziness of the probes, ContentKey, idle wrap-around, launch-log trimming
├── RemoteSessionDecisionTests.cs           RemoteSession / NoShell / NoQualifyingMonitor rules, remote detection, Virtual Desktop fallback, wallpaper-style gate, manifest entry
├── FullScreenHeuristicTests.cs             full-screen-foreground busy geometry (zoomed, work-area, shell classes, failures)
├── NotificationLogicTests.cs               NotificationCountdown state machine, placement/DPI maths, LaunchLogLine format (key as stored)
├── SessionTelemetryTests.cs                optional notification fields: HMAC unchanged for poster records, round-trip, tamper detection
├── FramePaletteTests.cs                    frame/text colour parsing, automatic contrast, shades, 4.5:1 sweep, fallbacks
└── … (Entra resolver/merger, delivered-key precedence, shared readers, EffectiveTeams, authoring-app helpers — all passing)

NewsService.Tests\ - also: SyncCompletenessTests (index-last, retry of an incomplete sync), CacheSelfHealTests, ContentChangedCycleTests (one event per cycle), ContentChangedNotifierTests, TaskDefinitionBuilderTests (real embedded XMLs), ViewerTaskScriptTests (mode resolver, script generator, stamp parity with PowerShell, emit command), ViewerTaskVerifierTests (stamp / field-comparison verification, obsolete-key warning, event-source creation).
```

## JSON Serialization Convention

All projects use the same options:

```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
};
```

## Toolchain

`global.json` at the repo root pins the .NET SDK to `10.0.401` with `rollForward: latestFeature` and
`allowPrerelease: false`: patch and feature-band updates within .NET 10 are picked up, a .NET 11 SDK or a
preview is not. If your machine has a different SDK, install a 10.0.x SDK at or above 10.0.401 (a lower 10.0.x
or a missing .NET 10 SDK fails the build with a "requested SDK version" error). To move the pin, edit
`global.json` in its own commit after running both test suites on the new SDK.
