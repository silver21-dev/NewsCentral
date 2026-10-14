# NewsViewer scheduled tasks

Two Task Scheduler definitions that launch NewsViewer in the logged-on user's session. They are the
**whole** launch surface: there is no HKLM Run entry (logon is a trigger of the Launch task).

**The installer owns the tasks; NewsService only verifies them.** NewsService never creates, modifies,
repairs or deletes a scheduled task. It embeds these two files (linked at build time, never copied) so it can
build the expected definitions and report drift (`ViewerTasks:Mode`, `VerifyOnly` by default). The tasks are
registered by the **registration script, a build artifact**: `NewsService.exe --emit-task-script
<package-staging>\Register-NewsViewerTasks.generated.ps1` runs on the build machine from the publish output,
embeds these XMLs as base64 of the exact file bytes, bakes in `Company`, the install path and the event
log/source, stamps each task with `[nc-hash:<sha256>]` at the end of its Description, and ships in the
package for the installer to run (docs/packaging.md). These files are the only task definitions - nothing is
generated in code. `scripts/Register-NewsViewerTasks.ps1` is the dev/pilot equivalent. Behavior of the
launches themselves (decision rules, notification) is in `docs/newsviewer-spec.md`; installer requirements
are in `docs/packaging.md` ("NewsViewer launch surface").

| File | Task | Trigger | Arguments |
|---|---|---|---|
| `NewsViewer-Launch.xml` | `\{Company}\NewsCentral\NewsViewer Launch` | **logon** of any user (15 s delay so the shell is up); session state change: **unlock**, **RemoteConnect** (RDP connect / reconnect), **ConsoleConnect** (fast user switching back to a local session) | none |
| `NewsViewer-ContentArrived.xml` | `\{Company}\NewsCentral\NewsViewer Content Arrived` | event: log `Application`, provider `NewsService`, **EventID 2001** (ContentChanged, once per sync cycle) | `--trigger content` |

`NewsViewer Launch` replaces the legacy Workstation Unlock task (`\{Company}\NewsCentral\NewsViewer`),
which must be removed so an unlock does not start NewsViewer twice.

## `--trigger content` and the idle check

The logon/unlock/connect task starts NewsViewer because the user is present by definition. The content task
fires whenever NewsService accepts new content, whether or not anybody is at the keyboard, so it passes
`--trigger content`, which switches on the **idle check**: if the last user input was more than two
minutes ago, NewsViewer exits without showing anything and the content is picked up by the next unlock,
connect or content event. Without the argument the idle check is skipped.

## Principal and settings (both tasks)

| Setting | Value | Why |
|---|---|---|
| Principal | group SID `S-1-5-32-545` (BUILTIN\Users), `LeastPrivilege` | Runs in whichever user's session fires the trigger, without elevation. Must be the **SID**: the group name "Users" is localized and fails to resolve at import on non-English Windows. Elevation would detach the process from the user's desktop and break the wallpaper style re-assert. |
| `MultipleInstancesPolicy` | `Parallel` | One instance per logged-on session (RDS hosts, fast user switching). Duplicates *within* a session are handled by NewsViewer's own single-instance mutex, which is session-local (`Local\NewsCentral.NewsViewer`). `IgnoreNew` would let one user's running poster block every other session. |
| `ExecutionTimeLimit` | `PT0S` (none) | An expanded poster with a stopped countdown, or a poster with auto-close disabled, may legitimately stay open for hours; the default 72 h limit would kill it. |
| `StartWhenAvailable` | `false` | A missed trigger is not replayed later: the next unlock / connect / content event covers it, and a replay at an arbitrary moment would pop a poster in the user's face. |
| Logon trigger `Delay` | `PT15S` | At logon the trigger fires while the shell is still starting; the delay lets the desktop come up before NewsViewer shows a window and re-asserts the per-user wallpaper style, both of which need it. 15 s is a margin chosen by judgement, not a measured value - raise it for slow profiles (roaming profiles, VDI) if a first launch lands on a half-loaded desktop. Only the logon trigger has a delay: unlock and connect events happen when the shell already exists. |
| Battery / network / idle conditions | all off | The task must run on laptops on battery and with no network. |

## Placeholders

Both files carry the `{Company}` placeholder in the task's own path (`RegistrationInfo/URI`, which places
the task in the `\{Company}\NewsCentral\` Task Scheduler folder, next to the legacy task) and in the install
path:

```
URI:               \{Company}\NewsCentral\NewsViewer Launch   (or ... Content Arrived)
Command:           "C:\Program Files\{Company}\NewsCentral\NewsViewer\NewsViewer.exe"
WorkingDirectory:  C:\Program Files\{Company}\NewsCentral\NewsViewer
```

The task name actually registered is the one passed to `schtasks /Create /TN`; keep it equal to the URI.

The generated registration script writes the canonical path as the unexpanded token
`%ProgramFiles%\{Company}\NewsCentral\NewsViewer\NewsViewer.exe` (and the same token in `WorkingDirectory`), so
the machine that registers the task expands it, not the machine that built the script. Task Scheduler expands
environment variables in `Command` and `WorkingDirectory` when it runs the task and keeps the token in the
stored XML (verified on Windows 11: a task with `%SystemRoot%` in `Command` and `%ProgramFiles%` in
`WorkingDirectory` ran with the working directory `C:\Program Files`). A path given with `--newsviewer-path` is
written literally.

The registration script (generated, or `Register-NewsViewerTasks.ps1`) substitutes the real company name, or the actual
folder for a non-standard location such as a dev box. The event subscription in
`NewsViewer-ContentArrived.xml` carries the NewsService log name and source (`Application` /
`NewsService`); they **must match** NewsService's `Logging:EventLog:LogName` / `SourceName`, or the task
never fires.

## File format

UTF-16 LE with BOM and CRLF line endings, the encoding `schtasks /create /xml` accepts reliably.
`.gitattributes` marks `deploy/scheduled-tasks/*.xml` as `binary` so Git never normalizes or re-encodes
them. Edit them with an editor that preserves the encoding, and verify with
`git diff --stat` (a re-encoded file shows as a whole-file change).
