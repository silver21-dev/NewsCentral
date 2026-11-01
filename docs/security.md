# Security

> Anti-tamper quick reference (integrity values, config/registry matrix, static-vs-dynamic key-trust model): see `docs/anti-tamper.md`. This document covers the phase history and rationale.

## Per-Component Authentication

> Azure app-registration permissions (incl. Graph `Device.Read.All` / `GroupMember.Read.All`), storage RBAC, the signing certificate, and device/group provisioning: see `docs/azure-setup.md` — the single source for Azure setup.

### NewsCentral

- Detects UPN accounts and supports local accounts
- Azure Blob distribution uses `InteractiveBrowserCredential` (MSAL interactive user session); token persisted in named cache `"NewsCentral"` for non-interactive re-authentication. Config: `AzureBlob:{TenantId, ClientId, AccountName}` in appsettings.json.

### NewsService

- Azure Blob access uses `ClientCertificateCredential` or `ClientSecretCredential`; mode selected by `AzureBlob:AuthMode`
- Certificate loaded from `Cert:\LocalMachine\My` by thumbprint — Local System has access by default, no extra grants required
- Proactive token refresh via `Azure.Identity`; handles weeks-long uptime without restart

### NewsViewer

- Architecture is prepared for future authentication requirement to access `%programdata%\NewsCentral\` data
- No authentication implemented in this version

## Anti-Tamper — HMAC-SHA256

`HmacService` (in `NewsCentral.Shared/Security/`) provides end-to-end content integrity using HMAC-SHA256.

**Signing (NewsCentral):** `index.json` signing has moved to per-team ECDSA (see below); `HmacService` is no longer used for index signing in NewsCentral.

**Signing (NewsViewer):** `TelemetryWriter` calls `HmacService.Sign(record)` on each `SessionTelemetry` before writing to disk.

**Verification (NewsService):**
- `SyncService` verifies each `index.json` via ECDSA (Phase C — see below). HMAC is no longer used for index verification.
- `TelemetryUploader` verifies each `session-*.json` before forwarding. `Invalid` → file discarded and logged. The optional notification fields (`recordType`, `source`, `contentKey`) are part of the signed form only when present; an older NewsService discards records that carry them, so NewsService and NewsViewer ship together (`docs/anti-tamper.md`, `docs/packaging.md`).

**Verification (NewsViewer):** `PresentationSelector` verifies each team's `index.json` via ECDSA (Phase C — see below). HMAC is no longer used for index verification. After selecting the winning assignment, `PresentationSelector` also verifies the cached image file against `Content.ImageHash` (SHA-256). A missing hash is accepted with a warning; a mismatch returns `(best, null)` so the caller exits silently. Disabled by `BypassImageIntegrityCheck`.

**Key management:** `Hmac:SecretKey` is a Base64-encoded 32-byte key configured in `appsettings.json` or overridden via registry (`Hmac\SecretKey`). An empty key disables HMAC system-wide — all content is treated as `Disabled` and passes through. This enables phased rollout: deploy the key to all machines before enabling signing in NewsCentral.

**`Signature` field** is present in: `TeamIndexFile`, `SessionTelemetry` — the only signable types. (`Presentation`, `Schedule`, and `Assignment` carried an inert `Signature` property that was never signed or verified; it has been removed.)

## Per-Team Asymmetric Signing — ECDSA P-256 (Phases A, B1 & C — implemented and tested)

`EcdsaSignatureService` (in `NewsCentral.Shared/Security/`) provides ECDSA P-256 / SHA-256 signing and verification as the foundation for replacing the shared HMAC key on `index.json` with per-team asymmetric keys. Session telemetry remains on HMAC-SHA256 and is not affected.

**Algorithm:** ECDSA nistP256, SHA-256 hash, IEEE P1363 fixed-field concatenation (64-byte r‖s, Base64). Never DER.

**Canonical payload:** Identical approach to `HmacService` — `Signature` temporarily nulled, non-indented camelCase JSON with `JsonStringEnumConverter`. Signed bytes are byte-for-byte consistent across both services.

**Key material — `EnvironmentSigningKeys`:** Stored in the per-environment **secure store** at `{SecurePath}\keys\environment-signing.json` (M3; `SecurePath` in `environment.json`, default `{DataPath}\secure`) on the authoring tier only. One file per environment holds `current`, `next` and `previousPublicKey`, each with a `createdAt`. (Before M3 each team had its own `team-signing.json`; those files are ignored and must be deleted by the administrator. Before S02 they sat under the shared DataPath, where every author could read them.) Must never be written through `IBlobDistributionService` or synced to client machines. See **Authoring-tier hardening (S02)** and **M3** below.

| Field | Type | Used by |
|---|---|---|
| `PrivateKey` | Base64 PKCS#8 | NewsCentral — signs `index.json` |
| `PublicKey` | Base64 SPKI | NewsService, NewsViewer — verify `index.json` |
| `PublicKeyPrevious` | Base64 SPKI (optional) | Both verifiers — rotation window |

**Key rotation:** `Verify<T>` accepts `params string?[] publicKeysSpkiBase64`; pass `[PublicKey, PublicKeyPrevious]` to accept content signed by either key during a rotation window. A key attempt that throws counts as that key failing; `Invalid` is only returned after all supplied keys are exhausted.

**Key management — `SigningKeyTool`:** `GenerateKeyPair()` creates a fresh nistP256 key pair and returns Base64 PKCS#8 + SPKI; `DerivePublicKey()` accepts PEM or raw Base64 PKCS#8; `Truncate()` produces a short display string for the UI.

**Phase A — complete:** `EcdsaSignatureService`, `SigningKeyTool`, and `TeamSigningKeys` are implemented in `NewsCentral.Shared` and covered by `NewsCentral.Shared.Tests` (9 passing xUnit facts): sign/verify round-trip, wrong-key rejection, rotation key-list fallback, `Disabled` and `Unsigned` states, public-key derivation from both Base64 PKCS#8 and PEM input, malformed-input rejection (`ArgumentException`), and tamper detection (mutated payload fails verification).

**Phase B1 — complete:** `IndexGenerationService` (NewsCentral) signs `index.json` with the team's ECDSA private key loaded from `team-signing.json` (authoring tier only; never written through `IBlobDistributionService`). A team with NO key (file absent) is published unsigned with a warning and continues normally — phased-rollout safe. A key that exists but cannot be read is a different case and blocks publishing (S02, below). A publish-time self-verify guard was subsequently added (see **Publish-time self-verify guard** below).

**Publish-time self-verify guard:** After signing, `IndexGenerationService` immediately verifies that the signed index will pass client verification before `SaveIndexFileAsync` is called. The guard:
1. Serializes the signed `TeamIndexFile` with `GetIndexJsonOptions()` — producing the exact bytes that will be written to disk.
2. Deserializes with client-verifier options (`PropertyNameCaseInsensitive = true`, `JsonStringEnumConverter`, **no** `SmartDateTimeConverter`) — matching exactly how `SyncService` and `PresentationSelector` parse the file.
3. Calls `_ecdsa.Verify(reloaded, signingKeys.PublicKey)`. Any result other than `Valid` throws `InvalidOperationException` and aborts the publish before any file is written or pushed to the blob tier.

This catches canonical/persisted-form drift at the authoring tier — if a future change to `SmartDateTimeConverter` or the serializer options causes a byte mismatch, the error surfaces on publish rather than silently distributing an index every client will reject.

**Persisted-form signing:** The index is normalized through `GetIndexJsonOptions` before signing so the signed form equals the persisted form. `GetIndexJsonOptions` uses `SmartDateTimeConverter`, which writes whole-second timestamps; signing the in-memory object directly would cover sub-second precision that is lost on write. The index is round-tripped through `GetIndexJsonOptions` before the `Sign` call so the payload is byte-consistent with the file on disk. Verifiers must parse the persisted timestamps back to the same values (standard ISO parsing and `SmartDateTimeConverter` both do so faithfully).

**Phase B2 — complete:** TeamAdmin-gated Key Management page (`/key-management`) in NewsCentral. `IndexGenerationService.LoadTeamSigningKeysAsync` is now public; `SaveTeamSigningKeysAsync` added. The page lets a TeamAdmin (or SystemAdmin) for the current team: view the current public key (full, copyable for GPO) and truncated private key hint; generate a fresh ECDSA P-256 key pair (held in page state until applied); apply the new pair — sets `PublicKeyPrevious = old PublicKey` for rotation-window continuity and writes `team-signing.json` to the secure store (S02; originally via `IStorageService` under DataPath; never through `IBlobDistributionService`); republish the index as a separate deliberate action that triggers `GenerateAndSaveIndexAsync` (and the self-verify guard). The full private key is never displayed — `SigningKeyTool.Truncate` is used throughout. Nav item is gated on `AuthService.HasRole(currentTeamId, "TeamAdmin")` and reacts to team-context changes. Import-from-authority (paste/derive) is deferred.

**Phase C — complete (C1 + C2):** `SyncService` (NewsService, C1) and `PresentationSelector` (NewsViewer, C2) now verify `index.json` with `EcdsaSignatureService.Verify` using the registry public keys read by `SigningKeyConfigurationReader.GetPublicKeys(configuration)` - `Signing:PublicKey` and `Signing:PublicKeyPrevious` (flat since M1; they were per-team before). Both verifiers now route through `SignatureGate.ShouldReject` (see **Phase E** below). Outcome logging with the gate: rejected → `LogError`/`Debug.WriteLine` with the gate reason string; `Valid` accepted → `LogInformation`; `Unsigned` accepted (default) → `LogWarning`; `Disabled` accepted (default) → `LogInformation`. Reject action unchanged: NewsService aborts the team sync; NewsViewer skips the team entirely. Verifiers deserialize `index.json` with standard ISO timestamp parsing and no `DateTime` converter, so the parsed values byte-match the signed, persisted form.

**Phase D - complete:** `docs/configuration.md` and `docs/security.md` cover the per-team signing parameters and the `Signing:RequireSignedIndex` enforcement flag. `scripts/Set-RegistryOverrides.ps1` (`-RequireSignedIndex`, `-TeamPublicKeys`, `-TeamPreviousPublicKeys`) provisions them for developer and pilot machines; fleet provisioning goes through GPO instead, per `docs/production-deployment.md` Step 7.

**Phase E — complete: Fail-closed enforcement flag (`SignatureGate` + `Signing:RequireSignedIndex`):** `SignatureGate` (in `NewsCentral.Shared/Security/`) centralizes the accept/reject decision for ECDSA verification results. Both `SyncService` and `PresentationSelector` call `SignatureGate.ShouldReject(result, requireSignedIndex, out reason)` in place of inline `VerifyResult` checks.

`Signing:RequireSignedIndex` (bool, default `false` when this phase shipped; **`true` since M2**) is read from `IConfiguration` by both verifiers. Registry override: `<Component>\Signing\RequireSignedIndex` REG_SZ `"true"` or `"false"`.

| `VerifyResult` | `RequireSignedIndex = false` | `RequireSignedIndex = true` (default since M2) |
|---|---|---|
| `Valid` | accept | accept |
| `Invalid` | **reject** | **reject** |
| `Unsigned` | accept | **reject** |
| `Disabled` | accept | **reject** |

Default `false` preserves the Phase C pass-through behavior — only `Invalid` is rejected. Set `true` to go fail-closed: unsigned indexes and teams whose public key has not been registered are rejected with the same per-component action as `Invalid`. The two measures shipped together in commit `64d3171`. `SignatureGate` is covered by 14 new xUnit facts in `NewsCentral.Shared.Tests/SignatureGateTests.cs` (full `VerifyResult × requireSignedIndex` matrix plus reason-string assertions).

**Canonical serialization and `SmartDateTimeConverter`:** `IndexGenerationService` serializes `index.json` via `JsonConfiguration.GetIndexJsonOptions()`, which includes `SmartDateTimeConverter`. This converter truncates `DateTime` values to whole-second precision before writing (UTC → `2026-05-17T14:22:00Z`; Unspecified schedule times → `2026-05-18T09:00:00`). Before signing, the index is round-tripped through these options so the payload covered by the ECDSA signature is byte-for-byte identical to what lands on disk. Verifiers (NewsService, NewsViewer) use their own `JsonDefaults.Options` (no `SmartDateTimeConverter`) and standard ISO parsing — which faithfully round-trips whole-second timestamps — so the deserialized values match the signed form. Changing the serializer options on either side without re-signing will cause all verifiers to return `Invalid`.

## Dynamic-team key delivery (key-with-content) - removed in M1

Phase 3a had each index carry its own verification key so Entra-resolved dynamic teams needed no registry key. That path is gone (see **M1** below): dynamic teams verify exactly like static ones, against the flat registry `SigningPublicKey` / `PublicKeyPrevious`, selected by the signed `KeyId`.

**Shared helpers (Phase 3a):** `ResolvedTeamsReader.ReadDynamicTeamFolders(cacheRootPath)` (absent/unreadable/malformed → empty set, ordinal-ignore-case) and `EffectiveTeams.Union(staticTeams, dynamicTeams)` (de-duplicated, ordinal-ignore-case) — both in `NewsCentral.Shared/Configuration/`.

**Phase 3b — consumption wired (implemented and tested): the Entra feature is now end-to-end.** Both verifiers compute their team set as `EffectiveTeams.Union(static, dynamic)` — static from `TeamConfigurationReader.GetTeams`, dynamic from `ResolvedTeamsReader.ReadDynamicTeamFolders(cacheRootPath)` — and verify each team via `SignatureGate.Verify(index, team, GetPublicKeys(config))` → `ShouldReject` (existing `RequireSignedIndex` policy unchanged):

- **NewsService `SyncService`** — `RefreshEntraTeamsAsync` runs first so `resolved-teams.json` reflects the current cycle; `ResolveEffectiveTeams` (unit-tested in `NewsService.Tests`) then unions static ∪ dynamic and the cycle syncs that set. A dynamic team with no published `index.json` falls through existing not-found handling (skip). The blob/share pull and cache-write mechanics are unchanged.
- **NewsViewer `PresentationSelector`** — read-only consumer (never writes `resolved-teams.json`); unions registry + dynamic teams and applies the same verification. Single-winner display logic is unchanged (the poster winner is the newest-to-team item: the later of `PresentationLastModified` and `PublishedDate`). NewsViewer uses reflection-based `System.Text.Json` (no source-gen context), so `ResolvedTeamsReader` is consistent with how `index.json` is already read; the NativeAOT path is preserved.

End-to-end flow: **Entra device resolution → `resolved-teams.json` → effective-team union on both tiers → `SignatureGate.Verify`**. A team present in both the static list and the dynamic set is fine - `Union` de-dupes and verification is the same for both.

This symmetry was incomplete in practice until each tier's pre-Entra bootstrap guard was removed: both `NewsService/Worker.cs` and `NewsViewer/Program.cs` originally exited before the union was ever computed when the **static** team list was empty, which made a dynamic-only machine inert on that tier regardless of what Phase 3b wired downstream (NewsService fixed in `f8cd54b`, NewsViewer in the following commit).

Session telemetry (`session-*.json`) remains on HMAC-SHA256 throughout all phases.

## Poster "More information" link (S04, October 2026 security improvements)

The poster's **More information** button used to launch `Content.MoreInfoUrl` from the index through the shell with no checks, so an index author (or anyone who can tamper with an unsigned index) could make users open any scheme or handler: `file:`, `ms-settings:`, custom protocol handlers, UNC paths. Control: the button is enabled only for an absolute `https` URL without user information whose host equals a domain in `Ui\AllowedDomains` or is a subdomain of one (a bare suffix match is never used, so `evilcontoso.com` does not match `contoso.com`; hosts are compared in punycode, so a homoglyph host is refused). An empty or absent setting disables the button for every link. The click re-validates and launches the parsed URL. Refusals are logged to `launch.log` (`MoreInfoUrlRejected`, host and reason, never the URL). The signature on the index protects the link only where signing is required; this control also covers unsigned teams. Details: `docs/newsviewer-spec.md`.

## Authoring-tier hardening (S02, October 2026 security improvements)

The authoring tier's trust boundary used to be "anyone with write access to DataPath": the same folder held every
team's private signing key and `config/users.json` (password hashes, `TeamRoles`). S02 narrows it. Detail and the
exact ACL table: `docs/anti-tamper.md` §9. The app never sets ACLs; `scripts/Set-AuthoringAcl.ps1` does, and the
app only warns about drift.

- **Secure key store.** The environment key lives at `{SecurePath}\keys\environment-signing.json`, behind its own
  storage abstraction (not the DataPath `IStorageService`). Loading has three outcomes — `NotConfigured`,
  `Loaded`, `Unreadable` (with a reason). `Unreadable` **fails** index generation/publish; it is never treated as
  "no key", and Key Management does not offer to generate a key over it. Per-team keys, their DataPath migration and
  the login migration pass were removed in M3 (see below).
  Where the user sees an unreadable key: a failed publish shows "The environment signing key could not be read:
  <reason>. Nothing was published ..." at the top of the Assignments page (single publish) or in the bulk result (bulk
  publish); when the index regeneration that follows an assignment delete or a presentation update/delete hits it, the
  operation still completes and a banner on every page says "Distributed indexes are not being updated because the
  environment signing key could not be read." (also audited per team as `IndexRegenerationFailed`). The old index
  stays; an unsigned one is never written.
- **users.json.** Administrator-writable only after provisioning. Login's `LastLogin` write is best effort, the UI
  language lives in a per-user local store, and the password page explains that an administrator must change it.
  User management (create/update/UPN/team membership/roles/delete) requires a SystemAdmin, enforced in
  `UserService` and audited as a Failure when refused; others see the Users page read-only
  (`docs/newscentral-spec.md`).
- **Audit log.** Append-only JSON lines of logins, user/role/key/presentation/assignment/environment/audit-setting
  events (never passwords, hashes, private keys or content); `Audit:Enabled` defaults on and a registry policy value
  can force it. A failed write never blocks an action; it raises a banner.
- **Drift warnings.** Keys folder readable, `users.json` or the `config` folder writable, or the audit folder tamperable by Users /
  Everyone / Authenticated Users / INTERACTIVE — warn only.

What this does **not** do: an author who is also a Windows administrator, or a team signer for a team, still holds
that power.

## M1 - single verification path: KeyId, team binding, delivered key removed

One ECDSA key pair per environment replaces per-team keys; M1 changes **verification only**.

- `TeamIndexFile.KeyId` = `KeyFingerprint.Of(publicKey)` (first 16 lowercase hex of SHA-256 over the decoded SPKI bytes). It is inside the signed payload; authoring sets it immediately before signing.
- `SignatureGate.Verify(index, expectedTeamFolder, publicKeys)`: no key -> `Disabled`; no signature -> `Unsigned`; no `KeyId` -> `KeyIdMissing`; `KeyId` matching no configured key -> `KeyIdUnknown`; signature checked against the matching key only -> `Invalid`; `TeamFolderName` != the folder being synced (OrdinalIgnoreCase) -> `TeamMismatch`; else `Valid`. `KeyIdMissing`, `KeyIdUnknown` and `TeamMismatch` always reject, regardless of `RequireSignedIndex`; the reasons are "signed without a key id", "signed with unknown key {KeyId}", "signed for team {TeamFolderName}".
- Removed: `TeamIndexFile.SigningPublicKey`, `IDeliveredKeyCarrier`, `SignatureGate.VerifyWithPrecedence`. The delivered key let anyone who could write the repository sign with their own key and ship it; an old index that still contains `signingPublicKey` deserializes (the property is ignored) and is then rejected for lack of a `KeyId`.
- Team binding: with one key for every team, an index signed for team A would otherwise verify in team B's folder.
- Registry: `Signing\PublicKey` and `Signing\PublicKeyPrevious` are flat; `Signing\{team}\...` is no longer read.
- Authoring self-check runs `SignatureGate.Verify(reloaded, teamFolderName, [publicKey])` and requires `Valid` before anything is written.
- Key storage and the Key Management page were still per-team at this point; M3 (below) made them per-environment. Registry tooling, manifests and the `RequireSignedIndex` default change in M2.

## M2 - client configuration for the single key

- `Signing:RequireSignedIndex` now defaults to `true` everywhere: both `appsettings.json` templates, both manifests, and the code (`GetValue("Signing:RequireSignedIndex", true)`), so an absent value is fail-closed. A machine without `Signing\PublicKey` (in both `NewsService\Signing` and `NewsViewer\Signing`) shows no content.
- Index rejections still reach the log every cycle but at Debug unless it is the first rejection for the team, the reason changed, or 60 minutes passed since the last Error for that reason (`SyncService`, injected `TimeProvider`); state is cleared when the team's index is accepted. Rejections stay Error-level.
- NewsService warns once at startup about per-team `Signing\{team}\PublicKey` / `PublicKeyPrevious` subkeys, which are no longer read.
- `ConfigManifests` gained `Signing:PublicKey` and `Signing:PublicKeyPrevious` (both components); `ComponentManifest.HasSigningHive`, `SigningTeamKeys` and `ComponentResolution.Signing` were removed, and Configuration Review shows the keys as ordinary rows with the copyable full-value rendering. `FleetExportBuilder` and `Set-RegistryOverrides.ps1` still emit the old per-team layout and `RequireSignedIndex = false` until M4; the authoring key store and Key Management stayed per-team until M3.

## M3 - one environment signing key, three-step rotation, SystemAdmin-only Key Management

- **Storage.** `{SecurePath}\keys\environment-signing.json` (`EnvironmentSigningKeys`: `current`, `next`, `previousPublicKey` and their `createdAt`) replaces the per-team `team-signing.json`, `TeamKeyStore`, `TeamKeyService`, `TeamKeyMigrator` and `TeamSigningKeys`. Old per-team files are ignored; code never deletes them (`docs/disaster-recovery.md` tells the administrator to).
- **Signing.** Every index is signed with `current` only (`KeyId = KeyFingerprint.Of(current.PublicKey)`, self-check with `current.PublicKey`). A prepared `next` key never signs. No change to `SignatureGate`, the `KeyId` format, canonical JSON or the clients.
- **Rotation** (`EnvironmentKeyService`, runbook with the exact GPO values per step in `docs/anti-tamper.md` §6): Generate (first key) -> Prepare next (GPO: `PublicKey` = next, `PublicKeyPrevious` = current) -> Activate next (no GPO change: it already holds exactly these two keys; then Regenerate all teams) -> Retire previous (GPO removes `PublicKeyPrevious`). Registry values come from `EnvironmentKeyService.GpoTarget`.
- **Authorization and integrity.** Every action requires a SystemAdmin (`UnauthorizedAccessException` otherwise, enforced in the service as well as the page), re-loads the file first, refuses over an unreadable file ("not replaced"), and refuses when the current key changed since the page loaded (optimistic check on the `KeyId` the page displayed). Audited as `KeyGenerated`, `KeyPrepared`, `KeyDiscarded`, `KeyRotated`, `KeyRetired` with public fingerprints only; `KeyMigrated` was removed. Key material never reaches audit lines, logs, notices or exceptions.
- **Key Management** is SystemAdmin-only, has no team context, shows the private key nowhere (not even truncated), and shows the age of each key from `createdAt`.
- **ACL.** `Set-AuthoringAcl.ps1` lost `-TeamSigners` and the per-team folder handling; `keys\` gets AuthorsGroup Read (authors' sessions sign with the key) on top of the administrators' Full control. `AuthoringAclChecks` checks `keys\` and `keys\environment-signing.json` for read by broad identities (Users, Everyone, Authenticated Users, INTERACTIVE); AuthorsGroup read is expected and not a warning. Residual exposure: any author can read the environment private key by design (trusted authors); rotate it if the file was ever broadly readable.
- **Failure behaviour.** An unreadable key stops publishing and "Regenerate all teams" at the first team (reported once, with the teams not processed); the background regeneration raises one environment-wide notice.
- **Not changed / open.** The fleet export carries no signing keys until M4 (it no longer reads a per-team store). `Set-RegistryOverrides.ps1`, NewsService, NewsViewer and `ConfigManifests` are untouched. Azure Key Vault for the private key remains the flagged preventive measure.

## M4 - close-out: published-index status, fleet export and registry script on the flat key

This closes the per-environment key work (M0-M4): M0 user/admin guard, M1 single verification path (KeyId, team binding), M2 fail-closed clients and flat registry keys, M3 one environment key with three-step rotation and SystemAdmin-only Key Management, M4 the last per-team leftovers.

- **Published indexes.** Key Management lists each active team's authoring-tier `index.json` KeyId with a status (`Current`, `Next`, `Previous`, `Unknown key`, `Unsigned`, `No index`, `Unreadable (reason)`), via the pure `PublishedIndexStatusEvaluator`. Only `keyId` and the presence of a signature are read; no private key is read or shown. Retire warns, without blocking, while any team with an index is not on `Current`.
- **Fleet export.** `FleetExportInput` carries one environment block (`FleetExportSigning`: `PublicKey`, `PublicKeyPrevious`, `KeyUnreadableReason`) filled from `EnvironmentKeyService.GpoTarget`, so it matches Key Management. Both outputs write `RequireSignedIndex` true and the flat keys; a null previous key is marked for removal (`SigningPublicKeyPrevious = ''` / `"PublicKeyPrevious"=-`); no per-team `Signing\` subkeys. No key or an unreadable key omits the key lines, keeps `RequireSignedIndex` true and warns (fail-closed). The export never contains private key material (asserted in a test).
- **`Set-RegistryOverrides.ps1`.** `-TeamPublicKeys` / `-TeamPreviousPublicKeys` are replaced by `-SigningPublicKey` / `-SigningPublicKeyPrevious` (validated as a 91-byte P-256 SPKI before any write; `''` removes the previous value). Binding `-SigningPublicKey` removes old per-team subkeys under `Signing\`. Exit 0 on success and `-WhatIf`, 1 on error.
- **`Set-AuthoringAcl.ps1`.** The stale "rotate every team key" closing message is gone.
- **Unchanged.** Verification (`SignatureGate`, `KeyId`, canonical JSON), NewsService, NewsViewer, the `EnvironmentKeyService` actions and the key file format.
- **Residual.** The published-index view reflects the authoring tier only; whether devices have fetched the regenerated indexes remains an administrator judgement. Any author can read the environment private key by design (trusted authors; see M3).
