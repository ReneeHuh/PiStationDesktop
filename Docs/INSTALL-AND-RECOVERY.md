# Install, use, and recover Pi Station

Pi Station currently targets **Windows 11 x64** (Windows build 22000 or later).

## Run from this checkout

1. Install the .NET SDK pinned in `global.json` and the WinApp CLI version in `tests/PiStation.UiTests/tool-versions.json`.
2. Run `dotnet build PiStationDesktop.slnx -c Debug`.
3. Run `winapp run src/PiStation.App/PiStation.App.csproj --configuration Debug --arch x64 --property Platform=x64 --no-build`.
4. Install Pi 0.84.4 or later and authenticate a model provider using Pi's own CLI. In Pi Station, open **Settings → Pi and runtime**, choose the executable or enter its path, then **Save and connect**. An empty path uses automatic discovery. The app can open and manage projects and drafts before Pi is available.
5. Add a local project, create a thread, and send a prompt. Git changes, files, terminals, and preview are in the workbench. **Changes → Pull requests** opens hosting review beside the coding workflow.

## Pi extensions and skills

Open **Settings → Pi / runtime**. Keep discovery off to load only the bundled browser extension and paths you explicitly add. Turn on **Discover installed Pi extensions** to let Pi discover installed resources under its own project trust policy. PiStation does not override that policy; configure project trust with Pi's CLI. Additional extension paths must be absolute paths to existing files or directories. These extensions execute code with the Pi process's permissions, so add paths you trust.

Choose **Save and connect**, then **Restart selected thread** once its turn and interactions have finished. New runtimes use the saved configuration immediately. Removing an explicit path or turning discovery off takes effect at that restart. The bundled browser extension remains available. Settings are saved together in `pi-runtime.json` under the app data root; older `pi-executable.txt` settings are read when the new file is absent.

Use the skill picker or write `Please use $skill:review on these changes.` Inline and multiple skill mentions resolve against Pi's discovered skills and include their instructions in the model request. Code examples, escaped mentions, and quoted context do not invoke skills. Missing or ambiguous skills produce an error before the draft is consumed. Native `/skill:name` prefixes and prompt templates remain supported by Pi.

The thread's **Pi extensions** panel displays notifications, status and text widgets. Extension title updates label that panel. Suggested editor text requires **Insert into draft**, which appends to your existing text. Switching threads does not automatically insert suggestions. These text-based RPC features do not support arbitrary terminal UI components.

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

Close Pi Station, then back up the complete `%LOCALAPPDATA%\PiStationDesktop` directory and your project repositories. Keep `host.db`, sessions, attachments, worktrees, and settings together. A `--data-root` launch uses that directory instead. Restore with the app closed.

Run `pwsh ./Build-Release.ps1` to generate an unsigned development MSIX and checksum in a fresh folder beneath `artifacts/release`. Unsigned packages are build artifacts; they need signing before normal installation.

Production publisher identity, signing, and the HTTPS update feed are [TODO later](RELEASE-TODO.md). Once installed through a configured `.appinstaller` feed, **Settings → Updates → Check for updates** uses Windows' update-availability API and can open App Installer. Direct development installations report that no feed is configured.

## Hosting support

GitHub supports repository publishing and PR actions. GitLab supports MR listing, creation, comments, labels, reviewers, approval, merge, close, and reopen. Azure DevOps supports PR listing/creation, reviewers, votes, merge, close, and reopen. Unsupported operations are disabled or rejected before dispatch. GitLab/Azure publishing and a verified Bitbucket integration remain future work; no generic `bb` executable is assumed.

Authenticate with each provider's own CLI. Pi Station checks authentication when detecting a repository; the presence of a CLI alone is labeled as installation status. GitLab filters follow its [documented list flags](https://docs.gitlab.com/cli/mr/list/), GitHub authentication follows [`gh auth status`](https://cli.github.com/manual/gh_auth_status), and Azure actions follow the [Azure Repos CLI reference](https://learn.microsoft.com/en-us/cli/azure/repos/pr?view=azure-cli-latest).
