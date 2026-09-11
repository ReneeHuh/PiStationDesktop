# Hosting repository browser

HOST-01 adds account-scope and repository browsing before cloning, based on `b3413a8`. The protocol is now **59**; manually update both Windows host and client before connecting.

## Using it

Open **Settings → Source control → Clone hosted repository → Browse accounts and repositories**. Choose the provider and optionally a server name, then **Load accounts**. Azure additionally requires an organization name. Choose an account scope, load repositories, select one, enter the destination path on the connected host, and choose **Clone and add project**.

Repository filtering searches names already loaded. **More accounts** and **More repositories** page further. Selecting another provider/server clears prior results; selecting another account clears the repository selection. Failed reads leave a retry path. The selected HTTPS clone URL is shown before dispatch. The existing direct remote-URL clone remains available, including manually entered SSH URLs.

Browsing uses the active credentials of the connected host. Selecting a scope does not switch accounts or sign in. GitHub/GitLab use `gh`/`glab`; Azure uses `az` with the DevOps extension; Bitbucket uses the [host-side REST credentials](BITBUCKET-HOSTING-2026-09-10.md). Git cloning uses existing Git credentials. No credential is copied from the REST adapter into Git. Browsing is allowed on read-only connections; cloning requires Operate access. The dialog captures its originating connection and rejects operations after that connection changes.

| Provider | Browsable scopes | Boundaries |
| --- | --- | --- |
| GitHub | Active user’s member repositories and organizations | Explicit server name supports enterprise hosts. Access and organization membership depend on the active token. |
| GitLab | Active user’s member projects and groups | Explicit server name; group results include subgroups and exclude shared-in projects. |
| Bitbucket Cloud | Active credentials’ workspaces and their repositories | `bitbucket.org` only. Repository-scoped tokens may lack workspace-list permission. |
| Azure DevOps | Projects within an explicitly entered organization | `dev.azure.com` only. This does not enumerate Azure organizations or switch subscriptions/accounts. |

Account and repository pages contain up to 100 entries; browsing stops after twenty pages with a notice. GitHub/GitLab may offer an extra empty page when the preceding page has exactly 100 entries. Azure repository listing is bounded to 2,000 entries and the existing provider-output size limit, sorted and sliced locally; it is fetched again for each page. Concurrent provider changes can shift pages, so the UI deduplicates identities and offers refresh. This is a bounded picker, not a guarantee of exhaustive organization-wide indexing.

Provider errors remain explicit. Malformed lists, invalid scope paths, credential-bearing or foreign-host clone URLs, and conflicting repository names/URLs are rejected. Bitbucket’s returned `next` URL is never followed; subsequent requests are constructed from the selected workspace and page. Azure’s organization userinfo is stripped from clone URLs. Credentials and raw provider-error bodies are not shown.

The existing clone operation protects nonempty destinations and registers the resulting project. Optional hosting metadata failure after successful clone/registration now returns success with a refresh notice, avoiding a misleading failure and duplicate clone attempt. A provider-specific metadata adapter may not recognize a custom hostname even when browsing and Git cloning work.

## T3 comparison

Reference: local `../t3code`, commit `0a590fa01af66ec135d2ebf2d5542b08a37dc275`, inspected without fetching upstream.

- `apps/server/src/sourceControl/SourceControlProviderDiscovery.ts` probes provider availability and authentication. Its account information describes active host credentials.
- `apps/server/src/sourceControl/SourceControlRepositoryService.ts` separates repository lookup, clone and publication. Lookup resolves a named repository through the chosen provider; cloning checks the destination and uses Git.
- `apps/web/src/components/CommandPalette.tsx`, around the repository/confirm steps, carries the environment identity through lookup and clone, displays a resolved remote, then accepts the destination.

The inspected T3 source uses named repository lookup rather than a general account/repository listing picker. PiStation follows its provider/environment separation and explicit destination step, and implements the broader browsing requested by HOST-01 through new typed read-only protocol calls. It keeps PiStation’s existing durable clone operation and project-registration flow. The picker supplies HTTPS URLs; T3 also selects SSH/HTTPS clone protocols.

API references: [GitHub repositories](https://docs.github.com/en/rest/repos/repos), [GitLab groups](https://docs.gitlab.com/api/groups/) and [projects](https://docs.gitlab.com/api/projects/), [Bitbucket workspace memberships](https://developer.atlassian.com/cloud/bitbucket/rest/api-group-workspaces/), [Azure project listing](https://learn.microsoft.com/en-us/cli/azure/devops/project?view=azure-cli-latest) and [repositories](https://learn.microsoft.com/en-us/cli/azure/repos?view=azure-cli-latest).

## Verification

The initial host build and the final solution/WinUI build passed; the latter had zero warnings/errors. The affected gate passed **94 checks**, with zero failures/skips: 91 Host, two ClientRuntime and one Protocol. See the [gate log](../TestResults/discovery-gate-1.log) and [summary](../TestResults/code-Debug-20260911-021014-da7e8d4b/summary.json). No functional changes followed this gate.

Coverage includes all four provider routes, custom GitHub/GitLab hosts, explicit Azure scope, paging bounds, invalid hosts/scopes, malformed lists, foreign/credential-bearing/mismatched clone URLs, sanitized failures, cancellation, nonempty destination protection, successful local clone/registration despite optional metadata failure, local/TLS browsing with no initial project, read-only browse/clone denial, and reconnect visibility. Existing source-control and repository-publication regressions were included.

[Static visual contracts](../TestResults/discovery-visual-contract.log) passed for 24 states, four responsive layouts and three text scales. [Package checks](../TestResults/discovery-package-check.json) confirmed identical Host and Protocol SHA-256 hashes in the desktop output and bundled SSH host.

Fixtures isolate all hosting-provider traffic. Local Git clone checks use temporary repositories. Live provider sign-in and native visual acceptance remain pending by request; static source contracts do not establish rendered appearance or physical interaction.
