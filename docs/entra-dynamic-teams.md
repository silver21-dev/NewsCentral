# Entra Dynamic Teams

**Status:** Implemented (NewsService-only, behind `Entra:Enabled`, default `false`). Structurally
complete and offline/fake-tested. **Live validation on an Entra-joined box is still pending** — see
"Status and unproven paths" below.

A machine can be subscribed to any number of dynamic teams, each produced by a named **instance** of
one of two **sources**: named **attribute schemes** and named **group-team instances**. This document
covers both under the shared instance model. For registry/appsettings layout and worked examples see
`docs/configuration.md` → "Entra device team resolution"; for Azure app-registration setup see
`docs/azure-setup.md`.

---

## The instance model

A dynamic team is produced by one configured **instance** of one **source**. Every instance — every
attribute scheme, every group-team pair — is independent: it resolves at most one team, and it carries
its own grace window.

Grace and identity are keyed `(Source, SourceId)`:

- `Source` is `Attribute` or `Group`.
- `SourceId` identifies which instance of that source produced the entry. For an attribute scheme,
  `SourceId` is the scheme name **as authored** in configuration. For a group-team instance,
  `SourceId` is **derived** from the instance's `InclusionGroup` (see "Group teams" below) — it is
  never authored directly.

The **configured instance set is read locally every cycle and is authoritative**, even when Graph is
completely unreachable. Concretely: if an instance is removed from configuration (a scheme deleted, a
group-team entry deleted, an `InclusionGroup` blanked out), its prior entry in `resolved-teams.json`
is dropped on the very next cycle **without grace** — grace is for "Graph didn't answer," not for
"this instance no longer exists." Renaming or retargeting an instance (changing a group instance's
`InclusionGroup`, which changes its derived `SourceId`) is indistinguishable from delete-then-create:
the old key is pruned without grace and the new key starts fresh, with no continuity between them.

Each poll cycle, NewsService:

1. Computes the active instance set from configuration (schemes, group instances) — this alone
   determines which `(Source, SourceId)` keys are eligible to exist in the output this cycle.
2. If nothing is configured (no schemes, no group instances), skips the Graph device fetch entirely
   and writes an empty resolved set.
3. Otherwise fetches this machine's Entra device object once, then resolves every active attribute
   scheme against it and evaluates every active group instance in one batched membership check
   (see "Graph mechanics" below).
4. Merges this cycle's per-instance outcomes with the previous `resolved-teams.json` through the
   grace state machine (`EntraResolvedTeamsMerger`, pure, unit-tested).
5. Applies `Entra:MaxDynamicTeams` (see "Bounding and selection") and writes the result atomically.

## Attribute schemes

Configured under `Entra:AttributeSchemes` — a dictionary of named schemes. Each scheme has:

- a **selector attribute** (`extensionAttribute1`..`extensionAttribute15`), and
- a **mapping table**: selector value → rule.

Resolution, per scheme: read the device's selector attribute; look up its value in the scheme's
mapping table (ordinal, case-sensitive) to get a rule; the rule is a `'-'`-joined, ordered list of
other `extensionAttribute1..15` names — **the rule may reference any of them except the scheme's own
selector attribute**. NewsService reads each referenced attribute's value in rule order, joins them
with `-`, and canonicalizes (lower-case; space/underscore → `-`; strip anything outside `[a-z0-9-]`)
into the team folder name.

Fails closed at multiple points, all producing "no team" for that scheme (not a crash, not a
fallback): a blank or malformed selector attribute name in configuration (must match
`extensionAttribute1`..`extensionAttribute15`); the device's selector value not present in the
mapping table; a rule referencing an out-of-range token or its own selector; any referenced attribute
being empty on the device.

**One team max per scheme.** `SourceId` for a resolved attribute team is the scheme name exactly as
authored in configuration (the dictionary key / registry subkey name) — must match `[A-Za-z0-9._-]+`;
an invalidly-named scheme is skipped (logged at `Warning`) and is not part of the active instance set.

## Group teams

Configured under `Entra:GroupTeams` — a dictionary of named instances (`Instances`) plus one
fleet-wide `ExclusionGroup`. Each instance has an `InclusionGroup` (required) and an optional
per-instance `ExclusionGroup`.

The membership decision, per instance:

```
team ⇔ inInclusion ∧ ¬inInstanceExclusion ∧ ¬inGlobalExclusion
```

— the device must be a transitive member of the instance's `InclusionGroup`, and must **not** be a
transitive member of either the instance's own `ExclusionGroup` (if set) or the fleet-wide
`ExclusionGroup` (if set; see "The global exclusion fails closed" below).

**The instance id is derived, not authored — and so is the team folder name.** Both are
`Canonicalize(InclusionGroup)` — the exact same canonicalization used for attribute teams (lower-case;
space/underscore → `-`; strip anything outside `[a-z0-9-]`). This means the grace-partition
`SourceId` and the published team folder name are, by construction, the identical string — they
cannot drift apart the way an independently-authored id could.

The dictionary key / registry subkey under `Instances` (e.g. `prague-its`) is an **operator-facing
label only**. It locates the instance in regedit/GPO and appears in log lines for diagnostics — it
never reaches `resolved-teams.json`, and it plays no role in matching or grace. Renaming the label
retargets nothing; changing `InclusionGroup` does (see "The instance model" above — that's a
delete-then-create). Recommend a short mnemonic label rather than restating the derived id.

**Collisions.** Two instances whose `InclusionGroup` values canonicalize to the same id collide —
this is possible even with visibly different `InclusionGroup` strings (case, spacing, punctuation all
fold together under canonicalization). The first instance by **label** (ordinal-ignore-case) wins and
stays active; the rest are skipped with a `Warning` naming both labels and the shared derived id.

**A blank `InclusionGroup` skips that instance** (logged at `Warning`) — it contributes no key, and
any team it may previously have produced is pruned without grace on the next cycle via the ordinary
"instance no longer configured" path. There is no separate explicit-removal code path for this case;
the active-instance-set mechanism already does the job.

### The global exclusion fails closed

`Entra:GroupTeams:ExclusionGroup` is a single, fleet-wide kill switch: membership in it suppresses
**every** group instance on the machine, regardless of source or how many instances are configured.
It is group-scoped — it never affects attribute schemes.

If the global exclusion's display name **cannot be resolved** (not found, or ambiguous — more than
one group with that display name), NewsService suppresses every group instance for that cycle. This
is deliberate: a suppression mechanism's safe failure mode is to keep suppressing, not to silently
stop. An exclusion that quietly went inert the moment its own name became unresolvable would be worse
than useless — it would look like it was still working. So the failure mode here is symmetric with
the mechanism's purpose: "can't confirm the kill switch is off" is treated the same as "kill switch is
on."

Because the blast radius is fleet-wide — a name typo or a group rename anywhere upstream can go dark
on every group instance across every machine that shares this configuration — NewsService logs
**exactly one unconditional `Error` line per cycle** naming the group, never gated on change and never
emitted per-instance. This is the one Entra log line in this feature that is deliberately loud by
design.

**This is distinct from the everyday case.** A global exclusion whose name resolves fine, where the
device simply isn't a member of it, is normal operation — no suppression, no log line beyond the usual
per-instance `Debug` outcome. Suppression is specifically the *unresolvable name* case, not "the
exclusion group has no members here."

## Graph mechanics

**One batched membership check per cycle**, covering every active group instance. NewsService
collects the full set of distinct group display names referenced that cycle — every active instance's
`InclusionGroup`, every active instance's non-blank per-instance `ExclusionGroup`, and the global
`ExclusionGroup` if set — resolves each name to an object id, and evaluates the device's transitive
membership across all of them in one pass. This costs **one `checkMemberGroups` Graph call per 20
distinct group ids** (the Graph API limit) regardless of how many group instances are configured, not
one call per instance.

**Per-name resolution isolates failures.** Each requested name gets its own resolution status
(resolved / not found / ambiguous). A single bad name — one instance's inclusion group renamed
upstream, say — produces a clean "no team" only for the instance(s) referencing that name; sibling
instances referencing other, valid names are unaffected.

**A transport-level failure (unreachable, or a 403) fails the whole batch.** If any chunk of the
`checkMemberGroups` call throws, the entire snapshot for that cycle is treated as failed (classified
via `GraphFailureClassifier` — 403 is persistent, everything else transient) — NewsService never
returns partial membership. Partial membership would read as "the device is not a member of the
groups from the chunks that didn't complete," which could wrongly grant a team the device should have
been excluded from, or wrongly withhold one it should have received. A clean failure that graces (or,
for 403, removes) is the safe choice; a best-effort partial result is not.

**Name→id resolution is cached for the process lifetime.** A successful, unambiguous resolution is
cached and reused on later cycles without another Graph round trip. Not-found and ambiguous results
are never cached, so a subsequently-fixed group name (or display-name collision resolved) is picked
up on the next cycle without a service restart.

## Grace and failure classification

`GraphFailureClassifier` maps every Graph/HTTP failure into one of two buckets, applied uniformly
across both sources:

| Classification | Examples | Effect |
|---|---|---|
| **Persistent** | 403 (missing consent), device object not found, a group display name not found or ambiguous, an authoritative "no mapping"/"not a member" answer | Clean removal, no grace. A 403 additionally logs at `Error`. |
| **Transient** | Network/timeout/throttling/5xx, an unreachable device or group check | The affected instance's prior entry rides the grace window (`Entra:GracePeriodMinutes`, default 240) with `State = Grace`, then drops if Graph still hasn't answered by the deadline. |

Grace is evaluated **per instance** (per `(Source, SourceId)` key) — a Graph outage graces every
currently-active instance independently, each against its own `LastConfirmedUtc`; an instance that
was already stale before the outage does not get a fresh grace window just because a sibling instance
is also unreachable.

## Bounding and selection

**`Entra:MaxDynamicTeams`** (default `16`, `0` = no cap) bounds the total number of dynamic teams
written to `resolved-teams.json` across both sources combined, after the grace merge. If more teams
resolve than the cap, NewsService truncates in a deterministic order — every Attribute entry (by
`SourceId`, ordinal-ignore-case) before every Group entry (likewise by `SourceId`) — and logs one
`Warning` per cycle naming the configured cap, the count resolved, and the dropped ids. A dropped
entry's grace state is not preserved; if fewer teams resolve on a later cycle it reappears normally
with a fresh `LastConfirmedUtc`, not a resumed grace window. The cap exists because each dynamic team
costs NewsService one `index.json` fetch (plus any changed images) per sync cycle, and costs
NewsViewer one index parse per presentation-selection pass — the cost is linear in how many dynamic
teams a machine ends up subscribed to.

**Presentation selection remains single-winner, regardless of instance count.** NewsViewer picks
exactly one active News-of-the-Week assignment per launch — the one *newest to the team* (the later of
`PresentationLastModified` and the assignment's `PublishedDate`)
across **all** verified, schedule-active assignments from **every** subscribed team, static or
dynamic (see `docs/newsviewer-spec.md`). Subscribing a machine to N dynamic teams does not give it N
posters; it gives N publishers competing for the same one poster slot, and whichever is newest wins.
This is a governance and authoring consideration for whoever provisions a large `AttributeSchemes` /
`GroupTeams:Instances` set — worth knowing before provisioning it, not after: a machine picking up
many dynamic teams "for coverage" does not thereby see more content, it just widens the pool the
single winner is drawn from.

**Change-gated summary logging.** Each cycle logs one line summarizing the full written team set
(`Source/SourceId=TeamFolderName (State)` per entry, in the same deterministic order used for
truncation) — at `Information` when that set differs from the previous cycle (including the
transition to zero dynamic teams), at `Debug` otherwise. This is the primary operator-facing signal
for "what did Entra resolve this cycle"; per-scheme and per-instance outcome lines stay at `Debug`,
and every `Warning`/`Error` described above (invalid or colliding instances, unresolvable names, the
global-exclusion suppression, device-level failures) is unconditional and unaffected by this gating.

## Consumption is unchanged

Dynamic teams are ordinary teams to everything downstream of `resolved-teams.json`. `ResolvedTeamsReader`
reads the file into a flat set of team folder names (ignoring `Source`/`SourceId` entirely — they
exist purely to drive NewsService's own grace bookkeeping); `EffectiveTeams.Union` merges that set
with the static (registry) team list on both NewsService and NewsViewer; verification routes through
`SignatureGate.Verify` (the signed `KeyId` selects the configured registry key; team binding; see `docs/anti-tamper.md` §5) exactly as for static teams. NewsViewer is a **read-only** consumer of
`resolved-teams.json` — it never writes it. The authoring and signing sides are untouched by any of
this: a dynamic team's `index.json` is authored, published, and signed exactly like a static team's.

**Trust model.** Dynamic teams verify exactly like static ones: against the environment's flat registry key (`Signing\PublicKey` / `PublicKeyPrevious`), selected by the signed `KeyId`, with the index's `TeamFolderName` bound to the folder being synced. No key is delivered with the content (removed in M1), so a dynamic team needs the same registry key as a static one and rotates the same way.

See `docs/security.md` for the trust model and `docs/data-model.md` for the
`resolved-teams.json` schema.

This consumption symmetry did not hold until recently: both `NewsService/Worker.cs` and
`NewsViewer/Program.cs` carried a pre-Entra bootstrap guard that exited before the union was
computed whenever the **static** team list was empty, so a dynamic-only machine was inert on
whichever tier still had the guard (NewsService fixed in `f8cd54b`, NewsViewer in the following
commit) — worth knowing if you are reading an older description of this feature as always having
worked this way.

## Auth / Graph setup

Both sources share one app registration and credential with Azure Blob access
(`AzureCredentialFactory.Create`) — `AzureBlob:*` must be populated even when
`Repository:StorageMode = Share`, because the Graph reads reuse the blob credential. Admin-consented
application permissions: `Device.Read.All` (device read, both sources) and `GroupMember.Read.All`
(group name resolution and membership checks, group teams only — `Directory.Read.All` is a broad
fallback if `GroupMember.Read.All` proves insufficient). The device id is read locally and must be a
GUID or Graph is skipped entirely for that cycle. `Microsoft.Graph` v6 fluent SDK; the v1.0 Device
model has no typed `extensionAttributes`, so it is read from `AdditionalData` as a Kiota
`UntypedObject` (documented v1.0 pattern; the beta SDK is deliberately avoided). Graph rides the same
default .NET HTTP stack as blob — no app-specific proxy — except that `AzureBlob:UseWinHttpProxy`
(default off), when enabled, routes blob **and** Graph together through the machine WinHTTP proxy via
the shared `AzureProxyTransportFactory`. See `docs/azure-setup.md` for the full setup checklist
(app registration, RBAC, device/group provisioning) and `docs/configuration.md` for every
appsettings/registry key.

## Status and unproven paths

Structurally complete and offline-tested: the resolver, the grace merger, the group decision
predicate, the outcome mappers, the chunking/mapping helper, and the orchestrator are all pure or
fake-tested (`NewsCentral.Shared.Tests`, `NewsService.Tests`). **Live validation on an Entra-joined
box has not happened yet** — do not read anything in this document as claiming otherwise. The
environment-bound seams that remain unproven outside of fakes are: the local DeviceId read, the
device fetch HTTP round trip, and `EntraGroupClient`'s name→id resolution and `checkMemberGroups`
calls.

Operational prerequisites before enabling `Entra:Enabled` on real hardware: `Device.Read.All` consent
(plus `GroupMember.Read.All` if any group instance is configured), device `extensionAttributes`
and/or group membership provisioned across the target fleet, verified `%programdata%` cache ACLs, and
GPO deployment of the Entra registry configuration.

## Out of scope

- **On-premises Active Directory groups.** Only Entra (Azure AD) cloud groups are supported; device
  cloud-group membership is reachable only through Graph.
- Per-instance grace-period overrides (today `GracePeriodMinutes` is one value shared by every
  instance of both sources).
- Per-scheme / per-instance team-folder prefixes or naming policy beyond straight canonicalization.
