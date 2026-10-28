# NewsCentral authoring tier — Authorization concept

**Scope:** the NewsCentral authoring app (the publishing component): its application roles, every folder and
container it reads or writes, and the permissions each principal needs there, for file-share and Azure deployments.
NewsService and NewsViewer appear only where they touch the same locations.

**Status:** concept, reviewed against the code at `master` (2026-10-28). Section 9 lists where the code or the
provisioning script does not yet match it.

Related: `docs/anti-tamper.md` §9 (S02 secure store, users.json and audit log, drift warnings),
`scripts/Set-AuthoringAcl.ps1` (applies part of section 6), `docs/production-deployment.md` step 3 (Blob RBAC),
`docs/newscentral-spec.md` (pages and services).

---

## 1. Principles

1. **Two layers, different jobs.** *Application roles* (`config\users.json`) decide the workflow: who may author,
   approve, publish and administer. *File-system ACLs and Azure RBAC* decide what a person can do with the data.
   The app runs as the operator's own Windows account, and against Blob Storage with the operator's own Entra
   identity, so **whatever the app can do for a user, that user can also do with Explorer, PowerShell or Storage
   Explorer.**
2. **Hard boundaries are only the ones enforced by ACL or RBAC.** They are:
   - authors versus everybody else;
   - administrators versus authors (`config\`, `audit\`, key administration);
   - who can read each team's private signing key;
   - who can write to the distribution.

   Separation between teams' *content* and the approval step are **workflow rules**. They hold for people who use
   the app, not for a member of the authors group who opens the share directly (section 9, F1).
3. **The fleet trusts signatures, not the share.** For a static team with `RequireSignedIndex=true`, a device
   accepts an `index.json` only when it verifies against the registry-pinned public key. That makes the private-key
   ACL the boundary that protects the fleet. Dynamic (Entra) teams trust the key that travels with the content, so
   for them **distribution write access is the boundary** (`docs/security.md`, "Trust note").
4. **The app never sets an ACL.** `Set-AuthoringAcl.ps1`, run once by an administrator, provisions permissions. The
   app only warns about drift (Key Management, Environment Management). This is the same rule as NewsService.
5. **Groups, never individuals.** Every ACE and role assignment names a group. Membership changes are the only
   day-to-day operation.

---

## 2. Principals

### 2.1 Application roles

| Role | Scope | Stored as | In one sentence |
|---|---|---|---|
| **SystemAdmin** | environment-wide | `User.IsSystemAdmin` | Everything in every team; the only role that changes users, teams, environment settings and indexes in bulk. |
| **ContentAuthor** | per team | `TeamRoles[team].Roles` | Creates presentations in the team, assigns them to any team, publishes approved assignments of the team. |
| **ContentApprover** | per team | `TeamRoles[team].Roles` | Approves or rejects assignments **targeting** the team. |
| **TeamAdmin** | per team | `TeamRoles[team].Roles` | Manages the team's signing key (Key Management) and republishes the team's index. |

- Roles are cumulative and per team. `AuthenticationService.HasRole` treats SystemAdmin as holding every role.
- Adding a user to a team on the Users page grants ContentAuthor; the other roles are ticked explicitly.
- The team selector offers only teams in which the user holds at least one role (SystemAdmin: all teams).
- Identity is per environment: a user exists in one environment's `users.json`, and switching environment means
  signing in again. Sign-in uses **UPN login** (the Windows identity's UPN matched to `User.UPN`) or, only when
  `EnableLocalAccounts=true`, a local username and password.

### 2.2 Directory groups and identities

Names are examples; `{Env}` distinguishes environments that share a directory.

| Group / identity | Members | Used for |
|---|---|---|
| `NC-{Env}-Admins` (**AdminsGroup**) | Windows accounts of every SystemAdmin | Full control of the authoring tier |
| `NC-{Env}-Authors` (**AuthorsGroup**) | Windows accounts of **everyone** who signs in: authors, approvers, team admins | Content read/write, read of `config\`, append to `audit\` |
| `NC-{Env}-KeyAdmins-{team}` | TeamAdmins of the team | Modify on `secure\keys\{team}` |
| `NC-{Env}-Signers-{team}` | Everyone who may publish **into** the team (section 3.3) | Read on `secure\keys\{team}` |
| `NC-Fleet-Computers` (or Domain Computers) | Computer accounts of managed devices | Share mode: read content, drop telemetry |
| Entra group `NC-{Env}-Authors` | Same people as the AD Authors group | Blob: publish |
| NewsService service principal | One per fleet (certificate) | Blob: read content, upload telemetry |

Each application role needs a matching directory group, as follows:

| App role | Must also be in |
|---|---|
| SystemAdmin | Admins (also Authors if they author) |
| ContentAuthor of team T | Authors; Signers of every team T publishes into |
| ContentApprover of T | Authors |
| TeamAdmin of T | Authors, KeyAdmins-T, Signers-T |

---

## 3. Application authorization matrix

"Service" means the check is enforced in the service and throws (the authority). "UI" means only the page hides the
control. "—" means no check beyond being signed in.

### 3.1 Content workflow

| Operation | SystemAdmin | ContentAuthor | ContentApprover | TeamAdmin | Enforced |
|---|---|---|---|---|---|
| Create presentation in team T | ✔ | of T | — | — | Service |
| Edit presentation | ✔ | **creator only** | — | — | Service |
| Delete presentation (regenerates target indexes) | ✔ | creator, or any ContentAuthor of T | — | — | Service |
| Duplicate presentation to teams | ✔ | of each target team | — | — | Service (via create) |
| Create / update schedule | ✔ | of T | — | — | **UI only** (F3) |
| Delete schedule | ✔ | ✘ | ✘ | ✘ | Service |
| Create assignment T → any team | ✔ | of source T | — | — | **UI only** (F3) |
| Approve / reject assignment | ✔ | — | of the **target** team | ✘ (sees it, F2) | Service |
| Cancel assignment (Draft / Pending) | ✔ | creator only | — | — | Service |
| Publish assignment (must be Approved) | ✔ | creator, or ContentAuthor of source | — | — | Service |
| Delete assignment, any status | ✔ | creator, or ContentAuthor of source | — | — | Service |
| Process poster statistics | ✔ | ✔ | ✔ | ✔ | — (F6) |

Approval is always required: the Create Assignment page fixes `requiresApproval = true`.

### 3.2 Administration

| Operation | SystemAdmin | TeamAdmin | Others | Enforced |
|---|---|---|---|---|
| Users: view | ✔ | ✔ read-only | ✘ | UI |
| Users: create, edit, UPN, team roles, delete | ✔ | ✘ | ✘ | Service (S02a) |
| Change own password | ✔ | ✔ | ✔ (needs write on users.json, so in practice admins only) | Service |
| Teams: create, edit, delete | ✔ | ✘ | ✘ | Service |
| Key Management of team T (generate, apply, republish) | ✔ | of T | ✘ | UI page gate + ACL on `secure\keys\T` |
| Index Management (regenerate all, repair copies) | ✔ | ✘ | ✘ | UI page gate (F4) |
| Configuration Review, fleet export | ✔ | ✘ | ✘ | UI page gate (F4) |
| Environment Management, environment settings | ✔ | ✘ | ✘ | Service + UI |
| Add / remove environment in the picker | ✔ and policy `AllowUserEnvironments` (or no policy catalog) | ✘ | ✘ | Service |
| Write the shared environment directory | ✔ (signed in) | ✘ | ✘ | Service + `config\` ACL |

### 3.3 Who signs whose index

An index is signed with the **target** team's key whenever that team's published content changes:

- by the publisher, when publishing into the team;
- when a published assignment or presentation that targets the team is deleted or edited;
- by Key Management (republish) and Index Management.

Therefore **reading `secure\keys\T` is required by everyone who publishes into T, not only by T's own authors.**
When the key is unreadable, publishing into T fails before anything is copied; that is the intended fail-closed
behaviour (`SigningKeyGate`). If cross-team publishing is broad, as with the "Other Teams" picker and up to 200
targets, `Signers-T` converges on the whole Authors group. Section 9, F5 covers what that means.

---

## 4. Storage locations

`{DataPath}` is the environment's authoring store. `{dist}` is the local distribution root
(`Distribution.LocalPath`, default `{DataPath}\_distribution`), used when `Distribution.Mode` is local.
`{SecurePath}` and `{AuditPath}` come from `config\environment.json`.

| Location | Contents | Written by | Read by |
|---|---|---|---|
| `{DataPath}\config\` | `users.json`, `teams.json`, `environment.json`, `environments.json` | SystemAdmin sessions | every session (login reads users.json) |
| `{DataPath}\{team}\content\{presentations,schedules,assignments}\` | authoring JSON | authors of the team and of every source team that publishes into it | authors |
| `{DataPath}\{team}\images\{original,generated}\` | uploaded and published images | same as above | authors |
| `{DataPath}\{team}\deleted\` | soft-deleted items | authors (delete paths) | admins (recovery) |
| `{DataPath}\{team}\index.json` | signed team index | publishers, admins | authors |
| `{DataPath}\statistics\` | aggregated view/click counts, `_run.json` | whoever runs statistics | authors (Assignments page) |
| `{SecurePath}\keys\{team}\team-signing.json` (default `{DataPath}\secure`) | team ECDSA private + public key | TeamAdmin of the team, SystemAdmin | signers of the team |
| `{AuditPath}\{yyyy-MM}\*.jsonl` (default `{DataPath}\audit`) | append-only audit log | every session (append) | admins |
| `{dist}\{team}\…` | distributed copy: presentations, schedules, images, `index.json` | publishers | fleet (NewsService) |
| `{dist}\uploads\session-*.json` | telemetry inbox | fleet (NewsService) | statistics run (read + move) |
| `{dist}\telemetry\…` | counted raw telemetry, `_invalid\`, `owner.json`, `.lock` | statistics run | statistics run |
| Blob container (Azure mode), `{team}/…` | same as `{dist}\{team}\` | authors (Entra identity) | NewsService SP |
| Blob container `uploads/` | telemetry (when the agent uploads to Azure) | NewsService SP | nobody yet (statistics are share-only) |
| `%LOCALAPPDATA%\{Company}\NewsCentral\` | `user-environments.json`, `user-preferences.json` | the user | the user |
| `HKLM\Software\{Company}\NewsCentral\NewsCentral\` | policy catalog, `EnableLocalAccounts`, `EnableAutoLogin`, `AllowedDomains`, … | GPO / local administrators | the app |

Telemetry records carry no user identity (session id, presentation, team, times, close reason), so the inbox is
pseudonymous. It still shows which presentations were viewed and when, so it is kept away from non-authors.

---

## 5. Target permissions — overview

| Location | Admins | Authors | Signers-T | KeyAdmins-T | Fleet computers |
|---|---|---|---|---|---|
| `{DataPath}` (root folder itself) | Full | Read, this folder only | — | — | — |
| `{DataPath}\{team}\…` | Full | Modify | — | — | — |
| `{DataPath}\config\` | Full | Read | — | — | — |
| `{DataPath}\statistics\` | Full | Modify | — | — | — |
| `{SecurePath}` and `keys\` | Full | — | — | — | — |
| `{SecurePath}\keys\T\` | Full | — | Read & Execute | Modify | — |
| `{AuditPath}` | Full | append-only (see §6.2) | — | — | — |
| `{dist}` and `{dist}\{team}\…` | Full | Modify | — | — | Read |
| `{dist}\uploads\` | Full | Modify | — | — | drop box, no read (see §6.2) |
| `{dist}\telemetry\` | Full | Modify | — | — | — |

"Admins" always means SYSTEM, BUILTIN\Administrators and AdminsGroup. Nobody else (Users, Everyone, Authenticated
Users, INTERACTIVE) has an entry anywhere in the table.

---

## 6. File shares

### 6.1 Share layout and share-level permissions

Use three shares. Share permissions apply to a whole share, so a separate share is the only way the share level
adds protection to the keys and keeps the fleet out of the authoring store.

| Share | Path | Share permissions | Settings |
|---|---|---|---|
| `NewsCentral$` | `{DataPath}` | Admins: Full; Authors: Change | ABE on; Offline Files `None` |
| `NewsCentral-Secure$` | `{SecurePath}` (outside DataPath) | Admins: Full; each Signers-T: Read; each KeyAdmins-T: Change | ABE on; Offline Files `None`; SMB encryption on |
| `NewsCentral-Dist` | `{dist}` (outside DataPath) | Admins: Full; Authors: Change; Fleet computers: Change | Offline Files `None` |

No Everyone, Authenticated Users or Domain Users entries at the share level. `{AuditPath}` can stay inside
`{DataPath}`: its own NTFS ACL is protected, and authors cannot delete it (6.2, root folder).

### 6.2 NTFS

**`{DataPath}` root.** Disable inheritance and set the following entries. "Subfolders and files only" on the root,
combined with protected ACLs on `config`, `secure`, `audit`, `statistics` and `_distribution`, means an author can
work inside team folders but cannot create, rename or delete anything at the root. In particular they cannot move
`secure\` or `config\` aside and replace it.

| Principal | Rights | Applies to |
|---|---|---|
| SYSTEM, Administrators, AdminsGroup | Full | this folder, subfolders and files |
| AuthorsGroup | Read & Execute | this folder only |
| AuthorsGroup | Modify | subfolders and files only |

New team folders are created by a SystemAdmin (Teams page) and inherit Modify for authors.

**`config\`, `users.json`, `secure\`, `audit\`.** Exactly as `Set-AuthoringAcl.ps1` applies them
(`docs/anti-tamper.md` §9). For the key folders, apply the split from F5:

| Path | Entries |
|---|---|
| `secure\keys\T` | inherited admins; `Signers-T`: Read & Execute; `KeyAdmins-T`: Modify |

**`statistics\`.** Disable inheritance; admins Full; AuthorsGroup Modify. This matches today's behaviour, where any
signed-in user can run statistics (F6).

**`{dist}`.** Disable inheritance; admins Full; AuthorsGroup Modify; fleet computers Read & Execute (this folder,
subfolders and files). That covers the root, which NewsService probes with `Directory.Exists`, and every team
folder.

**`{dist}\telemetry\`.** Disable inheritance; admins Full; AuthorsGroup Modify. No fleet entry.

**`{dist}\uploads\`** is a drop box for the fleet. NewsService copies files in with `File.Copy(..., overwrite: true)`.
Disable inheritance so the fleet's read right from `{dist}` does not apply here.

| Principal | Rights | Applies to |
|---|---|---|
| SYSTEM, Administrators, AdminsGroup | Full | this folder, subfolders and files |
| AuthorsGroup | Modify (the statistics run reads, moves and deletes) | this folder, subfolders and files |
| Fleet computers | Traverse, Read attributes, Create files, Synchronize | this folder only |
| CREATOR OWNER | Modify | files only |

Verify the drop box with one real NewsService upload run before rollout. The `overwrite: true` path on a retry is
exactly the kind of OS seam a mock does not exercise.

**Auditing (SACL).** Successful ReadData, WriteData and Delete on `secure\keys\`, and Write and Delete on `config\`.
Neither is visible to the app's own audit log, which only records actions taken through the app.

**Traverse.** Signers have no entry on `secure\` or `keys\` and reach their team folder through
"Bypass traverse checking", which Windows grants to Everyone by default and which also applies over SMB. A hardening
baseline that removes it breaks publishing with an access-denied error. Keep it, and record that in the baseline.

---

## 7. Azure

### 7.1 Blob distribution (content container)

The authoring app uses `InteractiveBrowserCredential`, so it writes as the signed-in author. Shared-key access
stays disabled (`docs/production-deployment.md` step 3).

| Principal | Role | Scope | Today | Target |
|---|---|---|---|---|
| Entra group Authors | Storage Blob Data Contributor | content container | ✔ | ✔ |
| Entra group Admins | Storage Blob Data Contributor | content container | via Authors | explicit, so admins don't depend on Authors membership |
| NewsService SP | Storage Blob Data Reader | content container | ✔ | ✔ |
| NewsService SP (telemetry) | Storage Blob Data Contributor | content container | when upload is on | **telemetry container only** (F7) |

- Nothing from `config\`, `secure\`, `audit\` or `statistics\` ever goes to Blob. The authors group's Contributor
  role covers the whole content container, which is why `IBlobDistributionService` must never carry those files.
- Per-team write scoping in Blob is possible with an ABAC condition on the blob path prefix. It has the same problem
  as per-team NTFS, though: publishing writes into the **target** team's prefix, so the condition would have to list
  every team a publisher may target. Don't do it unless cross-team publishing is restricted.

### 7.2 Authoring tier in Azure

If there is no on-premises file server, `{DataPath}`, `{SecurePath}` and `{dist}` can live on **Azure Files** with
identity-based SMB authentication (Entra Kerberos or AD DS):

- `{SecurePath}` gets its **own storage account**.
- Disable shared-key access on it.
- Use a private endpoint.
- Share-level RBAC:

  | Group | Role |
  |---|---|
  | Admins | Storage File Data SMB Share Elevated Contributor |
  | Authors (DataPath, dist) | Storage File Data SMB Share Contributor |
  | Signers | Storage File Data SMB Share Reader |
  | KeyAdmins | Storage File Data SMB Share Contributor |

The NTFS layer from section 6.2 applies unchanged, so `Set-AuthoringAcl.ps1` works against the UNC path.

### 7.3 Signing keys: Azure Key Vault (outlook)

Key Vault is the intended long-term store and already the top disaster-recovery gap:

- one non-exportable EC P-256 key per team;
- **per-key** RBAC: Signers-T as Key Vault Crypto User, KeyAdmins-T as Key Vault Crypto Officer;
- soft delete and purge protection on;
- diagnostic logs for every sign operation.

It removes the file-copy risk entirely: an author can sign through the vault but can no longer take the key away.
It needs code: signing becomes a remote call, and the `Loaded` / `Unreadable` outcomes need a remote equivalent.

---

## 8. Lifecycle

- **Joiner.** Add the person to the directory groups first (Authors, plus Signers and KeyAdmins as needed), then
  create the user or assign team roles in the app. Without Authors membership, login says "You don't have access to
  this environment's user list".
- **Mover.** Adjust app roles and the matching groups in the same change. A role removed only in the app leaves the
  file access in place.
- **Leaver.** Remove the person from **the directory groups first**; that is the boundary. Then deactivate them in
  the app. If they were in any Signers or KeyAdmins group, rotate those teams' keys: a key they could read may have
  been copied (`docs/anti-tamper.md` §6, dual-key window).
- **Review.** Quarterly, compare `users.json` roles with group membership. Configuration Review and the drift
  warnings on Key Management and Environment Management cover the ACL side; group membership needs a directory
  report.

---

## 9. Review findings and gaps

| # | Finding | Recommendation |
|---|---|---|
| F1 | **Team separation and approval are workflow rules only.** Every author has Modify on every team folder in DataPath and in the distribution, because publishing writes into the target team. A member of Authors can, outside the app, change another team's content or publish without approval. Signed static teams are still protected by the key ACL; dynamic teams and unsigned teams are not. | Accept and state it, as here. Enforce `RequireSignedIndex=true` on the fleet once keys are provisioned. Use separate environments for populations that must not touch each other. |
| F2 | TeamAdmin sees the team's pending approvals (`GetPendingApprovalsForUserAsync` includes TeamAdmin), but `ApproveAssignmentAsync` / `RejectAssignmentAsync` require ContentApprover, so the buttons fail. | Decide one rule. Either let TeamAdmin approve (add it to the service check), or drop TeamAdmin from the pending list. |
| F3 | `CreateAssignmentAsync`, `CreateScheduleAsync` and `UpdateScheduleAsync` check only that someone is signed in. The role check lives in the pages. | Add service checks: ContentAuthor of the source team (or SystemAdmin) for assignments and schedule create; creator, ContentAuthor of the team or SystemAdmin for schedule update. Same pattern as `PresentationService`. |
| F4 | Index Management and Configuration Review are gated only on the page. `RegenerateAllIndexesAsync` and the repair pass have no service check. | Add `RequireSystemAdmin` in those service methods. |
| F5 | `Set-AuthoringAcl.ps1` gives `TeamSigners[team]` Modify. Signing only needs read. Modify lets any signer delete or overwrite the key, which orphans every device that trusts it. With broad cross-team publishing, signers ≈ all authors (§3.3). | Split into `-TeamSigners` (Read & Execute) and `-TeamKeyAdmins` (Modify). If signers end up being all authors anyway, say so: the key ACL then protects against non-authors and against exfiltration only by people outside Authors, and Key Vault (§7.3) becomes the real fix. |
| F6 | Any signed-in user can run statistics (and enable "process on open"), which reads, moves and deletes in `{dist}\uploads` and `telemetry\`. That requires Authors to have Modify there. | Keep it, since authors already have Modify on `{dist}`, or restrict the button to SystemAdmin and remove Authors from `uploads\` and `telemetry\`. |
| F7 | When telemetry upload is on in Azure mode, the fleet SP needs Contributor on the content container. One SP serves the whole fleet, so local admin on any device (certificate extraction) gives write to every team's content, which breaks dynamic-team trust. | Upload telemetry to a separate container and give the SP Contributor only there (needs a NewsService setting for the telemetry container), or keep `Telemetry:UploadEnabled=false` in Azure mode. |
| F8 | `Set-AuthoringAcl.ps1` provisions `secure`, `config`, `users.json` and `audit` only. DataPath root and team folders, `statistics\`, `{dist}` and `uploads\` are left to whoever created them. | Extend the script (or add a sibling for `{dist}`) to apply section 6.2, with a `-FleetGroup` parameter. Same rules: 64-bit, elevated, `-WhatIf`, explicit exit code, one real elevated run. |
| F9 | The default locations put `secure\`, `audit\` and `_distribution\` inside DataPath. Fine with the root ACL in §6.2, but that ACL is not applied by anything today (F8), and the fleet would need access to the authoring share to read `_distribution`. | Production: `SecurePath` and `Distribution.LocalPath` on their own shares (§6.1). |
| F10 | Edit of a presentation is creator-only, but delete is allowed to any ContentAuthor of the team. | Intentional? If so, document why. Otherwise align: either both creator-only or both team-wide. |
| F11 | No separation of duties: a user who is ContentAuthor of the source and ContentApprover of the target can approve their own assignment. | If four-eyes is required, reject approval when `ApprovedBy == CreatedBy` (SystemAdmin exempt or not, decide). |
| F12 | Template leftovers `/counter` and `/weather` are reachable by URL. | Remove. |
| F13 | `docs/newscentral-spec.md` "Role Model" lists only SystemAdmin and ContentAuthor. | Replaced by a pointer to this document. |
