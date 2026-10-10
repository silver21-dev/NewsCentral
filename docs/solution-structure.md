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
│       ├── ActiveAssignmentSelector.cs pure IsActive + PickNewestActive(predicate); shared by NewsViewer's poster selection and NewsService's lock-screen/wallpaper selection
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
    └── AppConfiguration.cs                typed accessor over IConfiguration for the NewsCentral authoring app (DataPath, distribution, Azure/HMAC; no admin creds — M5b removed the Initialization:* seed keys)
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
│   ├── TelemetryUploader.cs       deserializes and HMAC-verifies session-*.json; forwards Valid/Unsigned, discards Invalid
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
│   └── SyncService.cs             orchestrates the poll cycle; ECDSA-verifies index.json via SignatureGate before caching; applies both display surfaces (lock screen, wallpaper) via a shared three-state dispatch core
├── JsonDefaults.cs                shared JsonSerializerOptions (WriteIndented + CamelCase + CaseInsensitive + enum converter)
├── Worker.cs                      BackgroundService host; reads interval from configuration
├── Program.cs                     DI wiring; registers HmacService; adds registry override source; storage mode resolved from merged config
└── appsettings.json
```

## NewsViewer — Layout

```
NewsViewer\
├── Configuration\
│   └── ViewerConfiguration.cs        typed POCOs bound from appsettings.json; includes BypassDailyGate, BypassImageIntegrityCheck, HmacOptions, and DeliverySection (wallpaper STYLE only — WallpaperStyle/WallpaperBackgroundColor; DefaultWallpaperPath moved to NewsService)
├── Models\
│   └── ViewerState.cs                viewerstate.json structure
│   (SessionTelemetry lives in NewsCentral.Shared — cross-component DTO)
├── Services\
│   ├── PresentationSelector.cs       reads index.json per team, ECDSA-verifies via SignatureGate; SelectActive (poster only — SelectActiveWallpaper removed, wallpaper selection now lives in NewsService) via shared ActiveAssignmentSelector; verifies image SHA-256
│   ├── WallpaperService.cs           desktop wallpaper STYLE-only applier — HKCU only, no SystemParametersInfo, no COM (DllImport SetSysColors); the image itself is NewsService/CSP-owned
│   ├── ViewerStateService.cs         reads/writes viewerstate.json for the date-only logical-day gate (AlreadyShownToday)
│   ├── VirtualDesktopManager.cs      CreateDesktop/SwitchDesktop/SetThreadDesktop wrapper
│   └── TelemetryWriter.cs            HMAC-signs and writes session-{guid}.json to uploads\ on close
├── Forms\
│   ├── ViewerForm.cs                 adaptive borderless WinForms window (image AR fitted in 1600×900, clamps 960×540); themed 220px side panel — wordmark, status pill, primary Close; Ui.Font helper (Segoe UI Variable, Segoe UI fallback)
│   ├── FluentControls.cs             Theme (Light/Dark palettes; Theme.Current selected via Ui\Theme registry, default Dark) + RoundedPanel/RoundedButton (custom-painted; IsPrimary accent fill)
│   └── BackgroundForm.cs             fullscreen solid-colour background for virtual desktop
├── Resources\
│   ├── UiStrings.resx                user-facing strings, default EN (satellite UiStrings.<culture>.resx per language)
│   └── UiStrings.cs                  static accessor — ResourceManager + CurrentUICulture
├── NativeMethods.cs                  Win32 P/Invoke — desktop, thread, process APIs
├── JsonDefaults.cs                   shared JsonSerializerOptions (same standard as NewsService)
├── Program.cs                        entry point; startup checks; remote-session guard; renders poster then re-asserts the wallpaper STYLE as the terminal step (after poster/VD teardown); one-shot, exits after
└── appsettings.json
```

`ShowNewApplicationContext.cs` has been removed — NewsViewer is a one-shot process with **zero** `FileSystemWatcher` usage and no resident message pump.

## NewsCentral.Shared.Tests — Layout

```
NewsCentral.Shared.Tests\
├── EcdsaRoundTripTests.cs                  9 facts: GenerateKeyPair → Sign → Verify (Valid / Invalid / Unsigned / Disabled / rotation fallback / tamper detection / PEM input / bad-key rejection)
├── SigningKeyConfigurationReaderTests.cs   5 facts: GetPublicKeys contract (both keys, empty filter, null filter, no-config, team scoping)
├── SignatureGateTests.cs                  14 facts: ShouldReject full matrix (VerifyResult × requireSignedIndex) + reason-string assertions
├── ActiveAssignmentSelectorTests.cs       10 facts: IsActive window/day + PickNewestActive newest-by-PresentationLastModified; IsWallpaper vs IsNewsOfWeek predicate independence (K03 wallpaper-only skipped by display selection)
└── … (Entra resolver/merger, delivered-key precedence, shared readers, EffectiveTeams — all passing)
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
