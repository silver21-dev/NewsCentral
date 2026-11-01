# NewsCentral — Disaster Recovery

What to back up on the authoring tier, and what to do when it is lost. Scope so far: the environment signing key.
(Other authoring data — DataPath content, `config\users.json`, the audit log — is covered by the normal backup of the
shares that hold them; see `docs/production-deployment.md`.)

## Environment signing key

**Back up `{SecurePath}\keys\environment-signing.json`** (default `{DataPath}\secure\keys\environment-signing.json`).
There is **one file per environment**. It holds all three slots: `current`, `next` (a prepared successor, if any) and
`previousPublicKey` (the public half of the key that was replaced, if any), each with its `createdAt`. It contains
**private keys**:

- Back it up to storage with the same access list as `SecurePath` itself (SYSTEM, Administrators, AdminsGroup), never
  to a location every author can read.
- Take a new backup after every Key Management action (Generate, Prepare, Discard, Activate, Retire): the file only
  changes then.
- Do not copy it into the GPO, a ticket or a chat. Only the **public** keys go to the fleet.

**If the file is lost and there is no backup**, the private key cannot be recovered (it exists nowhere else). Clients
keep their cached content, but nothing new can be signed with a key they trust. Recovery:

1. Restore access to `SecurePath` (permissions: `scripts/Set-AuthoringAcl.ps1`), then in Key Management **Generate** a
   new key (the page offers it when no file exists).
2. Deploy the new `PublicKey` to `NewsService\Signing` and `NewsViewer\Signing` and **remove** `PublicKeyPrevious`
   (the page shows the exact values).
3. Index Management → **Regenerate all teams**.

Until step 2 has reached a machine, that machine rejects every regenerated index (fail-closed,
`Signing:RequireSignedIndex`), so no new content flows to it; content it already cached is unaffected. There is no
dual-key window in this recovery, because the lost key cannot be used to sign a transition.

**If the file exists but is unreadable** (access denied, corrupt): do not generate over it. Key Management shows the
reason and offers no action; publishing is blocked and every existing index stays as it is. Restore access or restore
the file from backup.

## Old per-team key folders

Before M3 each team had its own key at `{SecurePath}\keys\<team>\team-signing.json` (and, before S02,
`{DataPath}\<team>\team-signing.json`). **Nothing reads those files any more and no code deletes them.** After the
environment key is in place, **delete the old `keys\<team>` folders and any `team-signing.json` under DataPath
yourself**: they hold private keys that are no longer needed, and any that were readable by authors should be treated
as exposed.
