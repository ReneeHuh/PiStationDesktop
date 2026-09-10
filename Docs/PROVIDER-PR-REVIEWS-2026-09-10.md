# GitLab and Azure pull request reviews

Protocol 58, based on commit `a188e19`. This batch implements HOST-20, HOST-21, and HOST-22: GitLab detailed reviews, Azure's supported detail/activity view, and provider-specific advanced actions. Authenticated provider acceptance remains DEL-07; no live provider writes or sign-in were performed.

## Using the review workspace

Select a GitLab merge request or Azure pull request in **Settings → Source control**, then open its review. The existing GitHub review workspace remains available.

| Feature | GitLab | Azure DevOps |
| --- | --- | --- |
| Details | Title, description, branches, author, reviewers, labels, current revision | Title, description, branches, author, reviewers, current revision |
| Code/activity | Hosted patches, commits, current-head pipelines, discussions | Read-only conversation; open the provider website for diffs |
| Review | Persistent inline drafts, comment/approve submission, discussion replies and resolve/reopen | Provider website for review submissions and comment changes; existing simple approve/request-changes actions remain available |
| Editing | Title/description, draft/ready, own comments, label/reviewer removal | Title/description, draft/ready, reviewer removal |
| Advanced actions | Project-allowed merge methods, enable/disable auto-merge, rebase branch update, PR/comment reactions | Merge/squash, enable/disable automatic completion |

GitLab does not offer a request-changes review verdict in this adapter. Azure does not offer inline review, comment editing, reactions, branch updates, workflow approval, or revert through this detail view. The native UI hides these unsupported sections. Controls for supported actions are enabled according to loaded state, known permissions, pending operations, and the current revision. An explicit provider-website link remains available.

GitLab merge methods follow the project's merge and squash settings: semi-linear and fast-forward projects expose rebase; merge-commit projects expose merge. Squash requires a recognized enabling setting. Merging uses the project's configured strategy. If the source branch needs rebasing, use the separate branch-update action and refresh after GitLab finishes it. A successful rebase request confirms acceptance, not completion of GitLab's background rebase.

## Target identity, writes, and recovery

Review targets remain bound to a project/workspace, repository, PR number, and inspected head. The host checks provider repository identity, head, and base before reads finish and before each component of a write. The client rejects a detail response from another provider or hosting authority. Azure draft keys now include the organization as well as the project/repository path, preventing collisions between organizations with equally named repositories.

GitLab inline positions include the current base/head/start SHA and both paths, including the previous path of a renamed file. Context-line comments include both line coordinates; additions and deletions include the appropriate side. Unavailable, collapsed, or unreturned patches cannot receive inline comments. Approval and merge requests carry the inspected SHA; Azure merge completion carries the inspected source commit and does not bypass policy. Metadata writes compare the expected text/state before dispatch. Providers still enforce actual permissions; Azure's adapter does not pretend to have a complete permission oracle.

A GitLab review consists of separate writes: inline comments, the summary, then approval. Validation happens before the first write. Each confirmed component updates the returned progress (`CompletedSteps`, `TotalSteps`, and `LastCompletedStep`). If a later call fails, loses its response, or observes a changed revision, the durable result is `DispatchUncertain` with the last confirmed progress. Approval is never sent after an earlier component fails. Reusing the operation identity returns its receipt without replaying writes, including after reconnect. Inspect the provider and operation history before composing the remaining work; resending the entire review can duplicate confirmed comments.

Provider commands use argument lists and JSON bodies, including strings that look like flags, booleans, or numbers. GitLab names its hostname and encoded project path. Azure names the organization, project/repository identities, and API version; CLI operations disable inferred defaults. Azure JSON payload files are temporary and removed after dispatch. Raw provider failure output is not included in status or receipts. Existing host authorization restricts review/management writes to operate access.

Some provider APIs have no conditional-write primitive for text edits, discussion replies, rebase requests, or automatic-completion configuration. An immediate re-read reduces stale actions but cannot make a sequence of remote calls atomic. Automatic merge/completion follows the provider's branch and policy behavior after it is enabled. These are not guarantees that future branch updates cannot be merged.

## Pagination and incomplete data

GitLab files, commits, pipelines, and discussions use separate revision-bound continuations. Each section has at most 30 pages of 100 items and the host refuses invalid, cross-repository, or stale continuations. A page has a 20,000-line patch budget; excess patches are explicitly unavailable. Inline-write preparation searches at most 3,000 files. Long discussion comment lists are capped at 100 comments with a website notice. Failed optional reads retain other sections and report what could not be loaded.

Like T3, reaction reads use GraphQL to fetch PR/comment awards together. Comment reactions cover up to 100 displayed comments, walking at most ten pages of notes; individual award connections are bounded at 100. Incomplete reaction data disables the affected reaction controls rather than presenting an empty count as authoritative. Azure conversation is bounded at 100 threads and 100 comments per thread, with a website notice for additional activity. The selected Azure scope does not supply hosted patches, commit history, or checks in this view.

This work does not implement cross-repository browsing, provider account/repository catalogs, Bitbucket, new provider-host detection rules, Azure Server/on-premises collections, or GitLab/Azure Pi checkout-and-review threads. Those are separate from detailed hosted review support. Large-workload performance and native accessibility acceptance remain delivery work.

## T3 comparison and provider contracts

The local reference is T3 `0a590fa01af66ec135d2ebf2d5542b08a37dc275`, not a claim about current upstream parity.

- [GitLab provider](../../t3code/apps/server/src/pullRequest/GitLabPullRequestProvider.ts) declares separate diff, review, editing, reaction, and action capabilities. [Its CLI adapter](../../t3code/apps/server/src/pullRequest/GitLabPullRequestCli.ts) submits inline comments before the summary/verdict, uses GraphQL for bulk reaction reads, and exposes rebase as its only branch-update method. PiStation adds durable component progress and repeated revision checks to this mapping.
- [Azure provider](../../t3code/apps/server/src/pullRequest/AzureDevOpsPullRequestProvider.ts) declares a narrower metadata/conversation/action surface. PiStation follows that supported scope and keeps the pre-existing simple Azure voting actions.
- [T3's shared service](../../t3code/apps/server/src/pullRequest/PullRequestService.ts) checks capabilities and viewer permissions before dispatch. PiStation carries typed capabilities in review snapshots and applies them in the native presentation model and host dispatch.
- Official contracts checked: GitLab [discussion positions](https://docs.gitlab.com/api/discussions/#create-a-new-thread-in-the-merge-request-diff), [merge requests](https://docs.gitlab.com/api/merge_requests/#merge-a-merge-request), [approvals](https://docs.gitlab.com/api/merge_request_approvals/), and [glab API payloads](https://docs.gitlab.com/cli/api/); Azure [PR updates](https://learn.microsoft.com/en-us/rest/api/azure/devops/git/pull-requests/update?view=azure-devops-rest-7.1), [conversation reads](https://learn.microsoft.com/en-us/rest/api/azure/devops/git/pull-request-threads/list?view=azure-devops-rest-7.1), [CLI actions](https://learn.microsoft.com/en-us/cli/azure/repos/pr?view=azure-cli-latest), and [API invocation](https://learn.microsoft.com/en-us/cli/azure/devops?view=azure-cli-latest#az-devops-invoke).

Implementation: [provider reads/parsing](../src/PiStation.Host/SourceControl/SourceControlHostingService.ProviderReview.cs), [provider writes](../src/PiStation.Host/SourceControl/SourceControlHostingService.ProviderReviewWrites.cs), [capabilities](../src/PiStation.Protocol/Models/HostingCapabilities.cs), and [native review model](../src/PiStation.App/ViewModels/PullRequestReviewViewModel.cs).

## Verification

Provider traffic is exercised through isolated command fixtures; local Git repositories validate remote identity without contacting a provider. New tests cover provider capabilities, namespace/organization isolation, paged reads, partial activity, reaction bounds, rename/context positions, unsupported verdicts/actions, stale revisions, expected-text conflicts, author and discussion membership, project merge settings, progress/replay, cleanup of Azure payload files, local/HTTPS transport, read-only denial, and native presentation-model behavior.

- Initial solution build passed with zero warnings/errors: [build log](../TestResults/provider-review-build-1.log). A later fixture build caught a constant-array analyzer rule, corrected before the test gate: [fixture build log](../TestResults/provider-review-test-build.log).
- The first focused gate retained **216 passes and seven failures**: [summary](../TestResults/code-Debug-20260910-210141-2c565e50/summary.json). It exposed the missing client provider check and unsupported-method selection normalization; these were fixed. The other failures were an obsolete GitHub-only capability assertion and equality checks comparing deserialized collection references; those tests were corrected to compare serialized receipt content.
- Full affected regression passed: **1,066 passed, zero failed, one optional live-Pi-writer skip** (ClientRuntime 519, Host 484, Protocol 63). [Summary](../TestResults/code-Debug-20260910-210730-4fbea760/summary.json), [log](../TestResults/provider-review-regression.log). The solution/WinUI build passed with zero warnings/errors. This gate preceded the final bulk-reaction, pagination, merge-method, and UI visibility refinements.
- Final focused gate after all functional changes passed: **229 passed, zero failed/skipped** (ClientRuntime 93, Host 126, Protocol 10). [Summary](../TestResults/code-Debug-20260910-212438-7765cebb/summary.json), [final build/test log](../TestResults/provider-review-final-3.log). Its first two build attempts caught a return-type analyzer rule and a fixture projection naming error; both were corrected. [First final build](../TestResults/provider-review-final.log), [second final build](../TestResults/provider-review-final-2.log).
- The final solution/WinUI build passed with zero warnings/errors. Desktop and bundled SSH host/protocol assemblies match by SHA-256: [package check](../TestResults/provider-review-package-check.json).
- Static visual contracts passed on the final source: 24 states, four responsive layouts, three text scales. [Log](../TestResults/provider-review-visual-contract-final.log). This checks source contracts, not rendered controls.
- Native automation failed before app selection: `Computer Use native pipe is unavailable ... The system cannot find the file specified. (os error 2)`, including retry and session reset/reinitialization. No native screenshot, keyboard, screen-reader, or rendered-dialog acceptance pass is claimed. The previously requested instruction to continue without desktop access was respected.
- Fresh provider authentication and authenticated writes remain unverified, as requested. Neither `glab` nor `az` was available on this verification process's PATH; the fixture tests do not establish installed-CLI or live-account acceptance.
