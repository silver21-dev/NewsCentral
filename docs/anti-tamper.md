# Anti-Tamper — ECDSA Index Signing

This is the single-page reference for the content-integrity system: the integrity values, the
configuration/registry that governs enforcement, the per-module behavior modes, and the
static-vs-dynamic (Entra) key-trust model. For the phased implementation history see
`docs/security.md`; for the full registry layout see `docs/configuration.md`; for dynamic-team
resolution see `docs/entra-dynamic-teams.md`.

## 1. Overview

Each team's `index.json` is signed with a **per-team ECDSA P-256** private key by NewsCentral at
publish time. NewsService and NewsViewer each verify that signature **independently** before
consuming the index — a rejected index aborts the team's sync (NewsService) or skips the team
(NewsViewer). The signed index also carries a **SHA-256** hash for each content image, which
NewsViewer re-checks against the cached image file before display. Session telemetry
(`session-*.json`) uses a **separate** HMAC-SHA256 mechanism and is unaffected by index signing. The
canonical (signed) form of a session record omits its optional notification fields (`recordType`,
`source`, `contentKey`) when they are null, so ordinary poster records sign exactly as before; the fields,
when present, **are** signed (stripping or adding one invalidates the record). An older NewsService
verifying a notification record or a `source`-marked poster record deserializes without those fields,
computes a different signature and discards the record as `Invalid` — ship NewsService and NewsViewer
together (`docs/packaging.md`).
An empty / unconfigured key makes verification a pass-through (`Disabled`) unless fail-closed
enforcement is turned on — see §4.

## 2. Integrity values

Three independent integrity values protect different artifacts:

| Value | Algorithm / format | Computed by | Stored in | Verified by |
|---|---|---|---|---|
| **Index signature** | ECDSA P-256, SHA-256, IEEE P1363 fixed r‖s (64-byte), Base64 | NewsCentral `IndexGenerationService` → `EcdsaSignatureService.Sign` | `index.json` top-level `signature` field | NewsService `SyncService`, NewsViewer `PresentationSelector` (each via `SignatureGate`) |
| **Image hash** | SHA-256, lower-case hex, `sha256:{hex}` prefix | NewsCentral `IndexGenerationService.BuildContentInfoAsync` | `content.imageHash` **inside** the signed index (so the hash is itself signature-covered) | NewsViewer `PresentationSelector.VerifyImageHash` at display time |
| **Session telemetry** | HMAC-SHA256, Base64 | NewsViewer `TelemetryWriter` → `HmacService.Sign` | `session-*.json` `signature` field | NewsService `TelemetryUploader` → `HmacService.Verify` |

**Index signature — canonical payload.** `EcdsaSignatureService.Canonicalize` temporarily nulls the
`Signature` field — and, for key-carrying indexes, the delivered `SigningPublicKey` (via
`IDeliveredKeyCarrier`) — then serializes with **non-indented, camelCase, enums-as-strings** JSON
(`CanonicalOptions`). Nulling `SigningPublicKey` combined with `[JsonIgnore(WhenWritingNull)]` keeps
the canonical bytes bit-identical whether or not a delivered key is present, so the signature never
covers the delivered key and every pre-key static signature still verifies. **Whole-second
timestamps** are not produced by the canonical serializer itself — before signing, the index is
round-tripped through `JsonConfiguration.GetIndexJsonOptions()` (which uses `SmartDateTimeConverter`
to truncate `DateTime` to whole seconds), so the signed payload matches the persisted-form bytes
exactly (see §6).

**Image hash.** `BuildContentInfoAsync` streams each image through SHA-256 and stores
`sha256:{hex}` in `content.imageHash`. Because that field lives inside the signed index, tampering
with either the recorded hash or the index is caught by the signature; tampering with the cached
image file alone is caught by NewsViewer's display-time re-hash. NewsService's sync loop hashes every
download in memory and writes it only when it matches `content.imageHash`, and treats a cached file
as current only when its own SHA-256 matches (§8); it does not perform a display-time check — that
is NewsViewer's.

## 3. Configuration & registry

Every anti-tamper-relevant value. Registry paths are under
`HKLM\Software\[Company]\NewsCentral\<Component>\` (see `docs/configuration.md` for the full layout;
`[Company]` is the build-time constant `SolutionConstants.Company` (authored in `Directory.Build.props`) and `NewsCentral` is `SolutionConstants.SolutionName`; neither is registry-overridable).

| Value | Registry path (per component) | Type | Default | Read by | Effect |
|---|---|---|---|---|---|
| Team public key | `…\<Component>\Signing\{teamFolderName}\PublicKey` | `REG_SZ` (Base64 SPKI) | — | NewsService, NewsViewer | Verification key for the team's `index.json`. Surfaced as `Signing:{team}:PublicKey`; read by `SigningKeyConfigurationReader.GetPublicKeys`. |
| Previous public key | `…\<Component>\Signing\{teamFolderName}\PublicKeyPrevious` | `REG_SZ` (Base64 SPKI) | — | NewsService, NewsViewer | Rotation window — verification accepts a signature made by either key (current tried first, then previous). |
| Require signed index | `…\<Component>\Signing\RequireSignedIndex` | `REG_SZ` `"true"`/`"false"` | `false` | NewsService, NewsViewer | Fail-closed switch — when `true`, `Unsigned` and `Disabled` are rejected in addition to `Invalid` (§4). |
| Bypass image integrity | `…\NewsViewer\BypassImageIntegrityCheck` | `DWORD` (`0`/`1`) | `0` (false) | NewsViewer | `1` skips the display-time image SHA-256 re-check (dev/test). |
| HMAC secret key | `…\<Component>\Hmac\SecretKey` | `REG_SZ` (Base64 32-byte) | `""` | NewsViewer (sign), NewsService (verify) | Telemetry HMAC key. Empty = HMAC disabled (telemetry treated as `Disabled` and passed through). Not used for index signing. |

**Separate `Signing\` subtrees.** NewsService and NewsViewer each read their **own**
`…\NewsService\Signing\…` and `…\NewsViewer\Signing\…` subtrees — the public keys and
`RequireSignedIndex` are configured per component and do not cross over. Deploy a team's public key
(and the flag) to **both** components if both must verify that team.

**Authoring-side key material.** The **private** key (and the current/previous public keys) lives in
`{teamFolderName}/team-signing.json` (`TeamSigningKeys`) on the **authoring tier only** — never
written through `IBlobDistributionService`, never synced to client machines. NewsCentral loads it via
`IndexGenerationService.LoadTeamSigningKeysAsync` to sign; the Key Management page manages it (see
`docs/newscentral-spec.md`). The public key is what gets distributed — to the registry (static teams)
or with the content as `SigningPublicKey` (dynamic teams, §5).

## 4. Enforcement behavior

**`SignatureGate.ShouldReject(result, requireSignedIndex, out reason)`** — the accept/reject matrix,
identical across both verifiers:

| `VerifyResult` | `RequireSignedIndex = false` (default) | `RequireSignedIndex = true` |
|---|---|---|
| `Valid` | accept | accept |
| `Invalid` | **reject** | **reject** |
| `Unsigned` | accept (warning) | **reject** |
| `Disabled` | accept | **reject** |

`Disabled` means no verification key was available at all (no registry key, and — for a static team —
no delivered key is ever consulted). Default `false` preserves phased-rollout pass-through: only a
present-but-wrong signature (`Invalid`) is rejected.

**Per-module reject action:**

- **NewsService `SyncService.SyncTeamAsync`** — on reject, logs `Error` with the gate reason and
  **returns for that team** (aborts its sync; no images downloaded, cached index left as-is). Accept
  paths: `Unsigned` → `Warning`, everything else → `Information`.
- **NewsViewer `PresentationSelector.EnumerateVerifiedAssignments`** — on reject, logs the reason and
  **`continue`s past the team** (its assignments are never yielded, so nothing from it is shown as
  the poster). Accepted teams' assignments are yielded normally. (NewsViewer selects only the
  poster now — lock-screen and wallpaper selection moved to NewsService's `SyncService`, which
  routes through the same `SignatureGate` verification independently.)

**NewsCentral signer modes** (`IndexGenerationService.GenerateIndexForTeamAsync`):

- **Sign** — `team-signing.json` has a `PrivateKey`: the index is signed and the team's current
  `PublicKey` is embedded as `SigningPublicKey` (key-with-content, §5).
- **Unsigned-with-warning** — no signing key: `Signature = null`, published unsigned with a warning
  (phased-rollout safe; consumers treat it as `Unsigned`).
- **Self-verify guard aborts publish** — after signing, the index is serialized with the persisted
  options and re-deserialized with the client-verifier options (case-insensitive, enum converter, **no**
  `SmartDateTimeConverter`), then re-verified against the team's own public key. Any result other than
  `Valid` throws `InvalidOperationException` and **aborts the publish before any file is written or
  pushed** — catching canonical/persisted-form drift at the authoring tier instead of shipping an index
  every client would reject.

## 5. Static vs dynamic (Entra) key-trust model

A team's verification key is chosen by **precedence** in
`SignatureGate.VerifyWithPrecedence(index, registryPublicKeys, isDynamicTeam)`:

| Condition | Key used | Result |
|---|---|---|
| Registry public key configured (either current or previous non-empty) | Registry key(s), dual-key rotation | **Registry always wins** — delivered key never consulted |
| No registry key, **dynamic** team, `SigningPublicKey` present | Delivered key | verified against the delivered key |
| No registry key, **dynamic** team, `SigningPublicKey` absent | — | `Unsigned` |
| No registry key, **static** team | — | `Disabled` (delivered key never consulted) |

Callers do `VerifyWithPrecedence(...)` → `ShouldReject(...)`; the §4 matrix then applies uniformly to
whatever result precedence produced (so `RequireSignedIndex = true` rejects a `Disabled` static team
with no registry key and a dynamic team carrying no delivered key alike).

**Why dynamic teams use key-with-content.** Entra-resolved teams (attribute- or group-derived; see
`docs/entra-dynamic-teams.md`) appear at runtime and cannot be pre-provisioned with a registry public
key. Instead NewsCentral emits the team's public key **with the content** in
`TeamIndexFile.SigningPublicKey` (Base64 SPKI, the same form as the registry key) for **all** teams —
a static consumer ignores it via precedence, a dynamic consumer verifies against it. Whether a team is
static or dynamic is a property of the **consuming machine** (does `resolved-teams.json` list it?),
not of the team, which is why every index carries the key.

**Weaker anchoring, and its mitigation.** A delivered key is only as trustworthy as the distribution
channel that carried it — authenticity for dynamic teams rests on **distribution-tier RBAC** (only
authorized publishers can write a team's `index.json` + `SigningPublicKey`). The model is **not**
protective against an attacker who can write the distribution channel or holds local admin on the
consuming machine. Anti-downgrade guarantees limit the blast radius: (1) the delivered key is **never**
used for a static team, so a key cannot be attached to downgrade a statically-trusted team; (2) a
present registry key **always** overrides the delivered key. To harden a dynamic team, **deploy a
registry `PublicKey` for it** — precedence makes the registry key win, pinning trust exactly as for a
static team.

**`resolved-teams.json`.** The list of dynamic teams NewsService resolves each cycle carries **no
signatures** — it is a local-tier file trusted via **`%programdata%` cache ACLs**, holding only team
identity + grace state. NewsViewer reads only `TeamFolderName` from it and unions those with its
registry static team list (`EffectiveTeams.Union`); it never writes the file. See
`docs/data-model.md` (ResolvedTeams) and `docs/entra-dynamic-teams.md`.

## 6. Operational notes

**Dual-key rotation sequence** (static teams — dynamic teams are rotation-free because the key travels
with the content):

1. Generate a new key pair (Key Management page or `SigningKeyTool.GenerateKeyPair`). Applying it sets
   `PublicKeyPrevious = old PublicKey` in `team-signing.json`.
2. Deploy the **new** `PublicKey` to each verifier's registry, keeping the **old** key as
   `PublicKeyPrevious` — verification then accepts a signature from either key during the window.
3. Wait for registry propagation to reach all consuming machines.
4. Republish the index (now signed with the new key) — a deliberate action on the Key Management page.
5. Once every consumer has the new key and the republished index, clear `PublicKeyPrevious`.

**Persisted-form signing.** `index.json` is serialized with `JsonConfiguration.GetIndexJsonOptions()`
(`SmartDateTimeConverter`, which truncates timestamps to whole seconds). The index is normalized
through those options **before** signing so the signed bytes equal the on-disk bytes. Verifiers parse
with plain ISO (`JsonDefaults.Options`, no `SmartDateTimeConverter`), which faithfully round-trips
whole-second timestamps. Changing the serializer options on either side without re-signing makes every
verifier return `Invalid` — the publish-time self-verify guard (§4) is the tripwire for exactly this
drift.

**Change detection vs. signature.** `IndexHash` (SHA-256 over the index's content fields) is what
drives per-cycle change detection — an unchanged `IndexHash` short-circuits the sync: no download and no index rewrite (only a local check that the cached images are still present, which re-fetches any that are missing). The
ECDSA **signature** governs trust, not change detection; the two are independent.

**Cache ACLs.** The `%programdata%\NewsCentral\` cache — the `index.json` copies, the cached images,
and `resolved-teams.json` — is protected by filesystem ACLs. That protection is the trust anchor for
the local consumption leg (especially the unsigned `resolved-teams.json` and, for dynamic teams, the
delivered-key path); it is a deployment prerequisite. A future anchored-root upgrade (Option B in
`docs/future.md`) would harden the delivered-key leg beyond ACLs. The lock-screen leg specifically no
longer depends solely on this cache ACL for its final apply — see §7.

## 7. Display-surface publish-folder hardening (NewsService)

The image hash inside the signed index (§2) protects `index.json` and the recorded hash from
tampering, and NewsService's download-time comparison against the sidecar hash catches a changed
image on the **next** sync. Neither of those, by itself, protected the **apply** step: until this
hardening, `SyncService` pointed `PersonalizationCSP\LockScreenImagePath` straight at the cached
image under `%programdata%\NewsCentral\`, trusting two things that were never independently true:

1. **The bytes on disk at apply time still matched the signed hash.** The hash was checked once,
   at download, and never rechecked when the path was actually handed to the OS moments (or days)
   later.
2. **The cache folder itself was safe to point a machine-wide surface at.**
   `%programdata%\NewsCentral\` inherits `C:\ProgramData`'s ACL, under which the built-in `Users`
   group holds create-file/create-folder rights that inherit downward, and `CREATOR OWNER` gets
   full control of anything it creates. A standard user can pre-create a team's
   `images\generated\` folder before NewsService reaches it and, as owner, retain delete-child
   rights over everything later written into it — on a folder a system-enforced surface (lock
   screen, and now wallpaper) was being pointed at directly.

`ImagePublisher` (`NewsService/Services/ImagePublisher.cs`) closes both gaps for **both** display
surfaces NewsService manages — the lock screen and, since the wallpaper-ownership migration from
NewsViewer, the desktop wallpaper — each in its own content-derived namespace (`lockscreen-*` /
`wallpaper-*`) within the same protected folder. Before `SetLockScreen`/`SetWallpaper` is ever
called, the winning image for that surface (or its configured default) is re-hashed and compared
against its expected SHA-256 — the index-recorded hash for content, a self-hash for the
admin-supplied default — and only a match is copied into
`Delivery:PublishedImagePath` (default `C:\Windows\Web\NewsCentral`, under `C:\Windows`'s ACL,
which does not grant standard users write access). The published file name is content-derived
(`{prefix}-{hash16}.ext`, `prefix` = `"lockscreen"` or `"wallpaper"`), so CSP is pointed at a file
whose name is only ever correct — no window exists where the path exists but the bytes don't yet
match it. A startup check and a per-publish check (`ImagePublisher.IsPublishFolderUserWritable`) refuse to publish
if the configured folder is found to be writable by `Users`, `Everyone`, `Authenticated Users` or `INTERACTIVE`, since a
user-writable publish folder would defeat the entire mechanism for **either** surface. See
`docs/configuration.md` for `Delivery:PublishedImagePath` and `docs/newsservice-spec.md` for the
full publish-then-apply flow.

**No residual gap — both surfaces are now hardened.** Wallpaper was initially deprioritized in the
lock-screen-only milestone that introduced this section (wallpaper was, at the time, a per-user,
post-authentication surface applied by NewsViewer directly from the `%programdata%` cache, with
materially lower blast radius than the lock screen). That gap closed when wallpaper-**image**
ownership migrated from NewsViewer to NewsService: the wallpaper image is now a machine-wide,
system-enforced surface applied through the identical publish-verify-dispatch path as the lock
screen. NewsViewer's remaining wallpaper code is style-only (HKCU, per-user, no image, no CSP
interaction) and carries none of the exposure this section addresses — see
`docs/newsviewer-spec.md`.

> **Prerequisite — Windows Enterprise (verified).** `PersonalizationCSP`, the mechanism both
> surfaces' applies rely on, is documented by Microsoft as supported on Windows Enterprise and
> Education SKUs, and on Pro only under Shared PC / Cloud Config (BootToCloud) configurations.
> **Verified on Windows 11 Enterprise:** `Delivery:PublishedImagePath` inherits `C:\Windows`'s ACL
> as intended (`Users` get read/execute only — no create-folder or append-data ACE, unlike
> `%ProgramData%`), the publish-then-apply path works end to end, and `ImagePublisher.SweepExcept`
> correctly leaves exactly one published file behind after a content change. The raw registry
> writes are widely observed to work on Pro outside the documented configurations too, but that
> remains undocumented behavior — do not rely on it for a production fleet running Pro.

### Teardown — clearing a stale value, per surface

Publishing to a protected folder (above) closes the integrity and ACL gaps at **apply** time, but
on its own does nothing about a surface that stays applied after the content behind it expires.
Before this addition, `intended == null` (no active content, no usable default) always left
whatever was currently applied untouched — described in this document, `docs/configuration.md`,
and `docs/newsservice-spec.md` as "sticky." That was appropriate for a machine mid-cycle with a
transient gap, but wrong as a permanent state: a machine whose content expired and was never
replaced would display stale, no-longer-approved content indefinitely.

`SyncService` dispatches three ways instead of two, identically for **both** surfaces (`IPersonalizationService.ClearLockScreen` / `ClearWallpaper`). Using the lock screen as the illustrative
example — wallpaper mirrors it exactly, value for value: when there is no active content and no
usable default,

- If the live `PersonalizationCSP\LockScreenImagePath` value resolves to a path **inside**
  `Delivery:PublishedImagePath` — i.e. a value **NewsService itself published** — it is **cleared**
  (`ClearLockScreen`, which deletes the three CSP values but leaves the `PersonalizationCSP` key
  itself in place, since the other surface's values live there too). Windows returns to its own
  default lock screen at the next lock boundary.
- If the live value resolves **outside** `Delivery:PublishedImagePath` — a value NewsService did
  **not** write — it is left alone. **NewsCentral never clears a CSP value it did not write.** This
  is the load-bearing safety property of the whole feature: without it, a machine whose lock screen
  (or wallpaper) is managed by GPO, Intune, or set manually by an admin would have that value
  silently deleted the first time NewsService itself had nothing to show.

**The ownership prefix test.** Distinguishing "ours" from "someone else's" is a fully-normalized
absolute-path comparison (`Path.GetFullPath` on both the configured root and the live value,
`OrdinalIgnoreCase`, directory-prefix — never a raw string `StartsWith` on the configured value),
so a trailing separator, a `..` segment, or different casing on either side cannot produce a false
match in either direction. Shared by both surfaces, since they publish into the same protected
folder. An empty or unresolvable `Delivery:PublishedImagePath` makes the test fail **closed**:
every live value, for either surface, is then treated as foreign, and nothing is ever cleared —
the safe failure mode for a mechanism whose entire job is deciding what is safe to delete.

Either surface can be disabled outright — `Delivery:LockScreenEnabled` for the lock screen,
`Delivery:WallpaperEnabled` for the wallpaper, both default `true` and fully symmetric — see
`docs/configuration.md` and `docs/newsservice-spec.md` for the opt-out this provides for RDS
session hosts, VDI templates, and RemoteApp hosts, and for why disabling either is **not** a
revert.

**The same ownership test governs uninstall.** Removing NewsCentral must clear these values too, or
the machine is left with a permanently enforced lock screen and wallpaper that no remaining software
can ever clear — the uninstaller applies the identical fully-normalized-path ownership test before
clearing anything. See `docs/packaging.md` → "NewsService — display-surface cleanup" and
`scripts/Remove-DisplaySurfaces.ps1`.

**Verified: clearing releases the lock screen (Windows 11 Enterprise).** Manually deleting the
three `PersonalizationCSP` lock-screen values and then locking the machine was tested directly:
Windows returns to its default lock screen at the lock boundary, with **no logoff and no Explorer
restart required**. Clearing works from session 0 (NewsService's own context) alone. The wallpaper
trio (`DesktopImage*`) uses the identical CSP write/clear mechanism and is expected to behave the
same way; it was not separately re-verified on hardware, since the code path is shared.

**A caveat on a widely-cited failure report.** A commonly-referenced FileWave deployment script log
shows an attempt to clear `LockScreenImageUrl` failing with an "unsupported" error. That failure
was reported against the **WMI MDM Bridge** (`MDM_PersonalizationCSPUrl` / the `Win32_...` MDM
provider classes), **not** direct registry deletion. `ClearLockScreen`/`ClearWallpaper` here delete
the registry values directly via `Microsoft.Win32.Registry`, exactly as `SetLockScreen`/
`SetWallpaper` write them — the same mechanism, in reverse. Do not read that report as evidence
against this implementation; it is evidence against a different delivery mechanism (WMI Bridge /
MDM) that this codebase does not use. This note exists so nobody later "fixes" working, verified
code on the strength of an unrelated report.

## 8. Image path and image hash hardening (NewsService)

NewsService runs as LocalSystem and builds cache, file-share and blob paths from the `ImagePath` of a
downloaded `index.json`. Signing alone does not protect that (`RequireSignedIndex` defaults to
`false`), so the path and the image bytes are validated independently of the signature.

**Path rule** (`ImagePathValidator` in `NewsCentral.Shared`; non-throwing, returns a reason for the log).
The only accepted shape is exactly what `PresentationService` emits:

    {team}/images/{original|generated}/{name}.png|.jpg

- `/` is the only separator; any `\` is rejected. Exactly four segments.
- `{team}` equals (OrdinalIgnoreCase) the team folder of the index being processed; `images`, the
  subfolder and the extension (`.png` / `.jpg`) are matched OrdinalIgnoreCase. `a.png.exe` is rejected.
- Rejected: empty / whitespace; rooted, UNC and device paths; `.` or `..` or empty segments; `:` anywhere
  (drive letters, alternate data streams); `Path.GetInvalidFileNameChars()`; segments ending in a dot or
  space; an empty file stem; reserved device names (`CON`, `NUL`, `COM1`, …). An empty `ImagePath` is
  rejected too (every assignment has an image).

**Containment** (after the shape check): `Path.GetFullPath` of `{root}\{team}\` + the relative part must
start with `{root}\{team}\` (OrdinalIgnoreCase), for the cache (`CacheManager.TryValidateImagePath`) and for
the file-share source (`LocalShareRepositoryReader`). For Azure Blob the name handed to the blob client is
the validated `ImagePath` itself and must start with `{team}/` (`AzureBlobRepositoryReader`).

**Hash rule.** `ImageHash` is `sha256:{hex}` (`IndexGenerationService`); hex case is ignored and the bare
hex form is accepted. Each image is downloaded fully into memory, hashed, and written to the cache only
on a match (`ImageHashVerifier`). A cached file counts as up to date only if its own SHA-256 matches —
the `.hash` sidecar is still written but is no longer trusted.

**Rejection behaviour**

| Condition | Result |
|---|---|
| Any entry fails the path or containment check | The **whole index** is rejected, before anything is read or written — also when `IndexHash` is unchanged. Error naming the assignment and the reason. Cached index, images and display surfaces are untouched. |
| `ImageHash` missing, empty or malformed | That image is rejected (Error) and never downloaded; its previous cached file is kept. Other images still sync. |
| Downloaded bytes do not match `ImageHash` | That image is rejected (Error); nothing is written; the previous file is kept. |

A rejected image counts as a missing image: the new `index.json` is **not** written (index last,
`docs/newsservice-spec.md`), so the previous index stays in effect and the sync is retried next cycle.
The Error is logged once per failing `IndexHash` and drops to Debug on repeats. Cache self-heal (unchanged
index) validates the cached index's paths before fetching anything.

**Not covered.** Display-surface selection reads the already-cached index; images it publishes are
re-hashed against `ImageHash` by `ImagePublisher` (§7). NewsViewer builds the cached image path from
`ImagePath` at `PresentationSelector.cs:54` and still verifies the hash at display time; it was
deliberately left unchanged.

**Self-heal hashing (S01b).** On every cycle the cached index's images are re-hashed (unchanged-index
path, and also when the source is unreachable). A cached file whose SHA-256 differs from `ImageHash` —
replaced in place — is logged at Error naming team, assignment, path and expected vs actual hash (once per
file and tampered content; repeats at Debug), deleted together with its `.hash` sidecar, and re-downloaded
through the S01a verified path. If the re-download fails verification or the source is unreachable the slot
stays empty and is retried next cycle; an absent file is restored the same way. A heal raises no
`ContentChanged` event.

**Offline re-validation (S01b).** Display-surface selection reads the cached index of every team without a
fresh download when the source is unreachable (or a downloaded index was rejected). That cached index is
re-validated with the same whole-index `ImagePath` rules as a download, because a cache written before
S01a was never checked. If any team's cached index fails, neither lock screen nor wallpaper is touched:
no publish, no sweep, no write, no clear — the previously published state stays. Error naming team and
entry once per distinct failure, then Debug.

**NewsViewer display-time checks (S01b).** `PresentationSelector` runs `CachedImageCheck` on every
candidate: `ImagePath` must pass `ImagePathValidator` (team = the folder of the index it came from) and
resolve inside `<cacheRoot>\<team>\`; `ImageHash` must be present and equal the file's SHA-256. The former
"no hash in index — skipping integrity check" fail-open is removed. A candidate failing any check is
ineligible and the selector falls through to the next newest; with none eligible NewsViewer behaves as with
no content. The "News arrived!" notification takes its item from the same selection, so it never announces
what the poster would refuse. Each rejection is a Warning line in `%LOCALAPPDATA%\NewsCentral\launch.log`
(once per presentation per run; NewsService owns the Error-level signal). `BypassImageIntegrityCheck`
(dev) skips only the hash comparison. The delivered-image buttons read `PublishedImagePath`, not the cache,
and are unchanged.
