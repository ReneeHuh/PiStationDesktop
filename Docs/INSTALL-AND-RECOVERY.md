# Install, use, and recover Pi Station

Pi Station currently targets **Windows 11 x64** (Windows build 22000 or later).

## Run from this checkout

1. Install the .NET SDK pinned in `global.json` and the WinApp CLI version in `tests/PiStation.UiTests/tool-versions.json`.
2. Run `dotnet build PiStationDesktop.slnx -c Debug`.
3. Run `winapp run src/PiStation.App/PiStation.App.csproj --configuration Debug --arch x64 --property Platform=x64 --no-build`.
4. Install Pi 0.84.4 or later. In Pi Station, open **Settings → Pi and runtime**, choose the executable or enter its path, then **Save and connect**. An empty path uses automatic discovery. The app can open and manage projects and drafts before Pi is available.
5. Add a local project, create a thread, and send a prompt. Git changes, files, terminals, and preview are in the workbench. **Changes → Pull requests** opens hosting review beside the coding workflow.

## Pi extensions and skills

Open **Settings → Pi / runtime**. Keep discovery off to load the bundled desktop extensions and paths you explicitly add. Turn on **Discover installed Pi extensions** to let Pi discover installed resources under its own project trust policy. Inspect and save project trust decisions in **Settings → Pi resources**; Pi's trust extensions still determine the effective policy. Additional extension paths must be absolute paths to existing files or directories. These extensions execute code with the Pi process's permissions, so add paths you trust.

Choose **Save and connect**, then **Restart selected thread** once its turn and interactions have finished. New runtimes use the saved configuration immediately. Removing an explicit path or turning discovery off takes effect at that restart. The bundled browser, resource-management and planning extensions remain available. Settings are saved together in `pi-runtime.json` under the app data root; older `pi-executable.txt` settings are read when the new file is absent.

Use the skill picker or write `Please use $skill:review on these changes.` Inline and multiple skill mentions resolve against Pi's discovered skills and include their instructions in the model request. Code examples, escaped mentions, and quoted context do not invoke skills. Missing or ambiguous skills produce an error before the draft is consumed. Native `/skill:name` prefixes and prompt templates remain supported by Pi.

The thread's **Pi extensions** panel displays notifications, status and text widgets. Extension title updates label that panel. Suggested editor text requires **Insert into draft**, which appends to your existing text. Switching threads does not automatically insert suggestions. These text-based RPC features do not support arbitrary terminal UI components.

## Manage Pi resources and providers

Select an idle thread, open **Settings → Pi resources**, and choose **Refresh resources**. The panel shows the active project and Pi configuration directories, resource paths and sources, current trust, provider credential status, and configuration/startup messages. Disabled resources remain listed. A saved enable/disable setting describes the next launch; runtime confirmation means Pi reported a command, tool, skill or context file. Extensions with no registered features may have loaded without being confirmed.

Use **Enable / Disable** for a discovered resource, then **Restart selected thread** to apply the saved setting. Package filters and other settings are preserved. Explicit extension paths are edited under **Pi / runtime**. **Trust project** allows Pi to load project-local settings and executable extensions; **Do not trust** saves the opposite decision for this folder. Restart applies the decision subject to Pi's trust extensions. This is separate from trust for repository setup scripts.

**Open Pi login / logout** opens Pi in an integrated PowerShell terminal using the configured executable. Enter `/login` or `/logout` and follow Pi's prompts. Exit Pi, restart the thread, and refresh the panel. Credential status means Pi found a credential source; it does not certify that the account can complete a model request. Pi handles credentials in its own login flow.

Expand **Add or update a custom model** to save a provider ID, model ID, endpoint and API to Pi's `models.json`. The endpoint and API affect all models with that provider ID. Other providers and existing model fields are retained; comments and formatting are rewritten. Supply an environment-variable **name**, then set its value before launching PiStation; leave the field blank to preserve an existing credential configuration. **Local server needs no key** is available for localhost endpoints. Restart and select the model in the composer after saving. Stale edits and malformed JSON require refresh or file repair before another save.

The resource and package terminal buttons open Pi's own `config` and `list` commands. In the package terminal, `pi install <source>`, `pi remove <source>` and `pi update` use the same configured runtime. The native panel refreshes metadata without installing packages. General runtime arguments, per-runtime environment editing and automatic Pi installation/update remain future setup work.

## Import, fork and export Pi sessions

Open **Settings → Sessions**, or search for **Manage Pi Sessions** in the command palette. Select the project that should own the new thread first.

**Import:** refresh Pi's default session folder, enter/choose another folder, or use **Import JSONL file…**. Select a listed session and choose **Import selected session**. PiStation copies the full session tree under its own data directory with a new identity. The CLI source remains unchanged. Current Pi v3 sessions are supported, up to 64 MiB and 50,000 entries; open older sessions in a current Pi version before importing. Unreadable/unsupported files are counted in the browser result. A folder scan examines at most 500 files within four subfolder levels.

**Fork:** select an idle thread and choose **Refresh session tree**. Active and alternate branches show their entry and parent identities. Select a completed assistant response, then **Fork after selected response**. The new thread contains the path through that response. **Copy whole session** retains every branch. Both actions use the selected project's current local workspace; they do not copy a worktree or rewind files. Model and thinking settings come from the copied history, and the new draft starts empty. The original thread keeps its draft. An unavailable source model is reported by the normal Pi configuration/recovery controls.

**Export:** **Export Pi JSONL…** saves the full tree, including embedded image data. **Export readable HTML…** saves a standalone, escaped text transcript of the active branch, including tool/thinking text. HTML represents images as placeholders; use JSONL to retain image data. Linked workspace files remain external to both formats. Choose a destination outside PiStation's application data.

Inspect, copy/fork and export require an idle thread with a saved session. A stale tree requires refresh before a new fork. If a copy request loses its response, retrying the unchanged action checks the saved operation outcome. Created threads survive relaunch and appear in the selected project's thread list.

## Plan, approve and execute

Expand **Plan** above the composer and choose **Plan mode**, then send your task in the composer. Pi can use dedicated file read/search tools to propose a numbered plan. Shell commands, writes, browser actions and other tools are blocked by the planning policy. Trusted extensions themselves still have the Pi process's permissions; this is not an OS sandbox.

Review or edit the numbered list, choose **Save plan**, then **Approve and execute remaining steps**. Approval enables the normal Pi tools for the run and preserves your unsent composer draft and attachments. **Stop** interrupts execution. The panel tracks the agent's reported completed steps; remaining steps require another approval after a paused run. **Export** saves the reviewed plan and progress as Markdown.

Plans and progress survive thread switches, reconnect and restart. Execution interrupted by process shutdown reopens paused. **Reload saved plan** replaces unsaved editor changes; stale saves require reviewing the current version first. After completion, planning restrictions remain active until **Return to normal tools**. Plan text supports up to 32 KiB and 100 steps. Back up the `plans` directory together with the sessions and host database.

## Organize and review work

- Use the sidebar's **Active / Settled / Snoozed / Archived** selector. A completed response leaves its task active. Settle tasks explicitly when the work is finished. Snoozes wake at their expiry on the next inbox refresh (within about 30 seconds while connected).
- Expand project groups to scan tasks across projects. Select a project name to work in that project; task actions remain available in the selected project's list. Group expansion is remembered locally.
- Review patches in unified or split mode. Select lines and choose **Add selection** to attach a review comment with a source path and line range.
- Right-click selected Markdown text to quote or cite the selection. Click a context chip to return to its source. Relative workspace links and links with `#L12` or `:12` open Files at that line.
- A stash saves the complete draft: prompt, attachments, and context chips. Restore it into an empty draft in the same project. Stashes retain their attachment files until the stash and any restored drafts no longer reference them.
- The transcript follows output while at the bottom. Scroll up to read history; use **Jump to latest** to follow again. Scroll positions are retained per thread while the window remains open.

## Recover and reconnect

- If Pi is missing or moved, update its executable path in Settings and retry. A running task keeps its existing process until restarted.
- Idle Pi processes stop after 30 minutes by default. Sending another prompt resumes the saved session. Running turns and pending approvals/questions are protected from idle shutdown.
- If a connection or process fails, use the recovery controls and inspect the saved command outcome before retrying uncertain work.
- Hosting writes have durable operation IDs. **Settings → Source-control hosting → Recent hosting operations** shows their outcomes. An uncertain operation is not automatically resent; inspect the remote provider before beginning another write.
- Costs are shown only when Pi supplies a cost. A missing cost remains unavailable, including in aggregates containing unpriced turns.

## Back up and update

Close Pi Station, then back up the complete `%LOCALAPPDATA%\PiStationDesktop` directory and your project repositories. Keep `host.db`, sessions, plans, attachments, worktrees, and settings together. A `--data-root` launch uses that directory instead. Restore with the app closed.

Run `pwsh ./Build-Release.ps1` to generate an unsigned development MSIX and checksum in a fresh folder beneath `artifacts/release`. Unsigned packages are build artifacts; they need signing before normal installation.

Production publisher identity, signing, and the HTTPS update feed are [TODO later](RELEASE-TODO.md). Once installed through a configured `.appinstaller` feed, **Settings → Updates → Check for updates** uses Windows' update-availability API and can open App Installer. Direct development installations report that no feed is configured.

## Hosting support

GitHub supports repository publishing and PR actions. GitLab supports MR listing, creation, comments, labels, reviewers, approval, merge, close, and reopen. Azure DevOps supports PR listing/creation, reviewers, votes, merge, close, and reopen. Unsupported operations are disabled or rejected before dispatch. GitLab/Azure publishing and a verified Bitbucket integration remain future work; no generic `bb` executable is assumed.

Authenticate with each provider's own CLI. Pi Station checks authentication when detecting a repository; the presence of a CLI alone is labeled as installation status. GitLab filters follow its [documented list flags](https://docs.gitlab.com/cli/mr/list/), GitHub authentication follows [`gh auth status`](https://cli.github.com/manual/gh_auth_status), and Azure actions follow the [Azure Repos CLI reference](https://learn.microsoft.com/en-us/cli/azure/repos/pr?view=azure-cli-latest).
