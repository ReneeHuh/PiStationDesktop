# Remote access completion plan

Status: implementation and local automated verification delivered in the working tree. All 296 solution tests and all 11 packaged UI journeys passed. See [implementation, usage, and qualification notes](REMOTE-ACCESS-IMPLEMENTATION.md). Signed-desktop deployment, physical two-machine checks, and rendered visual review remain explicit release gates.

Created September 7, 2026, against PiStation `5c1516c`. The [remote access review](REMOTE-ACCESS-REVIEW-2026-09-07.md) supplies the defect evidence and T3 comparison. This is the follow-on plan for the ten partial features, extending the release scope recorded in [the original remote plan](PISTATION-REMOTE-ACCESS-PLAN.md).

## Intended outcome

A Windows client can keep working with a Windows host over direct HTTPS or managed SSH through connection loss, host restart, and client sleep. Chat and terminals recover, valid file edits save, other devices see catalog changes, unused streams stop, and connection failures offer the right recovery action. Users can change a host endpoint without discarding its identity, update supported remote hosts through their owner, and open host-local development sites in Preview. Connections shows enough safe diagnostic and session information to understand failures.

This plan includes the host and client work needed to finish those behaviors. Public relays, account discovery, Tailscale configuration, Linux/macOS hosts, mobile/web clients, and general-purpose browser automation remain separate projects. Detecting network restoration is included because it is part of reliable reconnect.

## Feature completion map

| Feature | Delivery stages | Done when |
| --- | --- | --- |
| Automatic reconnect | R1, R5, R9 | Transient failures retry while the user wants the connection; resume/network restoration triggers recovery; disconnect cancels it. |
| Stream recovery | R1, R3, R4, R9 | Chat, terminal, and catalog streams resume or resnapshot without frozen waiters, missing events, or duplicate application. |
| Subscription management | R3, R4 | Live subscriptions follow active consumers; retained snapshots/cursors have explicit limits. |
| Remote file editing | R2 | Every supported file size saves over local, direct HTTPS, and SSH transports with revision checks and useful size errors. |
| Multi-device synchronization | R3 | Project and thread metadata changes appear on other connected clients without manual refresh. |
| Authentication recovery guidance | R1, R8 | Initial and reconnect failures consistently distinguish authentication, access, identity, compatibility, and connectivity. |
| Endpoint management | R5 | A replacement endpoint is verified before being saved, preserving credentials, identity, and window preferences. |
| Remote updates | R6 | Owned SSH hosts, supported standalone hosts, and packaged desktop hosts have an owner-mediated update path with progress and recovery. |
| Remote previews | R7 | Host-discovered local sites open through an authenticated route, including assets, navigation, SSE, and WebSocket reload traffic. |
| Connection diagnostics | R1, R8 | Users see connection stage, failure category, retry state, version, session activity, and a safe diagnostic export. |

The status of every row remains partial until its acceptance criteria pass. In particular, an error message telling users to update manually does not complete remote updates, and listing a host's localhost URL does not complete previews.

## Design rules

- Keep `EnvironmentService` as the execution and state boundary. Transport changes must not create another project/thread model.
- Keep certificate pinning, expected environment identity, protected credential storage, host-approved pairing, and explicit per-RPC policy.
- Use one connection lifecycle owner. A socket opening is transport readiness; authenticated identity and synchronization establish application readiness. Each stream separately reports whether it has caught up.
- Reconnect reads and subscriptions automatically. Never automatically resend terminal input, turns, file writes, update commits, or other mutations whose result is uncertain. Reuse existing receipts/revisions where available.
- Access and process ownership stay separate. A paired operate credential does not grant a client permission to kill a host by PID, executable name, or data directory.
- New capabilities have explicit authorization and compatibility decisions. Read-only clients stay passive; preview opening and update commits require operate access and their host policies.
- Keep the existing local host working at each stage. Apply transport configuration changes consistently to embedded, direct, and SSH listeners.

## Delivery sequence

| Stage | Work | Depends on |
| --- | --- | --- |
| R0 | Baseline, contracts, and regression fixtures | Review |
| R1 | Connection lifecycle, stream recovery, and authentication states | R0 |
| R2 | File/RPC transport limits | R0; can ship independently of R1 |
| R3 | Live environment catalog | R1 |
| R4 | Subscription ownership and bounded caching | R1, R3 |
| R5 | Verified endpoint changes and network restoration | R1 |
| R6 | Remote update preparation and owner handoff | R1, R5; R2 for shared limits policy, with update uploads using separate HTTP limits |
| R7 | Remote preview discovery and forwarding | R1, R4, R5 |
| R8 | Session visibility and diagnostic workflows | R1; integrate update/preview stages from R6/R7 |
| R9 | Integrated qualification and documentation | R1–R8 |

Ship R1 and R2 first to remove the reproduced P1 failures. R6 and R7 need new host capabilities and should be delivered as several bounded changes. This sequence does not require parallel agents or a single large rewrite.

## R0 — Establish the regression and compatibility baseline

**Work**

- Move the isolated outage/large-save reproductions from the review into maintained tests using temporary data, generated certificates, and FakePi. Include an existing chat subscription, not just a fresh client after restart.
- Add a deterministic connection test seam for clock/backoff, transport outcomes, and lifecycle receipts. Tests should await observable transitions or drain signals; use virtual time for retries and finite deadlines only as failure guards.
- Capture local/direct/SSH policies and supported payload sizes in shared declarations so future listeners cannot drift.
- Record a compatibility decision for every new wire contract, optional field, persisted schema, and capability. Register new types in `ProtocolJsonContext` and extend authorization coverage tests.
- Check both descriptor protocol ranges and `SshHostInfo.Validate`, which currently requires an exact protocol match. Do not claim version compatibility merely by advertising a capability or widening a range; prove command-envelope, serializer, and bootstrap compatibility. Unsupported protocol versions must produce an actionable update state.

**Acceptance**

- Regression tests expose the current frozen subscription and 40 KB save failure before their respective fixes.
- Tests distinguish unsupported optional features from incompatible protocols and exercise representative prior-version payloads.
- Test hosts never use a live environment data root.

**Primary code:** [ConnectionSupervisor](../src/PiStation.ClientRuntime/ConnectionSupervisor.cs), [ProtocolVersion](../src/PiStation.Protocol/ProtocolVersion.cs), [SshHostInfo](../src/PiStation.Protocol/Models/SshHostInfo.cs), [ProtocolJsonContext](../src/PiStation.Protocol/Serialization/ProtocolJsonContext.cs).

## R1 — One recoverable connection lifecycle

**Work**

- Make `ConnectionSupervisor` own desired connection state, attempts, cancellation, and readiness. Replace the finite SignalR automatic-reconnect loop with one application-owned attempt loop, so initial connection, automatic retry, Retry now, and endpoint replacement share the same policy.
- Use capped backoff for transient failures: immediate first retry, then approximately 1, 2, 5, 10, and 30 seconds with bounded jitter. Continue at the cap until explicitly disconnected or permanently blocked. Reset backoff after a stable connection or an explicit retry request.
- Give each attempt a generation and linked cancellation token. Ignore completion from older generations. Cancel transport preparation, HTTP setup, synchronization, and stream work when that generation ends; remove uncancelable SSH repair initiated through `CancellationToken.None`.
- Separate transport-ready from application-ready. Internal bootstrap calls can use an authenticated socket; public operations wait for identity/protocol validation and initial metadata synchronization. Do not let subscriptions use the current raw `HubConnectionState.Connected` shortcut before validation.
- Ensure all pending readiness waiters are completed or canceled on a generation transition. No task completion source may be replaced while leaving waiters stranded. Disposal terminates outstanding waits and retries.
- Classify failures into transient network/timeout/host unavailable, authentication required, operation permission denied, certificate mismatch/expiry, environment identity mismatch, incompatible protocol, and explicit cancellation. A permission failure on one operation does not automatically invalidate an otherwise valid connection.
- Stop retrying invalid credentials or incompatible identities. If SignalR reports an opaque close, use a bounded pinned/authenticated probe to establish the reason; prefer structured status/error information over message-string matching.
- Resubscribe using existing thread/terminal cursor and epoch rules. Make stream synchronization observable, including the zero-missing-events case, so the UI cannot indefinitely show a catch-up spinner or call stale data live. Keep command receipt reconciliation independent from subscription recovery.
- Apply the same classification to the recovery banner, Connections Open, SSH progress, and manual Retry. Retain unsent draft text through transient reconnect and distinguish it from a dispatched command with an unknown result.

**Acceptance**

- An existing client survives an outage longer than the old retry ladder, resumes after host return, and receives subsequent chat and terminal output.
- Retry now during an automatic attempt produces one active connection, with no duplicate subscriptions or abandoned waiters.
- Revocation/expiry during an active session reaches Authentication required with a fresh-pairing action; certificate and identity changes never trigger silent trust replacement.
- Cancel/close during SSH authentication or repair ends promptly and never restarts an intentionally disconnected connection.
- Host restart, epoch change, journal overflow, and no-missing-events resume all reach the correct stream state.
- A turn or terminal input with uncertain dispatch is not sent twice during recovery.

**Primary code:** [ConnectionSupervisor](../src/PiStation.ClientRuntime/ConnectionSupervisor.cs), [EnvironmentClient](../src/PiStation.ClientRuntime/EnvironmentClient.cs), [ThreadSubscription](../src/PiStation.ClientRuntime/ThreadSubscription.cs), [TerminalSubscription](../src/PiStation.ClientRuntime/TerminalSubscription.cs), [ManagedSshConnection](../src/PiStation.ClientRuntime/Ssh/ManagedSshConnection.cs), [ShellViewModel](../src/PiStation.App/ViewModels/ShellViewModel.cs).

## R2 — Make valid file payloads fit the transport

**Decision:** retain the existing file-save RPC and use a shared bounded SignalR receive limit. This fixes the current defect without adding a second file-write implementation.

**Work**

- Introduce shared transport options for all three listeners. Start with an 8 MiB hub receive ceiling, which provides room for the supported 1 MiB UTF-8 file plus worst-case JSON escaping and a bounded request envelope. Verify the actual serializer representation and envelope bounds in tests before finalizing that value.
- Keep the file service's 1 MiB UTF-8 content limit and optimistic revision checks. Add a matching client-side size error that retains edited text. The higher transport ceiling is not a new user-visible file-size allowance.
- Bound other user-controlled text/envelope fields appropriately; keep attachment and future update/preview HTTP limits separate from hub limits.
- Make rejection of a file just above the supported content limit an operation error while the connection remains usable. Deliberately oversized raw transport frames may still be closed at the transport boundary.
- When a save's acknowledgement is lost, retain the buffer and reload/check the remote revision before presenting a retry; do not automatically repeat the write.

**Acceptance**

- Save 40 KB, boundary-size, multibyte UTF-8, and heavily escaped files through local, direct HTTPS, and forwarded SSH connections.
- A file at the 1 MiB content limit succeeds; a file above it fails clearly without losing the editor buffer or disconnecting an ordinary client.
- Two clients saving from the same revision produce a conflict for the stale writer; neither silently overwrites the other.
- Oversized raw frames remain bounded, and read-only file saves remain denied.

**Primary code:** the three [hosting listeners](../src/PiStation.Host/Hosting), [WorkspaceFileReadService](../src/PiStation.Host/Files/WorkspaceFileReadService.cs), [file contracts](../src/PiStation.Protocol/Models/FileSearch.cs), [EnvironmentClient](../src/PiStation.ClientRuntime/EnvironmentClient.cs).

## R3 — Keep project and thread lists current across devices

**Decision:** introduce a lightweight environment catalog snapshot/delta stream. Full chat content stays in per-thread streams.

**Work**

- Add catalog contracts with environment identity, stream epoch, sequence/cursor, snapshot, project/thread upsert, removal, and synchronization-complete markers. Cover every existing mutation that changes displayed project/thread metadata: creation, naming, pin/archive state, workspace/branch state, project configuration/trust, and setup status.
- Store catalog sequence/change records in the host database in the same transaction as the corresponding durable metadata change. Publish only committed records. Runtime-only status gets an explicit live projection rule rather than pretending to be a durable metadata revision.
- Build snapshots and their watermark from a consistent read. Subscribe/replay around that watermark without a gap. Keep replay and subscriber buffers bounded; expired cursors or host epochs trigger an authoritative snapshot.
- Chunk or page large initial catalogs. Do not transmit chat messages, prompts, tool output, credentials, or whole file contents in catalog events.
- Add a client catalog store shared by sidebar, search metadata, and project selection. Apply local command responses and remote deltas idempotently, without allowing older snapshots to undo newer revisions.
- Preserve search/archive filters and selection during updates. Removing or archiving a selected item follows a defined UI transition. Refresh becomes an explicit resnapshot action, not the normal way to see another device's work.
- Authorize catalog reads for both read-only and operate devices. Subscription alone must not create Pi processes.

**Acceptance**

- Two clients see creation and metadata changes without reconnecting or refreshing; verify both directions and a read-only observer.
- A mutation racing the initial snapshot is applied exactly once in the final client state.
- Disconnect during several changes, restart the host, and overflow the replay window: all converge to the host catalog.
- Search/archive filtering and current selection remain correct while unrelated changes arrive.

**Primary code:** [HostDatabase](../src/PiStation.Host/Persistence/HostDatabase.cs), [EnvironmentService](../src/PiStation.Host/EnvironmentService.cs), [EnvironmentHub](../src/PiStation.Host/Hubs/EnvironmentHub.cs), [ThreadMetadataStore](../src/PiStation.ClientRuntime/ThreadMetadataStore.cs), [ShellViewModel](../src/PiStation.App/ViewModels/ShellViewModel.cs).

## R4 — Give live subscriptions an explicit lifetime

**Work**

- Replace indefinitely cached live thread subscriptions with acquire/release leases. Multiple consumers of one thread share a stream; releasing the last consumer cancels and removes it. Apply the same ownership contract consistently to terminal streams.
- Keep snapshot/cursor caching separate. Initial policy: at most 20 inactive thread snapshots, at most 32 MiB of retained serialized snapshot data, and a 10-minute idle lifetime, evicting least recently used entries. Do not retain a single oversized inactive snapshot; active content is outside this inactive-cache budget.
- Cancel and observe server subscription completion when a view stops consuming it. Removing a stream must not stop its Pi process, active turn, or terminal session.
- Ensure a disposed subscription can never be returned from `EnvironmentClient`'s dictionary. Handle rapid A → B → A navigation and release/reacquire during reconnect.
- Let the R3 catalog stream provide sidebar activity while detail streams are closed. Restore a cached view immediately as cached data, then resume from its cursor or request a snapshot.

**Acceptance**

- Visit at least 100 threads; live detail-stream count returns to the active-consumer count, and inactive cache use stays within its configured budget.
- Releasing one of two consumers preserves their shared stream; releasing the second closes it.
- Returning to an evicted or cached thread yields current history without duplicated events.
- Closing a stream leaves host work running; closing a remote window releases its connection and preview/stream resources.

**Primary code:** [EnvironmentClient](../src/PiStation.ClientRuntime/EnvironmentClient.cs), [ThreadSubscription](../src/PiStation.ClientRuntime/ThreadSubscription.cs), [TerminalSubscription](../src/PiStation.ClientRuntime/TerminalSubscription.cs), [ShellViewModel](../src/PiStation.App/ViewModels/ShellViewModel.cs).

## R5 — Verify and save changed endpoints

**Work**

- Add Edit address / Test connection to saved direct environments. Probe the candidate using the existing pin and credential, then verify the expected environment ID and protocol before atomically replacing the saved route.
- Preserve environment identity, device credential, client ID, layout, and cached state. Use a candidate connection generation so an unsuccessful probe or canceled edit leaves the previous saved route intact.
- Stage any unsaved draft/editor state explicitly during a connection swap. Do not flush through a superseded identity or drop unsaved text as a side effect of replacing its window/runtime.
- Track a selected host adapter and last bound address where useful. If the address disappears, report Needs address instead of displaying sharing as available. Offer current addresses; do not silently move exposure to another adapter or widen it to a wildcard.
- Wire Windows network-change and resume/activation notifications into R1's supervisor, with debounce and cancellation. Notifications prompt a bounded health check/retry; they do not bypass TLS or discover a new trusted identity automatically.
- Clear transient endpoint/tunnel URLs from persistence. Treat SSH forwards as route adapters for the same environment, retaining their existing SSH profile identity.

**Acceptance**

- Move the test listener to another port/address with the same identity; editing succeeds without pairing again.
- Wrong pin, wrong environment, expired grant, unreachable route, cancellation, and storage-write failure preserve the old saved profile and usable local state.
- Network restoration and wake recover an intended connection. Explicitly disconnected connections stay disconnected.
- Host adapter loss reports unavailable sharing and offers reconfiguration without exposing another interface.

**Primary code:** [RemoteConnectionStore](../src/PiStation.ClientRuntime/RemoteConnectionStore.cs), [RemoteAccessController](../src/PiStation.App/Composition/RemoteAccessController.cs), [RemoteConnectionsPanel](../src/PiStation.App/Views/Controls/RemoteConnectionsPanel.xaml.cs), [App window management](../src/PiStation.App/App.xaml.cs).

## R6 — Update remote hosts through their owner

This stage includes new infrastructure for separately owned production hosts. Retaining today's manual-update message alone is not sufficient.

**Shared workflow**

- Add an update capability/descriptor that reports host kind, current/target version, supported update method, compatibility, owner policy, and active-work state. Use separate request IDs and durable receipts for prepare, stage, commit, health confirmation, and final result; a reconnect must not duplicate a commit.
- Transfer a bounded package over authenticated HTTP or the existing verified SSH channel. Stage into a versioned directory, verify package type/platform/integrity and its configured trust source, and report progress before any shutdown. A checksum detects corruption; it must not be presented as publisher authentication.
- Show the concrete version change, owner, and active-work impact. Default to commit when idle; an immediate interruption is a distinct user action. Read-only clients cannot stage or commit. Hosts explicitly enable remote update requests through local configuration.
- Keep a small owner/launcher outside the replaceable runtime responsible for draining, stopping its captured child, releasing the data lock, starting the staged version, checking pinned environment readiness, and publishing the receipt. Never overwrite loaded binaries or stop an arbitrary discovered process.
- Cancel safely before commit. After commit, let the owner finish independently of client connectivity and expose the result on reconnect. Preserve environment ID, certificates, grants, state root, and configured exposure.
- Test package rollback separately from database compatibility. Do not start old binaries against a database migrated incompatibly by a failed new version. Require backward-compatible migration or a verified offline backup/restore procedure before claiming rollback support.

**Adapters to deliver**

| Host kind | Required implementation |
| --- | --- |
| Connection-owned managed SSH server | Extend the existing bundled stage/reconnect path with durable status, cancellation, active-work handling, and post-update version verification. |
| Separately running self-contained server | Add an owner/launcher mode for supported `serve` installations so the host can accept a request and hand off its own restart. Initial legacy installations may need a one-time launcher-capable upgrade; subsequent updates must be remote. |
| Packaged desktop hosting an environment | Integrate an owner-side Windows package update/relaunch adapter using a trusted matching-publisher package source. Include host window state and local-user work in the update decision. An SSH attachment cannot perform this by stopping its own non-owning control process. |
| Development or unsupported third-party launcher | Advertise the actual unsupported capability and recovery instructions. Do not report a successful update when only a bundle was copied. |

The packaged-desktop adapter requires a reproducible signed package and a configured update source. Establish those artifacts during this stage; until that adapter is delivered and verified, the supported desktop-host update row remains partial. No public package feed is assumed to exist today.

**Acceptance**

- Update each supported production host kind from an older compatible fixture and observe the target version after reconnect with the same environment/grants.
- Reject corrupted, wrong-platform, untrusted, and incompatible packages before shutdown; enforce the host's update policy and device permission.
- Exercise interrupted upload, cancel, repeated commit, two competing update requests, active work, lost client connection during restart, startup failure, and data-lock contention.
- A separately owned host is restarted only by its owner after an accepted update request. A normal client disconnect still leaves it running.
- Verify successful recovery or an accurate recoverable failure receipt after a failed activation; never claim rollback without testing its database behavior.

**Primary code:** [ManagedSshConnection](../src/PiStation.ClientRuntime/Ssh/ManagedSshConnection.cs), [SshHostBundle](../src/PiStation.ClientRuntime/Ssh/SshHostBundle.cs), [installer](../src/PiStation.ClientRuntime/Ssh/Helpers/install-host.ps1), [server entry point](../src/PiStation.Server/Program.cs), [App update/window flow](../src/PiStation.App/App.xaml.cs). New host update coordinator, launcher, package adapters, and receipt storage are required.

## R7 — Discover and forward host-local previews

**Decision:** keep WebView2 on the client and provide a native forwarding adapter for both direct HTTPS and managed SSH. A displayed host URL is separate from the temporary client loopback URL that renders it.

**Work**

- Add remote preview discovery/open/close capabilities with explicit authorization. Reuse the existing host scanner, but describe its current host-wide candidate list honestly: it does not establish that every discovered port belongs to the selected project.
- Opening a preview creates a short-lived lease bound to the authenticated principal, environment, selected upstream scheme/loopback port, and client connection. Require operate access for creating/using a forwarding lease because arbitrary application HTTP requests can mutate a development server. Read-only clients keep their existing manually reachable URL behavior.
- Validate the selected host-local target server-side; do not accept an arbitrary proxy destination on each request. Exclude PiStation's own control/management listeners. HTTPS upstreams use normal certificate validation or an explicit per-target trust decision, never a global certificate bypass.
- Create a native client loopback proxy that communicates with the host through a pinned, authenticated preview data channel. Use the same host lease protocol over the SSH-forwarded PiStation endpoint, so previews work without SSH on a direct connection. Bound connection count, buffers, body sizes, idle lifetime, and backpressure independently from chat RPCs.
- Keep the device credential in native transport code. It never enters page JavaScript, page URLs, logs, or upstream application headers. Protect the client loopback route with a preview-scoped gate initialized natively, validate its Host/Origin, and isolate browser storage between environments/preview sessions. Closing/revoking a lease invalidates that route.
- Preserve a distinct origin per preview mapping so root-relative assets work. Support HTTP methods and streaming bodies, same-upstream redirects, upstream cookies, SSE, and WebSocket upgrades for HMR. Define rewriting of Host/Origin/Location/cookie attributes only for the selected upstream; cross-target navigation requires a new authorized route. Do not blindly rewrite page content or forward control credentials upstream.
- Persist the logical host preview URL and environment association, never transient ports, leases, or secrets. Recreate routes after reconnect. Closing Preview cancels its forwarding resources without stopping the development server or PiStation host.
- Retain back/forward/reload, screenshots, and other existing preview behavior. Open External for a tunneled preview must either keep an explicitly scoped external-browser lease alive or explain that the embedded route is unavailable externally; it must not open a host localhost address on the wrong machine.

**Acceptance**

- A development site listening only on the host loopback renders through both direct HTTPS and managed SSH.
- Test HTML, root-relative JS/CSS/images, same-origin cookies, redirects, forms/fetch, SSE, and a WebSocket reload round trip.
- Validate wrong/revoked/expired leases, read-only access, arbitrary destination attempts, control-port targets, and local proxy requests outside the scoped browser session.
- Disconnect/reconnect, close/reopen Preview, change the selected site, and revoke the device: no leaked forwards, credentials, or browser state crossing environments.
- The host development server keeps running when Preview closes. Existing local and manually reachable previews still work.

**Primary code:** [preview contracts](../src/PiStation.Protocol/Models/Preview.cs), [PreviewDiscoveryService](../src/PiStation.Host/Preview/PreviewDiscoveryService.cs), [RemoteAuthorizationFilter](../src/PiStation.Host/Security/RemoteAuthorizationFilter.cs), [WorkbenchPreviewViewModel](../src/PiStation.App/ViewModels/WorkbenchPreviewViewModel.cs), [PreviewWebViewSurface](../src/PiStation.App/Views/Controls/PreviewWebViewSurface.xaml.cs). New host lease/forwarding endpoints and a native client preview proxy are required.

## R8 — Make connection and session state useful

**Work**

- Build diagnostics from R1's structured lifecycle state: route type, stage, retry attempt/next retry, last successful connection, safe failure category, host/client version, and compatibility. Add update and preview stages from R6/R7.
- Extend auth-session metadata with created/last-used/last-connected/disconnected information as appropriate. Track each live connection independently so closing one of several sockets does not incorrectly mark the device offline. Coalesce persistent activity writes instead of writing for every RPC.
- Use connection leases/heartbeats with expiry or equivalent reconciliation so a host crash or CLI process cannot leave a persisted online flag permanently true. Keep host/CLI views consistent and preserve backward-compatible migration of existing grants.
- Add Test connection, Retry now, Re-pair, Edit address, and Copy diagnostics actions where the failure makes them useful. Reuse the same actions across the recovery banner, Settings, and applicable command entries.
- Export a bounded diagnostic bundle containing safe state and correlation IDs. Exclude tokens, pairing links, password prompts, headers, prompts, file contents, and raw exception/request formatting. Endpoint/account details should be shown or redacted deliberately, not leaked through exception text.

**Acceptance**

- Users can distinguish offline host, failed SSH authentication, rejected device access, certificate/identity mismatch, incompatible version, and failed update/preview route.
- Session activity is correct with multiple connections, CLI revocation, restart, abrupt host loss, and clock-controlled expiry.
- Diagnostic exports pass secret-sentinel tests while retaining enough information to identify the failing stage and request.
- Session refresh preserves selection and does not block the UI with frequent SQLite writes.

**Primary code:** [RemoteAccessStore](../src/PiStation.Host/Security/RemoteAccessStore.cs), [RemoteDiagnosticLoggerProvider](../src/PiStation.Host/Hosting/RemoteDiagnosticLoggerProvider.cs), [RemoteAuthCli](../src/PiStation.Server/RemoteAuthCli.cs), [ConnectionViewModel](../src/PiStation.App/ViewModels/ConnectionViewModel.cs), [RemoteConnectionsPanel](../src/PiStation.App/Views/Controls/RemoteConnectionsPanel.xaml.cs).

## R9 — Qualification and completion gate

Run focused tests as each behavior lands. At integration, use the repository's [pull-request gate](../Invoke-PullRequestTests.ps1) and the applicable packaged UI journeys from [the UI test plan](WINAPP-UI-TESTING-PLAN.md). Coordinate host/update tests so one fixture cannot stop another fixture's server. No test may read-write a live user database or mutate unrelated SSH/firewall configuration.

| Scenario | Required coverage |
| --- | --- |
| Transports and access | Embedded local, direct pinned HTTPS, managed SSH; operate and read-only clients. |
| Network recovery | Short and prolonged outage, host restart, client sleep/wake, adapter loss/restoration, manual retry, explicit disconnect. |
| Stream correctness | Existing chat/terminal/catalog consumers, zero-event resume, duplicate/gapped events, epoch changes, bounded replay overflow, rapid consumer churn. |
| Mutations | Concurrent file revisions, attachment behavior, uncertain turn/file/update results, no automatic duplicate execution. |
| Multi-device state | At least two independent clients plus a passive observer; creation/metadata changes and reconnect convergence. |
| Endpoint changes | Verified migration, wrong identity, cancellation, persistence failure, unsaved local state. |
| Updates | All supported production owners, package validation, active work, failed activation, duplicate requests, post-restart receipt/version. |
| Previews | Host-loopback fixture through direct/SSH, assets, cookies, redirects, SSE, WebSocket, revocation and teardown. |
| UI | Connections and recovery banner, keyboard/command entry points, narrow layouts, cancellation/progress, accessibility labels. |
| Real Windows machines | Two-machine direct and SSH pairing, authentication prompts, sleep/wake, preview forwarding, and supported host update/reconnect. Record any unexercised case explicitly. |

**Completion checklist**

- [x] R0: maintained regression fixtures and protocol 20 compatibility decision.
- [x] R1: persistent recovery, synchronization markers, TLS interruption recovery, and correct authentication states.
- [x] R2: full supported file sizes tested across local, direct HTTPS, and SSH listeners.
- [x] R3: live multi-device catalog with atomic paged application and replay.
- [x] R4: bounded inactive cache and consumer-owned live streams, including server-side cancellation.
- [x] R5: verified endpoint replacement, rollback, and network/activation recovery hooks. Physical adapter and sleep/wake qualification remains in R9.
- [ ] R6: owner-mediated updates for supported production host kinds.
- [x] R7: authenticated preview discovery and forwarding through direct HTTPS and SSH listeners, with embedded route recreation after reconnect.
- [x] R8: session activity, expiring connection leases, structured recovery status and safe diagnostic export.
- [ ] R9: focused tests, required build/PR checks, packaged UI, and recorded real-machine qualification.

R6 includes a working standalone supervisor, staging/receipts, tested activation and startup-failure restoration, the existing owned-SSH update workflow, a Windows MSIX owner adapter, and package build tooling. Its checkbox remains open until a matching trusted signed MSIX has been installed/relaunched on a disposable packaged host. R9 remains open for the separately recorded physical-machine cases even after local automated checks pass.
- [x] Update README workflows, the feature status list, and relevant remote/preview architecture notes to match delivered behavior. Keep the review as a dated finding record.

The 123 passing focused tests in the review are the starting baseline, not evidence that these stages are complete. Mark each feature done only after its behavioral checks pass; report remaining unsupported deployment modes separately.
