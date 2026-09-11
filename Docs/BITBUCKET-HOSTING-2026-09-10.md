# Bitbucket Cloud hosting

HOST-06 is implemented on top of `3635fc7`, using the existing protocol-58 contracts. This covers Bitbucket Cloud at `bitbucket.org`; Bitbucket Data Center and account/repository discovery before clone (HOST-01) remain outside this adapter.

## Usage and credentials

Existing Bitbucket project remotes now support PR listing and creation, the cross-repository inbox, detailed patches, commits, checks, discussions, replies, resolution, inline reviews, approval/requested changes, own-comment edits/deletion, title/body edits, reviewer requests/removal, merge and decline. Reviewer requests require a Bitbucket UUID; there is no reviewer candidate picker. Settings also supports publishing a repository into a Bitbucket workspace and resuming publication into an existing repository.

Configure credentials in the environment of the process running the PiStation host, then restart that process so it inherits them:

- `PISTATION_BITBUCKET_ACCESS_TOKEN` uses Bearer authentication and takes precedence.
- Otherwise set both `PISTATION_BITBUCKET_EMAIL` and `PISTATION_BITBUCKET_API_TOKEN` for Basic authentication.

Anonymous reads can work for public repositories. Configured credentials and a successful repository read allow actions to be offered; they do not establish write permission. Bitbucket remains authoritative about account permissions and repository rules. Own-comment changes additionally require a verified matching user UUID. Use appropriately scoped credentials following Atlassian's [API token instructions](https://support.atlassian.com/bitbucket-cloud/docs/using-api-tokens/) and [permission reference](https://support.atlassian.com/bitbucket-cloud/docs/api-token-permissions/).

Clone and publication pushes still use the existing Git credential configuration. REST credentials are not installed into Git. Publication preserves unrelated remotes, captures the branch/commit, handles empty repositories, and supports explicit resume without creating another repository. API tokens are never put into remote URLs or command arguments.

## T3 comparison and boundaries

Reference: local `../t3code`, commit `0a590fa01af66ec135d2ebf2d5542b08a37dc275`. The relevant source is `apps/server/src/sourceControl/BitbucketApi.ts`, `BitbucketSourceControlProvider.ts`, and `apps/server/src/pullRequest/BitbucketPullRequestApi.ts`, `BitbucketPullRequestProvider.ts`.

Both implementations use the Cloud REST API rather than a fictional `bb` CLI. PiStation follows T3's inline-comments, summary, then verdict ordering, and its merge/decline capability boundary. It retains PiStation's durable drafts and operation receipts instead of adopting T3's in-memory review store. T3 additionally has workspace reviewer candidates and a configurable API base; PiStation currently accepts reviewer UUIDs and fixes the API origin to Bitbucket Cloud.

Draft transitions, labels, reopening declined PRs, reactions, automatic merge, branch update, revert and workflow controls are not offered. The existing **Review in Pi** checkout action remains GitHub-only. Detailed Bitbucket review is available. API routes and merge semantics follow the [official pull-request reference](https://developer.atlassian.com/cloud/bitbucket/rest/api-group-pullrequests/).

Review sections page independently, with up to 100 entries per page and 30 pages per section. Patch reads are capped at 2 MB and parsing at 20,000 lines per page; binary, ambiguous or unavailable patches cannot receive inline comments. Continuations bind to the repository, PR, head and base. The inbox retains its 100-repository/ten-page limits and loaded-page text/author filtering; unsupported filters are explicit.

Each write rechecks the captured remote, head/base and known account before dispatch. Bitbucket does not provide an atomic commit condition for these writes: a change between verification and the API call remains possible. Multipart reviews are not atomic. Confirmed progress is retained; an uncertain dispatched step is not automatically replayed. Merge acceptance requires a subsequent refresh to establish the final merged state.

The HTTP client fixes the API origin, permits only bounded same-repository read redirects, never follows write redirects, limits response bodies, and applies a 45-second deadline through body consumption. Provider response bodies and credential values are excluded from user-facing errors.

## Verification

The initial host build passed. The first broader gate exposed a Windows newline bug: rebuilt patch headers retained carriage returns and valid patches appeared unavailable. LF normalization fixed that issue; tests also cover renamed files and header-like text inside patch contents. The original failure evidence is retained in `TestResults/bitbucket-gate-1.log` and `TestResults/code-Debug-20260910-233003-6ce7f1b5/`.

The final solution/WinUI build passed with zero warnings/errors. The affected gate passed **310 tests**, with zero failures/skips: 110 ClientRuntime, 190 Host and 10 Protocol. See the [gate log](../TestResults/bitbucket-gate-2.log) and [summary](../TestResults/code-Debug-20260910-233425-11522cbc/summary.json). Coverage includes local/TLS transport, read-only denial, stale revisions/accounts, scoped continuations, renamed-file inline anchors, interrupted reviews and durable replay, unsupported actions, response/redirect limits and empty-repository publication/resume. No functional changes followed this gate.

[Static visual contracts](../TestResults/bitbucket-visual-contract.log) passed for 24 states, four responsive layouts and three text scales. [Package verification](../TestResults/bitbucket-package-check.json) confirmed matching Host and Protocol SHA-256 hashes in the desktop output and bundled SSH host.

Tests use isolated HTTP fixtures, temporary Git repositories and local/TLS transports; no live provider sign-in, API write or real publication push was performed. Native interaction and rendered visual acceptance remain pending. Static visual contracts are source checks, not native rendering evidence.
