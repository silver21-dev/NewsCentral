# Production Deployment Preparation

> **Scope.** Everything that must exist in Azure/Entra, and everything that must be written into the
> registry by GPO, before the fleet components are deployed to a corporate environment.
>
> **Out of scope.** MSI/Intune packaging and installer authoring (`docs/packaging.md`), the OS image
> baseline, and .NET runtime distribution.
>
> **Relationship to other docs.** This document is the *ordered sequence*; it consolidates
> `docs/azure-setup.md` (the single source for Azure entities) and `docs/configuration.md` (the single
> source for the registry surface). Where the two disagree with this document, they win — update this
> one.

---

## Table of contents

1. [Step 1 — Decisions to record first](#step-1--decisions-to-record-first)
2. [Step 2 — Azure identities](#step-2--azure-identities)
3. [Step 3 — Storage account and RBAC](#step-3--storage-account-and-rbac)
4. [Step 4 — Certificate](#step-4--certificate)
5. [Step 5 — Entra device data](#step-5--entra-device-data)
6. [Step 6 — Environment signing key](#step-6--environment-signing-key)
7. [Step 7 — Registry configuration to push](#step-7--registry-configuration-to-push)
8. [Step 8 — Validation and cutover](#step-8--validation-and-cutover)
9. [Upgrading an existing installation](#upgrading-an-existing-installation)
10. [Preparation checklist](#preparation-checklist)
11. [Appendix — Reference commands](#appendix--reference-commands)

---

## Step overview

| Step | Name | Outcome |
|---|---|---|
| 1 | Decide the deployment shape | Storage mode, auth mode, signing posture, dynamic teams — recorded before anything is created |
| 2 | Azure identities | Two app registrations created, credential attached, Graph permissions consented |
| 3 | Storage | Account + container created, RBAC assigned to a group and a service principal |
| 4 | Certificate | Machine certificate issued, deployed to `LocalMachine\My`, public key uploaded |
| 5 | Entra device data | *(dynamic teams only)* extensionAttributes stamped and/or groups created and populated |
| 6 | Signing keys | Per-team ECDSA key pairs generated; public keys extracted for GPO |
| 7 | Registry configuration | Two GPO-delivered registry trees authored and pushed — NewsService **and** NewsViewer |
| 8 | Validation | End-to-end proof on a pilot machine, then fail-closed switches flipped |

Steps 2-6 can run in parallel with packaging work. Step 7 depends on Step 6 (the public keys) and on the
`Company` value being final. Step 8 depends on everything.

---

## Step 1 — Decisions to record first

Each of these changes what gets created later. Settle them before touching Azure.

| Decision | Options | Consequence |
|---|---|---|
| Distribution tier | Azure Blob / file share | File share needs no storage account. **But if `Entra:Enabled=true`, the `AzureBlob:*` credential keys are required even in file-share mode** — the Graph reads reuse the blob credential. |
| NewsService auth mode | `Certificate` / `ClientSecret` / `ClientSecretEnv` | `Certificate` is the production answer (Step 4). The other two place a secret in clear text (registry, or a machine environment variable). |
| Signing posture | `RequireSignedIndex` defaults to `true` | Fail-closed from the start: a machine without the environment's `Signing\PublicKey` shows no content, and the misconfiguration is visible as an Error in the NewsService log. Deliver the key before or with the install (see the note under Step 7). |
| Dynamic teams | `Entra:Enabled` `false` / `true` | `false` = static registry team list only. `true` adds Step 5, Graph admin consent, and the pending live-validation risk below. |
| `Company` value | One string, fixed at build | Defines both the registry hive and the scheduled-task folder. Must be identical in `Directory.Build.props` and every GPO path. |
| Team folder naming | Sanitized team name | Registry `teams\` value names must match the generated folder name exactly — e.g. `cz-its`, not `CZ_ITS`, and **no `team-` prefix**. |

> ⚠️ **Entra dynamic-team resolution has not been validated live.** It is structurally complete and
> offline/fake-tested, shipping behind `Entra:Enabled=false`. The unproven seams are the DeviceId read,
> the device fetch HTTP, and `EntraGroupClient`'s name→id + `checkMemberGroups` calls. Plan a validation
> window on an Entra-joined machine before relying on it, and keep the flag off until it passes.
> See `docs/entra-dynamic-teams.md`.

---

## Step 2 — Azure identities

Two separate identities. They *may* be combined into one app registration configured as both public and
confidential client; keeping them separate is cleaner and is assumed here. Full detail:
`docs/azure-setup.md` §1–§2.

### 2.1 Agent — NewsService (app-only / confidential client)

The unattended identity for blob reads and, when enabled, the Graph device and group reads.

| Setting | Value | Maps to |
|---|---|---|
| Supported account types | Single tenant | — |
| Platform / redirect URI | None — this is a daemon | — |
| Application (client) ID | Overview blade | `AzureBlob:ClientId` |
| Directory (tenant) ID | Overview blade | `AzureBlob:TenantId` |
| Credential | Certificate (preferred) or client secret | `AzureBlob:CertificateThumbprint` / `AzureBlob:ClientSecret` |

**Microsoft Graph permissions — Application type, admin consent required:**

| Permission | Required when | Notes |
|---|---|---|
| `Device.Read.All` | any Entra dynamic team | Reads the device object and its extensionAttributes. Omit entirely if `Entra:Enabled=false`. |
| `GroupMember.Read.All` | at least one group-team instance | Resolves group display names and checks transitive membership. |
| `Directory.Read.All` | fallback only | Broader; use only if `GroupMember.Read.All` proves insufficient at runtime (surfaces as a `403`). |
| *(none for Storage)* | — | App-only blob access is governed by RBAC, not an API permission. |

> ⚠️ **Missing admin consent surfaces as a `403`, and `403` is classified as persistent.** NewsService
> performs a clean team removal and logs an `Error` — it does not ride the grace window. That is
> deliberate so the cause is visible, but it means a forgotten consent presents as *teams disappearing*,
> not as a stall.

### 2.2 Authoring — NewsCentral (interactive / public client)

Authentication is delegated to the signed-in content author. There is no shared service account for
publishing.

| Setting | Value |
|---|---|
| Supported account types | Single tenant |
| Platform | Mobile and desktop applications |
| Redirect URI | `http://localhost` |
| Allow public client flows | **Yes** — required for `InteractiveBrowserCredential` |
| Client secret / certificate | None |
| API permissions | Azure Storage → `user_impersonation` (Delegated) |

The MSAL token is persisted in a named cache (`"NewsCentral"`), so authors see a browser prompt on first
use only. Subsequent launches refresh silently unless Conditional Access requires step-up or the refresh
token lapses.

**First sign-in — no default credentials.** NewsCentral has no seeded admin account of any kind. A
brand-new environment (no `config/users.json` yet) is initialized through the **setup wizard** —
from the login page for a Policy or Configured environment, or from Environment Management's
"Initialize an environment" section while signed in to another environment — where the operator
running the wizard chooses the first System Administrator's own username and password.

---

## Step 3 — Storage account and RBAC

| Item | Value / setting | Maps to |
|---|---|---|
| Storage account name | **Without** the `.blob.core.windows.net` suffix | NewsService `AzureBlob:AccountName`, NewsCentral `AzureBlob:AccountName` |
| Container name | Default `newscentral`; auto-created by NewsCentral on first publish | NewsService `AzureBlob:ContainerName`, NewsCentral `Storage:AzureBlobContainerName` |
| Container access level | Private — no anonymous access | — |
| Shared key access | Disabled — RBAC only | — |
| Networking | Permit the agent machines / corporate proxy egress if the account uses a firewall or private endpoint | — |

### 3.1 Role assignments

| Principal | Role | Reason |
|---|---|---|
| Content authors — assign to an Entra security **group**, not individuals | **Storage Blob Data Contributor** | Publish content. Group assignment lets IT manage access through normal membership changes. |
| NewsService agent service principal | **Storage Blob Data Reader** | Sync content to the local cache. Least privilege — the agent never writes content. |

> If the agent also uploads session telemetry to the same account (`Telemetry:UploadEnabled=true` with
> `StorageMode=Azure`), it needs **Storage Blob Data Contributor** instead. This is the only reason to
> widen the agent's role — decide it deliberately.

### 3.2 Proxy under Local System

NewsService runs as Local System. The default .NET `HttpClient` resolves its proxy via **WinINet**
(per-user), which is unreliable when no user profile is loaded — whereas the Intune client and Windows
Update reach the cloud via the **machine WinHTTP proxy**. If the fleet is behind a proxy:

```
AzureBlob\UseWinHttpProxy   REG_SZ   "true"
```

This routes **both** blob and Graph traffic through the machine WinHTTP proxy. Default `false` = no
behaviour change.

---

## Step 4 — Certificate

Required only when `AzureBlob:AuthMode=Certificate`. See `docs/azure-setup.md` §6.

1. Generate the certificate — internal CA template, or self-signed for a pilot. RSA 2048 or better;
   key usage Digital Signature.
2. Upload the **public** `.cer` to the agent app registration → Certificates & secrets.
3. Deploy the certificate **with its private key** to each agent machine's `Cert:\LocalMachine\My`, via
   GPO auto-enrolment or an Intune SCEP profile.
4. Record the thumbprint → `AzureBlob:CertificateThumbprint`.

Local System has read access to `LocalMachine\My` by default — no additional private-key permission
grants are required.

### 4.1 Shared vs per-machine certificate

| Option | Trade-off |
|---|---|
| **Shared** — one certificate on every agent machine, one public key on the app registration | Simple to manage and rotate. Compromise of any machine compromises the fleet credential. |
| **Per-machine** — unique certificate each; Entra supports multiple certificates per app registration | Stronger isolation and per-device auditability. Needs provisioning automation and a larger key inventory. |

Shared is the reasonable starting point for an agent holding a read-only role. Escalate if the security
team requires per-device auditability.

---

## Step 5 — Entra device data

Skip entirely if `Entra:Enabled` will be `false`. Both source kinds may be used together. Full model:
`docs/entra-dynamic-teams.md`.

### 5.1 Attribute schemes

Stamp the relevant extensionAttributes on the device objects. `Selector` names the attribute that drives
the lookup; `Mappings` translates a selector value into a team rule.

```
Entra\AttributeSchemes\{scheme}\Selector           REG_SZ  "extensionAttribute1"
Entra\AttributeSchemes\{scheme}\Mappings\{value}   REG_SZ  "extensionAttribute2-extensionAttribute5-extensionAttribute4"
```

An attribute entry's `SourceId` is the **authored scheme name** (e.g. `fat`), independent of the
resulting team folder name.

### 5.2 Group teams

For each instance, create the inclusion group and optionally a per-instance exclusion group. Optionally
create one fleet-wide exclusion group shared across every instance. Add target devices as members —
membership is evaluated **transitively**, so nested groups count.

| Registry value | Meaning |
|---|---|
| `Entra\GroupTeams\Instances\{label}\InclusionGroup` | Inclusion group display name — setting this **activates** the instance |
| `Entra\GroupTeams\Instances\{label}\ExclusionGroup` | Per-instance exclusion group display name (optional) |
| `Entra\GroupTeams\ExclusionGroup` | Fleet-wide exclusion group display name (optional) |

> ⚠️ **The team folder name is derived, not authored.** It is `Canonicalize(InclusionGroup)` —
> lower-case, space/underscore → `-`, strip anything outside `[a-z0-9-]`. `NewsCentral Prague ITS`
> becomes `newscentral-prague-its`, and content **must** be published under that exact folder name. The
> registry subkey `{label}` is an operator-facing label only; renaming it retargets nothing.

> ⚠️ **Inclusion group display names must be unique in the tenant.** An ambiguous name produces no team
> for that instance and is logged. An unresolvable **fleet-wide** exclusion name suppresses *every* group
> instance — it fails closed by design, logging one `Error` per cycle.

### 5.3 Caps and grace

| Value | Default | Effect |
|---|---|---|
| `Entra\Enabled` | `false` | Gates the whole feature |
| `Entra\GracePeriodMinutes` | `240` | How long a team is retained while Graph is *transiently* unreachable. Per instance, against its own last-confirmed time. `0` = no grace (deliberate and valid). |
| `Entra\MaxDynamicTeams` | `16` | Cap on total dynamic teams written across both sources; `0` = no cap. Truncates deterministically **after** the grace merge, with a `Warning` naming the dropped ids. |

> ⚠️ **All three MUST be provisioned as `REG_SZ`, never `REG_DWORD`.** `RegistryConfigurationProvider`
> coerces DWORD `0`→`"False"` and `1`→`"True"`; the int binder then throws and crashes the service at
> startup. `GracePeriodMinutes=0` and `MaxDynamicTeams=0` are both legitimate values, which is exactly
> what makes this trap live.

---

## Step 6 — Environment signing key

The environment owns **one** ECDSA P-256 key pair, shared by all teams. The private key lives only in the authoring
tier (`{SecurePath}\keys\environment-signing.json`); the public key is what gets deployed. Only a system
administrator manages it. See `docs/security.md` and `docs/anti-tamper.md` (§6 has the full rotation runbook).

**The first key is generated in Key Management, and its values feed the GPO:**

1. In NewsCentral, sign in as a **SystemAdmin** and open **Key Management**.
2. **Generate key.** The page shows the key as Current and the **Registry values to deploy (GPO)**.
3. Copy the **full public key** from the page (rendered copyable for this purpose) into `Signing\PublicKey` for
   **both** `NewsService` and `NewsViewer` (the same value in both; the Step 4/5 GPO or
   `scripts/Set-RegistryOverrides.ps1` at pilot scope). Leave `PublicKeyPrevious` unset.
4. Index Management → **Regenerate all teams** to sign every team's index with the key.
5. Back up `environment-signing.json` (`docs/disaster-recovery.md`).

The file never leaves the authoring tier — it is written to the secure store (not the DataPath `IStorageService`) and
is never distributed via `IBlobDistributionService`. Public keys are **not secret**; treat them as configuration.
Later rotation is three steps (Prepare next key → Activate next key → Retire previous key), each with exact GPO
values shown on the page; see `docs/anti-tamper.md` §6. The fleet export carries no signing keys until M4: copy the
values from Key Management.

> ⚠️ **Rotation trap.** Signing and index regeneration are distinct operations. Regenerating indexes
> re-signs with the current key but does **not** rotate keys — and because client change detection uses
> `IndexHash` rather than the signature, regenerating with a new key on *unchanged* content does **not**
> force clients to re-sync. `PublicKeyPrevious` must remain in the registry until an actual content
> change has propagated to every machine.

> ⚠️ **The private keys live in `environment-signing.json` in the authoring tier's secure store (see 6.1).** Azure Key
> Vault migration is the flagged highest-priority preventive measure. Until then, the authoring tier's
> backup and access control *is* the key protection.

### 6.1 Lock down the authoring tier (`Set-AuthoringAcl.ps1`)

DataPath is shared by every author, so by itself it protects nothing from them. NewsCentral never sets ACLs; the
environment administrator does, once, with `scripts/Set-AuthoringAcl.ps1` (elevated, 64-bit PowerShell,
`-WhatIf` supported, exit `0`/`1`). Order matters:

1. **Create the groups**: administrators and authors (for example `NC-Admins`, `NC-Authors`). Make sure the
   administrators are in the administrators group and in `NC-Authors` only if they author too. There are no
   per-team signer groups: one environment key is read by all authors and changed only by administrators.
2. **Preview, then apply**:
   ```powershell
   .\scripts\Set-AuthoringAcl.ps1 -DataPath <DataPath> -AdminsGroup <DOMAIN\NC-Admins> -AuthorsGroup <DOMAIN\NC-Authors> -WhatIf
   .\scripts\Set-AuthoringAcl.ps1 -DataPath <DataPath> -AdminsGroup <DOMAIN\NC-Admins> -AuthorsGroup <DOMAIN\NC-Authors>
   ```
   Add `-SecurePath` / `-AuditPath` if `environment.json` sets non-default locations. The script prints the resulting
   ACLs (`icacls`). `SecurePath\keys` gets AuthorsGroup **Read** (authors' sessions sign with the key) on top of the
   administrators' Full control; existing `keys\<team>` folders are left untouched. Administrators and authors must
   log off and on again for new group membership to apply.
3. **Delete the old per-team key files yourself.** Code never deletes under `keys\`: remove any
   `{SecurePath}\keys\<team>\` folders and any `{DataPath}\<team>\team-signing.json` left from before the
   environment key (see `docs/disaster-recovery.md`).
4. **Rotate the key if `environment-signing.json` may have been readable by other users before step 2** (Key
   Management → Prepare next key → deploy → Activate → Regenerate all teams → Retire previous; see
   `docs/anti-tamper.md` §6).
5. Confirm the **folder-permission warnings** on Key Management and Environment Management are gone. They are
   warnings only; the audit-folder check can over-report on a folder that was not provisioned by the script.

Also deliver `HKLM\Software\<Company>\NewsCentral\NewsCentral\Ui\AllowedDomains` (REG_SZ) with the **same value** as NewsViewer's `Ui\AllowedDomains` (S04), in the same GPO. The two settings are independent: NewsViewer enforces its own, and NewsCentral uses its own to refuse a More information link that viewers would show grayed out, at entry time. An empty NewsCentral value rejects every new link.

Also deliver `HKLM\Software\<Company>\NewsCentral\NewsCentral\Audit\Enabled` (DWORD; `docs/configuration.md`, or
`Set-RegistryOverrides.ps1 -ComponentName NewsCentral -AuditEnabled`) only if policy must force auditing on or off.

The script also makes the **`config` folder** read-only for authors (inheritance disabled; SYSTEM, Administrators and
AdminsGroup Full, AuthorsGroup Read): otherwise anyone who can create or delete files there could replace `users.json`.
Authors then no longer record a last-login time, and only a system administrator changes users, teams, environment
settings and the shared environment list.

---

## Step 7 — Registry configuration to push

> ⚠️ **Fail-closed by default (since M2): no `Signing\PublicKey`, no content.** `Signing\RequireSignedIndex`
> defaults to `true` (also when no value exists). A machine that lacks `Signing\PublicKey` in **both**
> `NewsService\Signing` **and** `NewsViewer\Signing` rejects every index and shows nothing:
> NewsService logs `index rejected — no public key, signing required` (once per team per hour at Error)
> and NewsViewer skips the team. The GPO must therefore deliver the key **before or with** the install.
> The pilot check is `index accepted — signature valid` in the NewsService log. Per-team
> `Signing\{team}\PublicKey` values from the old model are ignored (NewsService warns once at startup).

Two separate trees, one per fleet component. **GPO owns this configuration entirely — the installer must
not write it** (`docs/packaging.md`). Full key surface: `docs/configuration.md`.

**M6 — generate the repository/signing values instead of hand-copying them.** NewsCentral's
Environment Management page has an **Export fleet settings** button (SystemAdmin only) for the
current environment. It generates the `Repository\`/`AzureBlob\` and `Signing\` values below for
NewsService and NewsViewer — as a `Set-RegistryOverrides.ps1` invocation (dev/pilot machines only;
see the script's own scope note) and as a `.reg` file for GPO hand-off. The fleet always
authenticates with its **own** app registration and certificate (§2.1 above), never the authoring
app's, so the export never contains the authoring `ClientId` or any credential — the fleet's
`ClientId` and certificate thumbprint are emitted as literal placeholders (`<FLEET-CLIENT-ID>`,
`<CERT-THUMBPRINT>`) that **must be replaced before use**. Everything outside this surface — Entra,
Delivery, Telemetry, Logging, Hmac, Display, Ui — is still configured by hand per
`docs/configuration.md`.

```
HKLM\Software\{Company}\NewsCentral\NewsService\
HKLM\Software\{Company}\NewsCentral\NewsViewer\
```

> ⚠️ **Each component reads only its own subkey.** The `teams\` list and the `Signing\` subtree are
> **not** shared — both must be written twice, once under `NewsService` and once under `NewsViewer`.
> Provisioning only one is a silent half-configuration: NewsService will sync content that NewsViewer
> then refuses or ignores.

### 7.1 Value type rules

| Rule | Detail |
|---|---|
| `REG_SZ` for any integer | Any numeric value whose legitimate range includes `0` or `1` **must** be `REG_SZ`. DWORD `0`→`"False"`, `1`→`"True"`, and the int binder then throws at startup. |
| `REG_SZ` for doc-specified booleans | Written as `"true"`/`"false"` strings where `docs/configuration.md` specifies `REG_SZ` — `RequireSignedIndex`, `UseWinHttpProxy`, `Entra\Enabled`. |
| `DWORD` is fine for genuine booleans | `BypassDailyGate`, `BypassImageIntegrityCheck`, `Telemetry\UploadEnabled`, `Delivery\LockScreenEnabled`, `Delivery\WallpaperEnabled`, `Active` — the `0`/`1` coercion is exactly what these want. |
| `teams\` uses value **names** | The team folder name is the value *name*; the data is ignored. Write an empty string as data. |

### 7.2 NewsService tree

```
HKLM\Software\{Company}\NewsCentral\NewsService\
│
├── Service\
│       PollIntervalSeconds     REG_SZ   "60"            ← REG_SZ, never DWORD
│       CacheRootPath           REG_SZ   "C:\ProgramData\NewsCentral"
│
├── Repository\
│       StorageMode             REG_SZ   "Azure" | "Share"
│       SharePath               REG_SZ   "\\fileserver\newscentral"   (Share mode)
│
├── AzureBlob\
│       AuthMode                REG_SZ   "Certificate" | "ClientSecret" | "ClientSecretEnv"
│       TenantId                REG_SZ   <tenant GUID>
│       ClientId                REG_SZ   <agent app client GUID>
│       AccountName             REG_SZ   <storage account, no suffix>
│       ContainerName           REG_SZ   "newscentral"
│       CertificateThumbprint   REG_SZ   <40 hex chars>   (AuthMode=Certificate)
│       ClientSecret            REG_SZ   <secret>         (AuthMode=ClientSecret)
│       UseWinHttpProxy         REG_SZ   "true" | "false"
│
├── Signing\
│       RequireSignedIndex      REG_SZ   "true"           (default; omit or leave "true")
│       PublicKey               REG_SZ   <Base64 SPKI>    (the environment's key - REQUIRED, see note)
│       PublicKeyPrevious       REG_SZ   <Base64 SPKI>    (rotation window only)
│
├── Entra\                                                (omit entirely if not used)
│       Enabled                 REG_SZ   "false"
│       GracePeriodMinutes      REG_SZ   "240"
│       MaxDynamicTeams         REG_SZ   "16"
│       GroupTeams\ExclusionGroup                     REG_SZ  <display name>
│       GroupTeams\Instances\{label}\InclusionGroup   REG_SZ  <display name>
│       GroupTeams\Instances\{label}\ExclusionGroup   REG_SZ  <display name>
│       AttributeSchemes\{scheme}\Selector            REG_SZ  "extensionAttribute1"
│       AttributeSchemes\{scheme}\Mappings\{value}    REG_SZ  <rule>
│
├── Delivery\
│       DefaultLockScreenPath   REG_SZ   <absolute path>  ("" = no default; no content + no default CLEARS a value NewsService published)
│       DefaultWallpaperPath    REG_SZ   <absolute path>  (same semantics; NewsService owns the wallpaper image)
│       PublishedImagePath      REG_SZ   <absolute path>  (protected folder for both surfaces; must not be user-writable; default C:\Windows\Web\NewsCentral)
│       LockScreenEnabled       DWORD    1                (0 = lock-screen surface not managed at all — NOT a revert; RDS/VDI/RemoteApp opt-out)
│       WallpaperEnabled        DWORD    1                (0 = wallpaper surface not managed at all — NOT a revert; mirrors LockScreenEnabled)
│
├── Hmac\
│       SecretKey               REG_SZ   <Base64 32-byte>  (telemetry only)
│
├── Telemetry\
│       UploadEnabled           DWORD    1
│
├── Logging\LogLevel\Default              REG_SZ   "Information"
├── Logging\EventLog\LogLevel\Default     REG_SZ   "Information"
│
└── teams\
        {teamFolderName}        REG_SZ   ""      (one value per static team)
```

> ⚠️ **Prerequisite — Windows Enterprise or Education.** Personalization CSP — the mechanism behind
> both the `Delivery\LockScreenEnabled` and `Delivery\WallpaperEnabled` applies above — is documented
> by Microsoft as supported on Windows Enterprise and Education SKUs, and on Pro only under Shared PC
> / Cloud Config (BootToCloud) configurations. Confirm the pilot fleet's SKU before Step 8; the raw
> registry writes are widely observed to work on Pro outside those configurations too, but that
> remains undocumented behavior.

> ⚠️ **Prerequisite — Windows Spotlight.** Disable it via GPO or Intune before enabling
> `LockScreenEnabled`:
> `HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent` → `DisableWindowsSpotlightFeatures = 1`,
> `DisableSpotlightCollectionOnDesktop = 1`. Spotlight is not written by NewsCentral and is owned by
> GPO/Intune, but left enabled it will intermittently override the CSP-applied lock screen — a
> symptom ("the lock screen sometimes reverts") that is very hard to diagnose after the fact if this
> prerequisite was missed during Step 7.

### 7.3 NewsViewer tree

```
HKLM\Software\{Company}\NewsCentral\NewsViewer\
│   CacheRootPath               REG_SZ   "C:\ProgramData\NewsCentral"
│   Active                      DWORD    1       (0 = NewsViewer exits at startup with no action; not a revert)
│   BypassDailyGate             DWORD    0       (1 only on test machines)
│   BypassImageIntegrityCheck   DWORD    0       (1 only on test machines)
│
├── Display\
│       LogicalDayStartHour     REG_SZ   "5"     ← REG_SZ; "0"/"1" break as DWORD
│       ShowInRemoteSessions    REG_DWORD  0/1 (default 1; 0 = no poster/notification in remote sessions)
│
├── Delivery\
│       WallpaperStyle              REG_SZ   "Fit" | Fill | Stretch | Center | Tile   (style only — image is NewsService/CSP-owned)
│       WallpaperBackgroundColor    REG_SZ   "0 0 0"
│
├── Ui\
│       Theme                   REG_SZ   "Dark" | "Light"
│       ShowDeliveredImages      REG_DWORD  0/1 (default 0; Wallpaper / Lock screen buttons on the poster form)
│       NotifyNewContent         REG_DWORD  0/1 (default 1; "News arrived!" notification on new content)
│       FrameColor                REG_SZ     #RRGGBB (default empty = theme; poster side panel, title bar and border)
│       FrameTextColor            REG_SZ     #RRGGBB (default empty = automatic black/white by contrast)
│       AllowedDomains            REG_SZ     domains the poster's More information link may open, ';'-separated (default empty = button disabled; deploy no later than the NewsViewer build that enforces it)
│
├── Signing\
│       RequireSignedIndex      REG_SZ   "true"           (default; omit or leave "true")
│       PublicKey                REG_SZ   <Base64 SPKI>    (the environment's key - REQUIRED, see note)
│       PublicKeyPrevious        REG_SZ   <Base64 SPKI>
│
├── Hmac\
│       SecretKey               REG_SZ   <same Base64 32-byte key as NewsService>
│
└── teams\
        {teamFolderName}        REG_SZ   ""
```

> ⚠️ **Deliver `Ui\AllowedDomains` no later than the NewsViewer build that enforces it (S04).** That build opens
> the poster's **More information** link only for an `https` URL on a listed domain (or a subdomain); with the
> value absent or empty the button is grayed out on every machine, fleet-wide. Example value:
> `contoso.com;intranet.contoso.net`. Posters whose link is refused leave a `MoreInfoUrlRejected` line in the
> user's `launch.log` (`docs/newsviewer-spec.md`).

> ⚠️ **The HMAC key must be identical in NewsViewer (signer) and NewsService (verifier).** It covers
> session telemetry only — index signing is ECDSA. An empty key disables HMAC system-wide and telemetry
> passes through as `Disabled`: an acceptable phased-rollout state, not a permanent one.

### 7.4 Values NOT delivered by GPO

| Item | Where it lives | Why |
|---|---|---|
| `Company` | Build-time constant in `Directory.Build.props` | It defines the hive path and cannot be read from the path it defines |
| `NEWSSERVICE_AZURE_CLIENTSECRET` | Machine-scope environment variable | Only when `AuthMode=ClientSecretEnv`. The single configuration item that does not arrive through GPO. **Machine scope is required** — Local System does not see user variables. |
| Environment **private** signing keys | `environment-signing.json` in the secure store (`{SecurePath}\keys\`), authoring tier only | Never distributed; public keys only reach the fleet |

> ⚠️ **`ClientSecretEnv` is a convenience delivery, not a secure one.** A machine environment variable is
> clear text readable by any SYSTEM process, exactly like a registry `REG_SZ` secret. Do not present it
> to a security reviewer as hardened. `Certificate` mode is the production answer; Azure Key Vault
> remains the intended secure path.

---

## Step 8 — Validation and cutover

### 8.1 Pilot validation sequence

| # | Check | Expected result | Where to look |
|---|---|---|---|
| 1 | GPO tree landed under the correct `Company` | Values readable under `…\NewsCentral\NewsService\` | `reg query` |
| 2 | NewsService starts and reaches the repository | `status.json` shows `isOnline: true` and the expected `syncSource` | `%ProgramData%\NewsCentral\status.json` |
| 3 | Index signature verifies | Acceptance logged as `Valid` per team on first cycle or on change | Event Log → Application, source `NewsService` |
| 4 | Content cached | `index.json` + images present under the team folder | `%ProgramData%\NewsCentral\{team}\` |
| 5 | Lock screen applied | `LockScreenImagePath` points at a `lockscreen-*` file under `Delivery:PublishedImagePath` (not the ProgramData cache) | `HKLM\…\PersonalizationCSP` |
| 6 | Poster shows once per logical day | `ViewerForm` appears at logon or unlock, once | Visual |
| 7 | Wallpaper **image** applied by NewsService and **style** re-asserted by NewsViewer | `DesktopImagePath` points at a `wallpaper-*` file under `Delivery:PublishedImagePath`; HKCU style values set on every NewsViewer run, including already-shown days | `HKLM\…\PersonalizationCSP` + Visual + `HKCU\Control Panel\Desktop` |
| 8 | Telemetry round-trip | `session-*.json` appears in `uploads\`, is forwarded, then removed | `%ProgramData%\NewsCentral\uploads\` |
| 9 | Dynamic teams *(if enabled)* | `resolved-teams.json` lists the expected teams with `State: Active` | `%ProgramData%\NewsCentral\resolved-teams.json` |
| 10 | Negative: tampered index rejected | Team skipped, `Error` logged | Event Log |
| 11 | NewsViewer launch surface registered | Both tasks exist: `\<Company>\NewsCentral\NewsViewer Launch` and `\<Company>\NewsCentral\NewsViewer Content Arrived`; principal group `S-1-5-32-545`, run level `LeastPrivilege`. The installer log of the registration script ends with `Verification passed` and exit code 0 | `Get-ScheduledTask -TaskPath '\<Company>\NewsCentral\' \| % { $_.TaskName, $_.Principal.GroupId, $_.Principal.RunLevel }`; installer log |
| 12 | NewsService event source present | `[System.Diagnostics.EventLog]::SourceExists('NewsService')` is true and the source is under the `Application` log (the script creates it; NewsService also creates it at startup unless `ViewerTasks\RegisterEventSource` = `"false"`) | PowerShell; Event Viewer -> Application |
| 13 | NewsService logs no launch-surface drift at startup | Within about 10 s of service start: `NewsViewer scheduled tasks verified: both are in place (... matched by nc-hash stamp ...)` and **no** `Warning` containing `launch surface has drifted` or `NewsViewer executable not found` | Event Log -> Application, source `NewsService` |

### 8.2 The fail-closed default

`RequireSignedIndex` is `true` by default, so there is nothing to flip: the pilot proves the key
inventory instead. Check 3 above must report `index accepted — signature valid` for **every** team on
**every** pilot machine, with `Signing\PublicKey` present in both `NewsService\Signing` and
`NewsViewer\Signing`.

```
NewsService\Signing\PublicKey            REG_SZ   <Base64 SPKI>
NewsViewer\Signing\PublicKey             REG_SZ   <Base64 SPKI>
```

`Unsigned` and `Disabled` indexes are rejected alongside `Invalid`. A machine without the key goes dark
rather than display unverified content - the intent. Setting `RequireSignedIndex` to `"false"` is a
deliberate pass-through for diagnosis only.

### 8.3 Diagnosing a silent no-op

A freshly deployed machine that behaves as though it has no configuration has one overwhelmingly likely
cause.

> ⚠️ **`Company` mismatch.** `OpenSubKey` returns `null` with no error, every GPO override is discarded,
> and the component runs on shipped defaults — NewsService finds no teams and does nothing, NewsViewer
> shows nothing. Verify that the built-in `Company` value and the GPO hive path use the **identical**
> string before investigating anything else.

Other silent-failure candidates, in order of likelihood:

- `teams\` value names do not match the generated folder names exactly (case, hyphens, a stray `team-`
  prefix).
- `Signing\` or `teams\` written under `NewsService` but not `NewsViewer`, or vice versa.
- A numeric value provisioned as `REG_DWORD` instead of `REG_SZ` — this one is *not* silent, it crashes
  the component at startup, but the crash can be mistaken for a service that never installed.
- Public key stale after a rotation where content never changed — `PublicKeyPrevious` removed too early.
- NewsViewer silent on an RDP, Citrix, or Horizon session: `Display:ShowInRemoteSessions` was set to `0`
  (`reason=RemoteSession` in `launch.log`), the client window is smaller than 1920×1080
  (`NoQualifyingMonitor`) or the session is a published app without a shell (`NoShell`).

> **NewsViewer has no logging framework, but it keeps a per-user `launch.log`.** When a user reports "I
> don't see any news", read `%LOCALAPPDATA%\NewsCentral\launch.log` first: every launch (except on a machine
> with `Active = false`) logs a `decision=` and the `reason=` it was taken for (`NoContent`,
> `NoQualifyingMonitor`, `NotVisible`, `NoShell`, `Idle`, `Busy`, `RemoteSession`, `AlreadySeen`, …). Then
> verify the NewsViewer registry tree and, if needed, clear the per-user gate and marker by deleting
> `%LOCALAPPDATA%\NewsCentral\viewerstate.json`. EventLog logging for NewsViewer is a planned follow-up
> (`docs/packaging.md`).

### 8.4 Verbose logging for troubleshooting

NewsService logs its per-cycle detail (index unchanged, image up to date, retries of an incomplete sync,
cache self-heal repeats) at **Debug**, which is off by default (`Logging:LogLevel:Default` = `Information`).
NewsViewer has no log level to raise: its per-user `launch.log` is always written (size-capped) and is the
place to look for launch decisions (see 8.3).

**Dev box** — an environment variable; the `__` maps to `:`, and the category prefix `NewsService` covers
every `NewsService.*` logger without turning on framework noise:

```
set Logging__LogLevel__NewsService=Debug
dotnet run --project NewsService
```

`dotnet run` starts in the **Production** environment (NewsService ships no `launchSettings.json`) unless
`DOTNET_ENVIRONMENT=Development` is set, so the built-in `appsettings.Development.json` mechanism is not
used. The dev overlay file is nevertheless loaded explicitly and unconditionally when present (see below),
so no environment switch is needed for it.

**Fleet** — the registry `Logging\` key, via the deployment script (REG_SZ, written under
`HKLM\Software\{Company}\NewsCentral\NewsService\Logging\LogLevel`):

```powershell
.\scripts\Set-RegistryOverrides.ps1 -Company "<Company>" -ComponentName NewsService -LogLevel Debug
# back to normal:
.\scripts\Set-RegistryOverrides.ps1 -Company "<Company>" -ComponentName NewsService -LogLevel Information
```

The script only writes `Logging:LogLevel:Default`, which raises **every** category (including
`Microsoft.*`) to Debug — expect a lot of output; restart the service afterwards and revert when done. A
single-category value (`Logging\LogLevel\NewsService`, REG_SZ `Debug`) can be created by hand and is read
the same way.

**Precedence as implemented** (`NewsService/Program.cs`; later wins): `appsettings.json` →
`appsettings.{Environment}.json` (host default) → environment variables → command-line arguments →
`appsettings.Development.json` (explicit, optional) → **registry** (always last). So the registry beats
the dev overlay, which beats an environment variable. Within the logging system itself, the **most specific
matching category** wins regardless of source: a registry `Default=Warning` does not turn off an
environment-variable `NewsService=Debug`, because they are different keys.

**Where Debug appears.** The host's Console and Debug-output providers honour `Logging:LogLevel:*`
(Console is only visible under `dotnet run`, not as a service). The **EventLog provider has its own
minimum level**, `Logging:EventLog:LogLevel:Default` (shipped `Information`, `-EventLogLevel` in the
script), which takes precedence over the general section for that provider — so Debug never reaches the
Windows Event Log unless that value is lowered too. There is no file sink. The `ContentChanged` event
(2001) is written directly and is unaffected by any of these levels.

> ⚠️ Lowering `-EventLogLevel` to Debug floods the Application log on every client. Use it only
> temporarily on individual machines and revert afterwards.


### 8.5 Rolling out new-content notifications

The "News arrived!" notification (NewsViewer) and the launch surface behind it
(`docs/newsviewer-spec.md` → "Launch triggers", "New-content notification"; installer side in
`docs/packaging.md` → "NewsViewer launch surface").

**Steps**

1. **Install NewsService and NewsViewer together** (NewsService first where the order is controllable). The
   notification records add optional fields to the HMAC-signed session telemetry; an older NewsService
   discards those records as invalid.
2. **Register the scheduled tasks from the package: the installer owns them.** Run the packaged
   `Register-NewsViewerTasks.generated.ps1` elevated (docs/packaging.md, "The registration script is a build
   artifact"). It registers `NewsViewer Launch` (logon + 15 s, unlock, RDP connect, console connect) and
   `NewsViewer Content Arrived` (NewsService event 2001, `--trigger content`) with the install path, company
   and event log/source baked in at build time, creates the event source if missing, and removes the
   **legacy Workstation Unlock task** (`\{Company}\NewsCentral\NewsViewer`). **NewsService never creates,
   repairs or deletes tasks**; it only verifies them (`ViewerTasks\Mode` = `VerifyOnly`, default) once per
   hour and logs drift at `Warning`. There is no Run entry any more; logon is a trigger of the Launch task.
   Make sure the package no longer writes the HKLM Run value `NewsViewer`, and remove an existing one on
   upgrade (NewsService no longer does). If the obsolete `ViewerTasks\Manage` value is still deployed by
   GPO, remove it: it is ignored and NewsService logs one `Warning` at startup. Watch the first start in the
   Application log (source `NewsService`): "verified: both are in place" means it worked; a `Warning` naming
   a missing task or path means the script was not run or NewsViewer is not where
   `ViewerTasks\NewsViewerPath` / the default says.
3. **The NewsService event source** (`NewsService`, log `Application`) is created by the script above and,
   unless `ViewerTasks\RegisterEventSource` = `"false"`, by NewsService itself explicitly at startup. The
   Content Arrived task subscribes to exactly that log/source; the script bakes in the values it was built
   with, so after a GPO change of `Logging:EventLog:LogName`/`SourceName` rebuild and re-run the script
   (NewsService reports the old subscription as drift).
4. **`Ui:NotifyNewContent`** (NewsViewer, DWORD, default on = `1`) controls the notification. To turn it
   off fleet-wide, deliver `HKLM\Software\{Company}\NewsCentral\NewsViewer\Ui\NotifyNewContent` = `0` by
   GPO; on a dev/pilot machine:

   ```
   .\scripts\Set-RegistryOverrides.ps1 -Company "<Company>" -ComponentName NewsViewer -NotifyNewContent $false
   ```

   With it off, a launch after today's poster was already shown exits silently, as before the feature.

5. **Remote sessions now get posters and notifications.** NewsViewer used to exit silently in any RDP,
   Citrix or Horizon session; it no longer does (`docs/newsviewer-spec.md` → "Remote sessions"). In a
   remote session: a `UseVirtualDesktop` poster is shown on the standard desktop; the HKCU wallpaper style
   re-assert is never run; a published-app session without a shell exits as `NoShell`; and a client window
   smaller than 1920×1080 exits as `NoQualifyingMonitor`. To restore the old behavior fleet-wide, deliver
   `HKLM\Software\{Company}\NewsCentral\NewsViewer\Display\ShowInRemoteSessions` = `0` (DWORD) by GPO; on a
   dev/pilot machine:

   ```
   .\scripts\Set-RegistryOverrides.ps1 -Company "<Company>" -ComponentName NewsViewer -ShowInRemoteSessions $false
   ```

   With it off, a remote-session launch exits with `reason=RemoteSession` (logged); `--manual` still shows
   the poster.

**Upgrade expectations (what users will and will not see)**

- On the first launch after the upgrade the per-user content marker is empty: NewsViewer adopts the
  current content silently (`MarkerInitialized` in `launch.log`). **No notification** is raised for a
  poster the user already saw.
- **One possible one-time notification** because the poster is now ranked newest-to-team (the later of the
  presentation's last-modified and the assignment's publish date): a client may select a different
  presentation than before. If the daily gate was already hit that day, that user gets one
  "News arrived!" for it. Expected, once.

**Dev-box task setup.** NewsService does not register tasks. Point the verifier at the build with
`ViewerTasks\NewsViewerPath` (elevated PowerShell at the repo root), then register the tasks with the
script - either the reference script or one generated from your build:

```
dotnet build
$exe = (Resolve-Path .\NewsViewer\bin\Debug\net10.0-windows\NewsViewer.exe).Path
.\scripts\Set-RegistryOverrides.ps1 -Company "<Company>" -ComponentName NewsService -ViewerTasksNewsViewerPath $exe
.\scripts\Register-NewsViewerTasks.ps1 -Company "<Company>" -ExePath $exe
# Optional: run NewsService in a console. It does NOT register or repair anything - the script above did that;
# NewsService only verifies the tasks and logs "verified" or a drift Warning.
dotnet run --project NewsService
```

`-Company` is required in every mode (it names the `\<Company>\NewsCentral\` task folder). The script
registers both tasks with that path, makes sure the event source exists, removes the legacy unlock task, the
leftover dev-box tasks and the Run value, and prints each task's triggers (including the logon trigger),
arguments and command. Re-running is harmless; `-Company "<Company>" -Uninstall` removes the two tasks.
To exercise the packaged path instead, build the artifact and run it:
`NewsService.exe --emit-task-script .\Register-NewsViewerTasks.generated.ps1 --newsviewer-path $exe`, then
`.\Register-NewsViewerTasks.generated.ps1 -WhatIf` and without `-WhatIf`. Tasks registered by the reference
script carry no `nc-hash`, so NewsService verifies them by field comparison only; the generated script stamps
them. Both scripts must be run from **64-bit** PowerShell (they refuse a 32-bit host with a non-zero exit; see docs/packaging.md). NewsService run non-elevated is fine: it only reads the tasks. If a dev box still has the old
`ViewerTasks\Manage` value, NewsService logs one Warning and ignores it.

### 8.6 Field validation checklist (notification and launch surface)

Not yet validated in the field. Run on the listed environment and record the result.

| # | Scenario | Expected result |
|---|---|---|
| 1 | **Virtual Desktop (VDI) presentation** — an assignment with `UseVirtualDesktop`, notification shown, user clicks it | The poster opens on its own virtual desktop (not behind the user's windows) with the countdown stopped; closing it returns to the original desktop. `launch.log` shows `outcome=Expanded`. |
| 2 | **Multi-team device** — two of the device's teams change in the same sync cycle | Exactly **one** event 2001 in the Application log for that cycle and **one** NewsViewer launch (`trigger=content` once in `launch.log`); one notification for the single newest-to-team item. |
| 3 | **Windows Hello (face/fingerprint) unlock** | The unlock-triggered launch is **not** treated as idle (the idle check applies only to `--trigger content`); the poster / notification behave exactly as after a password unlock. |
| 4 | **Fast user switching (local console)** and the **`ConsoleConnect`** trigger | `ConsoleConnect` launches NewsViewer in the session that is switched back to. With two simultaneous **local** sessions, **each** gets its own notification (Parallel tasks + the per-session `Local\` mutex): no `AlreadyRunning` caused by the other session. |
| 5 | **150% scaling / mixed DPI / multiple monitors** | The notification appears on the **primary** monitor only, at the bottom-right of its working area (bottom-left for Arabic), 5 DIPs from the corner, correctly scaled; no clipping in de/zh/pt/ar. |
| 6 | **Fresh machine** | The event source is registered by the registration script (and created by NewsService at startup if still missing) before the first content event; after the first sync that changes content, event 2001 appears and the Content Arrived task fires (`Last Run Result` 0x0 in Task Scheduler). |
| 7 | **Live Entra multi-team validation** *(carried over)* | On an Entra-joined box with `Entra:Enabled=true`, a device in several dynamic teams resolves them all (`resolved-teams.json`), one cycle produces one event, and NewsViewer selects the newest-to-team item across the union. See `docs/entra-dynamic-teams.md`. |
| 8 | **Real RDP session** (a full desktop) | The `RemoteConnect` launch is logged in `launch.log` of that session. A publish shows the mini form inside the RDP session (and the poster, once per day); lock the RDP session (pause during lock), disconnect and reconnect (the reconnect launch is logged, no duplicate poster, marker respected). The wallpaper style is **not** rewritten (check `HKCU\Control Panel\Desktop\WallpaperStyle` is untouched). |
| 9 | **Citrix / Horizon full desktop** | Same as 8: poster and notification appear in the published desktop; detection works for ICA (`SM_REMOTESESSION`) and Horizon (`ViewClient_Machine_Name`). |
| 10 | **Citrix/RDS published app (RemoteApp)** — no Explorer in the session | NewsViewer exits with `reason=NoShell` (no taskbar window, nothing to show on); no poster, no error, nothing hangs. |
| 11 | **Multi-session host (AVD / RDS)** with several users logged on | Each user session gets its own launch and its own notification (Parallel + `Local\` mutex). One content event 2001 starts **one NewsViewer process per logged-on session** (the event task fires in every session), each deciding on its own marker; no session blocks another. |
| 12 | **`UseVirtualDesktop` poster over RDP** | The poster opens on the **standard** desktop (no desktop switch, no black background form); the launch-log line carries `note=VirtualDesktopFallback`; expanding the notification behaves the same. |
| 13 | **RDP / Citrix / Horizon client window below 1920×1080** | Expect `reason=NoQualifyingMonitor` (no poster, no notification). Decide later whether to relax the threshold for remote sessions. |
| 14 | **`Display:ShowInRemoteSessions` = 0** | In a remote session a launch logs `reason=RemoteSession` and shows nothing; `--manual` still shows the poster. Setting it back to `1` restores posters. |
| 15 | **Logon trigger** (task-owned, no Run value) | About 15 s after sign-in NewsViewer starts (`launch.log` has a line for the logon). Note whether a second line with `reason=AlreadyRunning` follows (ConsoleConnect also firing at logon is harmless). The HKLM Run `NewsViewer` value is gone. |
| 16 | **NewsService reports a deleted task; it does not repair it** | Delete `NewsViewer Launch` (elevated), restart NewsService: within about 10 s it logs a `Warning` that the task is missing (`launch surface has drifted`) and does **not** recreate it; re-running the registration script restores it and the next check logs `verified`. A non-elevated delete attempt is refused ("Access is denied"). |

---

## Upgrading an existing installation

Two bugs fixed in this build affected every environment that had ever published content across
teams, and the fix does not retroactively correct data already on disk — a one-time repair closes
that gap.

**Why.** Before the fix, publishing an assignment from team S to team T wrote T's copy of the
assignment while it was still `Approved`, and only updated S's own record afterward — so T's copy
never flipped to `Published` and T's devices never received the content. Separately, a team's
`index.json` listed every assignment that team had ever published, including ones sent to OTHER
teams — so a publishing team's own devices could show content meant for someone else.

**What to do.** After installing a build that includes these fixes, in **every** environment: sign
in as a System Administrator, open **Index Management**, run **Repair published assignment
copies**, then run **Regenerate all**. Do this once per environment, not once overall — each
environment's data is separate from the others.

**Running it again is harmless.** The repair tool only writes a copy that is actually stale; a copy
that already matches its source reports as "already in sync," not "left" or an error. Re-running it
on an environment that was already repaired is a safe no-op.

---

## Preparation checklist

| # | Task | Owner | Done |
|---|---|---|---|
| 1.1 | Deployment shape decided and recorded (storage, auth, signing, Entra) | Solution owner | ☐ |
| 1.2 | `Company` value final in `Directory.Build.props` and communicated to the GPO author | Solution owner | ☐ |
| 1.3 | Team list and exact folder names agreed | Solution owner | ☐ |
| 2.1 | Agent app registration created; credential attached | IT / Cloud | ☐ |
| 2.2 | Graph permissions added and **admin consent granted** (if Entra enabled) | IT / Identity | ☐ |
| 2.3 | Authoring app registration created; public client flows enabled; `user_impersonation` added | IT / Cloud | ☐ |
| 3.1 | Storage account created; shared key access disabled; container private | IT / Cloud | ☐ |
| 3.2 | Authors security group created and populated | IT / Identity | ☐ |
| 3.3 | RBAC: authors group → Storage Blob Data Contributor | IT / Cloud | ☐ |
| 3.4 | RBAC: agent SP → Storage Blob Data Reader (or Contributor if telemetry upload) | IT / Cloud | ☐ |
| 3.5 | Proxy decision made; `UseWinHttpProxy` set if required | IT / Network | ☐ |
| 4.1 | Certificate issued and deployed to `LocalMachine\My` on pilot machines | IT / PKI | ☐ |
| 4.2 | Public `.cer` uploaded to the agent app registration; thumbprint recorded | IT / Cloud | ☐ |
| 5.1 | *(Entra)* extensionAttributes stamped and/or groups created and populated | IT / Identity | ☐ |
| 5.2 | *(Entra)* Inclusion group names confirmed unique; canonicalized folder names published | Solution owner | ☐ |
| 6.1 | Signing key pair generated per team in Key Management | NewsCentral admin | ☐ |
| 6.2 | Index republished per team; self-verify guard passed | NewsCentral admin | ☐ |
| 6.3 | Public keys extracted and mapped to exact team folder names | NewsCentral admin | ☐ |
| 6.4 | HMAC 32-byte key generated and stored securely | NewsCentral admin | ☐ |
| 7.1 | NewsService registry tree authored; value types verified `REG_SZ` vs `DWORD` | IT / Ops | ☐ |
| 7.2 | NewsViewer registry tree authored — including its **own** `teams\` and `Signing\` | IT / Ops | ☐ |
| 7.3 | GPO linked to the pilot OU and applied | IT / Ops | ☐ |
| 7.4 | *(ClientSecretEnv only)* machine environment variable provisioned | IT / Ops | ☐ |
| 7.5 | `Delivery:PublishedImagePath` confirmed not user-writable: the default `%SystemRoot%\Web\NewsCentral` (or, for any other path, an explicit installer-set ACL); check with `icacls` that `Users`/`Everyone`/`Authenticated Users`/`INTERACTIVE` have read-only rights only. A writable folder makes NewsService log an `Error` and refuse to publish | IT / Ops | ☐ |
| 7.6 | Registration script emitted from the release build (`NewsService.exe --emit-task-script`, docs/packaging.md) and handed to the packaging team, with the note that it needs 64-bit PowerShell | Build / Release | ☐ |
| 7.7 | Installer registers both NewsViewer tasks at install (runs the packaged script, expects exit 0) and removes both at uninstall (`-Uninstall`) | Packaging | ☐ |
| 8.1 | Pilot validation checks 1-13 passed | QA / Ops | ☐ |
| 8.2 | Stabilisation window completed on the pilot group | Project lead | ☐ |
| 8.3 | `Signing\PublicKey` present on both components and `index accepted - signature valid` seen per team | IT / Ops | ☐ |
| 8.4 | Rollout to the wider fleet | IT / Ops | ☐ |

---

## Appendix — Reference commands

**Generate the HMAC key (Base64, 32 bytes):**

```powershell
[Convert]::ToBase64String(
    [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

**Verify the machine is Entra-joined:**

```powershell
dsregcmd /status        # AzureAdJoined : YES
whoami /upn             # must return user@domain, not DOMAIN\user
```

**Verify a registry tree landed:**

```powershell
reg query "HKLM\Software\{Company}\NewsCentral\NewsService" /s
reg query "HKLM\Software\{Company}\NewsCentral\NewsViewer"  /s
```

**Verify the agent certificate is present:**

```powershell
Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -like '*NewsService*'
```

**Force a NewsViewer re-display on a test machine:**

```powershell
Remove-Item "$env:LOCALAPPDATA\NewsCentral\viewerstate.json"
# or set BypassDailyGate = 1 (DWORD) on that machine only
```

**Canonicalization of a group display name:**

```
"NewsCentral Prague ITS"  ->  "newscentral-prague-its"
# lower-case; space/underscore -> '-'; strip anything outside [a-z0-9-]
```

### Documents to hand over

| Audience | Document |
|---|---|
| Azure / Identity team | `docs/azure-setup.md` + Steps 2-5 of this document |
| GPO / Endpoint team | Step 7 of this document + `docs/configuration.md` for the full key surface |
| Packaging team | `docs/packaging.md` — complete and self-contained |
| Support / service desk | Step 8.3 of this document |
