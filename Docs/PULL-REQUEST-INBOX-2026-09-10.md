# Cross-repository pull request inbox

Based on `89de757`, using the existing protocol-58 list/review contracts. HOST-10 adds a combined inbox for project repositories in connected PiStation windows, including local and remote connection profiles.

## Using the inbox

Open **Settings → Source control → Pull requests and review → All repositories**. The inbox discovers projects from each connected window. Choose an environment/profile, provider, repository URL fragment, state, text, author, drafts, labels, or GitHub-specific involvement/review/check filters, then select **Apply / refresh**.

Every row shows its environment, project, repository, PR number, title and state. **Review selected** opens the existing detailed review workspace with that row's captured connection and project root. **Provider website** opens its HTTPS page. GitHub's **Review in Pi** creates/reopens the isolated review worktree and selects its thread in the originating environment window. Creating a new PR and linking to the current task remain in the selected-project dialog.

The review uses the originating profile's existing durable draft store and operation receipts. The selected project in the inbox window does not determine the review's write destination. Different profiles reaching the same environment retain distinct connection and draft identities. Another review already open for that source must close first. A disconnected or replaced source cannot be used for a write; read-only connections disable review submissions and management actions.

## Paging, filtering and limits

- Repository reads run with a maximum concurrency of four. Successful repositories remain available if another fails. **Retry failed repositories** resumes the failed page without reloading successful repositories; project-discovery failures use **Apply / refresh**.
- Each repository retains its own continuation. An empty page after local filtering can still offer **Load more**. Rows repeated within a project's pages are updated, deduplicated and sorted by recency. Separate project checkouts and connection profiles retain separate rows and draft targets.
- A changed remote identity or invalid/non-advancing continuation stops that repository until refresh. Refresh results are immutable, and canceled/older UI requests cannot replace newer results.
- GitHub uses its existing provider search, account involvement, author, review, checks and label filters. Its existing 1,000-result search limit remains explicit.
- GitLab and Azure use provider state paging and local matching of title/number/branches, exact author and drafts on loaded pages. GitLab also supports local labels; required labels are ANDed and excluded labels exclude any match. Azure PR labels are reported as unsupported, rather than producing false empty results. Azure author matching prefers `uniqueName`. These providers do not silently approximate account involvement, `@me`, review decisions or checks: selecting those filters reports the affected repository as unsupported.
- The inbox loads up to 100 project repositories and ten pages per repository, with notices when limits are reached. It does not imply that a locally filtered first page searches all history. It does not open disconnected environments automatically or discover repositories outside registered projects.
- GitLab listing explicitly names its host/repository. Azure explicitly names organization, project and repository, disables CLI defaults, includes all statuses for the All filter, and returns human-facing PR links. Malformed list payloads are failures rather than false empty results.

## T3 comparison

Reference checkout: `C:/Users/Bacon21/Workspace/t3code`, commit `0a590fa01af66ec135d2ebf2d5542b08a37dc275`.

- T3's [list merging](../../t3code/apps/web/src/components/pullRequest/pullRequestList.logic.ts) carries the environment on every row and keeps viewer identities and continuations separate. PiStation captures the connection profile, project target and full hosting repository identity.
- T3's [repository orchestration](../../t3code/apps/server/src/pullRequest/PullRequestService.ts) combines independent errors/cursors with bounded concurrency and optional cross-repository provider searches. PiStation uses four concurrent calls through its existing per-repository protocol instead of adding a provider batch-search protocol.
- T3's [Azure adapter](../../t3code/apps/server/src/pullRequest/AzureDevOpsPullRequestProvider.ts) explicitly lacks free-text server search. PiStation also distinguishes loaded-page filtering from provider search. T3's GitLab search and account-based filtering are broader; this batch does not claim identical provider filter coverage.
- PiStation retains its durable local review drafts and operation recovery. The inbox does not adopt T3's in-memory review-store lifetime.

Implementation: [inbox paging/filtering](../src/PiStation.ClientRuntime/PullRequestInbox.cs), [connected-window routing](../src/PiStation.App/ViewModels/ShellViewModel.PullRequestInbox.cs), [native inbox/review dialogs](../src/PiStation.App/Views/ShellPage.PullRequestInbox.cs).

## Verification

The initial runtime build passed. The first gate invocation used invalid suite names and ran no tests. The next build found a constant-array analyzer violation in a test; it was corrected. The following focused gate passed **83 tests** (26 ClientRuntime, 57 Host), with zero failures/skips and a solution/WinUI build with zero warnings/errors: [summary](../TestResults/code-Debug-20260910-225025-eceb8258/summary.json), [log](../TestResults/pr-inbox-gate-3.log). That gate preceded the final transport, read-only, source-profile identity and presentation refinements.

The broader affected review gate passed **248 tests** (107 ClientRuntime, 131 Host, 10 Protocol), with zero failures/skips and a clean solution/WinUI build: [summary](../TestResults/code-Debug-20260910-225329-d539f724/summary.json), [log](../TestResults/pr-inbox-final-gate.log). Coverage includes independent failures/retries, empty filtered pages, duplicate and cross-environment identities, changed remotes, invalid cursors, cancellation, bounded concurrency/pages, scoped CLI destinations, malformed payloads, local/TLS inbox reads and read-only review denial. This gate preceded the final Azure-label capability/copy correction.

After that correction, the final solution/WinUI build passed with zero warnings/errors, and all **15 focused inbox tests** passed with zero failures/skips: [summary](../TestResults/code-Debug-20260910-225826-6d7428c6/summary.json), [log](../TestResults/pr-inbox-final-label-gate.log). No functional changes followed this gate.

Static visual contracts passed for 24 states, four responsive layouts and three text scales: [log](../TestResults/pr-inbox-visual-contract-final.log). These check source structure, not rendered controls. Native interaction and fresh authenticated-provider acceptance remain pending; live sign-in remains skipped at the user's request, and the native automation helper was unavailable in the preceding batch. No new native pass is claimed.
