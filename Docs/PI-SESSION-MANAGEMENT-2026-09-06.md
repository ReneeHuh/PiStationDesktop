# Pi session management

September 6, 2026. Implements the session import/fork/export milestone after commit `82f3396` and the [resource/setup follow-up](PI-RESOURCES-AND-SETUP-2026-09-06.md).

## Delivered behavior

**Settings → Sessions**, also available as **Manage Pi Sessions** in the command palette, now provides:

- A browser for Pi session files, with source project/path, title, entry count and modified time. Import a listed file or choose a JSONL file directly.
- Independent whole-session copies and forks after a completed assistant response, including points on alternate branches. Copies preserve the full tree; forks retain the ancestor path through the selected response. Tool calls and partial responses are not fork points.
- New thread/session identities under PiStation's data root. The selected project's current local files are used. Model/thinking settings come from the copied branch, independent of target project defaults. Originals retain their files and drafts; new drafts are empty.
- Branch-tree inspection with active-branch markers, parent identities and bounded previews. Active-branch statistics show message counts, model/thinking, tokens and cost. Missing usage remains unknown.
- Full JSONL export, retaining embedded images, and standalone HTML text export of the active branch. HTML escapes transcript content and includes tool/thinking text. Images are represented by a placeholder in HTML; external workspace file references are not bundled.
- Persistent import/fork operation records. Repeating the same request returns the same thread; reusing an operation identity for different content is rejected. Thread/configuration/receipt insertion is transactional, after an atomic session-file write. An interrupted write before the transaction can be retried against the same owned file.

Session operations require an idle runtime with no pending approval/question. A source revision mismatch prevents a stale fork. Invalid JSON, unsupported versions, duplicate/orphaned tree entries and malformed message/model structures are rejected before thread creation. Export uses a temporary file and replacement, outside app-owned data.

Session contracts were introduced in **v25**; the [plan follow-up](PI-PLAN-WORKFLOW-2026-09-06.md) advances the current protocol to **v26**. The current Pi v3 JSONL format is read without rewriting the source. Older versions require opening in a current Pi version first. Limits are 64 MiB/50,000 entries per session, 500 files within four directory levels per browser scan, and 5,000 displayed tree entries. JSONL export retains the entire validated file. This does not clone a worktree, rewind files, bundle linked attachments or switch the original thread to a different branch.

Implementation: [session reader](../src/PiStation.PiRpc/Sessions/PiSessionDocument.cs), [host workflow](../src/PiStation.Host/EnvironmentService.Sessions.cs), [transactional persistence](../src/PiStation.Host/Persistence/HostDatabase.PiSessions.cs), [view model](../src/PiStation.App/ViewModels/ShellViewModel.Sessions.cs), [usage guide](INSTALL-AND-RECOVERY.md#import-fork-and-export-pi-sessions).

## Validation

Debug and Release solution builds succeeded with **zero warnings and errors**. Each configuration has **234 passing distinct code tests**, including the isolated timing-related reruns described below:

| Suite | Debug | Release |
| --- | ---: | ---: |
| Protocol | 25 | 25 |
| PiRpc | 65 | 65 |
| Host | 96 | 96 |
| ClientRuntime | 44 | 44 |
| CommandSystem | 4 | 4 |

Evidence is under `TestResults/pi-sessions-debug` and `TestResults/pi-sessions-release`. Opt-in real-runtime tests are additional to these totals.

- Unit checks cover branch extraction, full-tree copies, model/thinking retention, invalid tree/version rejection, partial-turn rejection and escaped HTML for the active branch.
- Host checks exercise authenticated SignalR import, idempotent replay/conflicts, model/draft preservation, stale revisions, export, original-byte preservation and host restart. Invalid imports create no thread; existing thread identities cannot be reused by an import.
- Actual Pi **0.84.4** and **0.85.0** both resumed a copied full session and an earlier-response fork, continued each independently, and reopened the fork after runtime restart. The original file remained byte-for-byte unchanged. These checks use an isolated configuration, offline provider and temporary project; no authenticated provider requests were needed for this milestone. Final-source evidence: `TestResults/pi-sessions-debug/session-real-084-final.trx` and `session-real-085-package.trx`. The isolated npm installation of 0.85 is selected by its package directory. A trial using its npm-local `.bin/pi.ps1` shim failed discovery before launching Pi; resolving that shim layout is a separate locator follow-up.
- All six native WinUI checks passed with FakePi: import, selected-response fork, relaunch with separate drafts, source-byte preservation, JSONL export and HTML export through the Windows save dialogs. Screenshot validation and visual inspection passed. Evidence: `TestResults/pi-sessions-native/82df82366963416cbdc0c69466cadb42/result.json`, `pi-sessions.png` and `native-export.jsonl`/`native-export.html` in that directory.

Two existing timing-sensitive checks needed isolated reruns. In Debug, the Host checkpoint test timed out in baseline Git capture while native automation was running, and the PiRpc stop/retry test exceeded its one-second FakePi startup timeout before reaching the tested behavior. All 96 Debug Host tests passed in the isolated suite run. The PiRpc stop/retry test and all six session-reader cases passed together in `TestResults/pi-sessions-debug/PiRpc-focused.trx`; the preceding PiRpc run had passed its other 64 cases. In Release, 95 Host tests passed initially; the checkpoint test exceeded its shared 20-second deadline during Git recovery capture, then passed alone in seven seconds (`TestResults/pi-sessions-release/Host-checkpoint-focused.trx`). No timeout thresholds were changed.

## Reproduce

```powershell
dotnet build PiStationDesktop.slnx -c Debug -p:Platform=x64
dotnet test tests/PiStation.PiRpc.Tests/PiStation.PiRpc.Tests.csproj --filter 'Category!=RealPi&Category!=RealPiOffline'
dotnet test tests/PiStation.Host.Tests/PiStation.Host.Tests.csproj

$env:PISTATION_RUN_REAL_PI_OFFLINE = '1'
$env:PISTATION_PI_PATH = 'C:/path/to/pi.ps1'
dotnet test tests/PiStation.PiRpc.Tests/PiStation.PiRpc.Tests.csproj --filter 'FullyQualifiedName~RealPiSessionCopyTests'

pwsh tests/PiStation.UiTests/Invoke-PiSessionsSlice.ps1 -NoBuild -Capture
```

The PR runner invokes the native slice with required screenshot validation. Run the Host suite separately from native automation on a busy machine. Broader physical DPI, accessibility, very large-session performance and additional provider/session-format acceptance remain outside this focused milestone.

The [native plan/execute follow-up](PI-PLAN-WORKFLOW-2026-09-06.md) implements the next milestone. Actual subagent extension controls remain next, followed by PR hosted diffs and inline discussions. Advanced runtime setup, fresh-account onboarding and the rest of the [48-item parity backlog](FEATURE-COMPLETION-AUDIT-2026-09-05.md) remain.
