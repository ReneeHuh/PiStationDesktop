# Remote access review: PiStationDesktop2 compared with T3 Code

Reviewed on September 7, 2026. PiStation checkout: `5c1516c`; T3 checkout: `0a590fa01` at `C:\Users\Bacon21\Workspace\t3code`.

PiStation has a substantial remote implementation: HTTPS pairing, read-only and operate grants, active-session revocation, independent remote windows, managed Windows SSH, host discovery, and bundled host installation. Its core trust and process-ownership decisions are sound in the paths reviewed. The most urgent problems are connection recovery and RPC payload limits, followed by subscription lifetime and authentication error presentation.

This is a source review of PiStation's remote access with T3's local checkout as the comparison reference, supported by focused PiStation tests and isolated HTTPS reproductions. It is not a complete security audit of T3. No production source was changed. The two existing modified documentation files were left untouched.

## Actionable findings

### 1. [P1] Reconnection can leave chat and terminal subscriptions waiting forever

**Location:** [ConnectionSupervisor.cs:150](../src/PiStation.ClientRuntime/ConnectionSupervisor.cs#L150), with consumers in [ThreadSubscription.cs:51](../src/PiStation.ClientRuntime/ThreadSubscription.cs#L51) and [TerminalSubscription.cs:57](../src/PiStation.ClientRuntime/TerminalSubscription.cs#L57).

When SignalR begins reconnecting, `OnReconnectingAsync` replaces `_connected` with an incomplete task completion source. Subscription loops wait on that task. If automatic retries run out, `OnClosedAsync` replaces it again without completing or canceling the previous task. A later manual reconnect completes only the new task. Existing subscriptions remain blocked on the abandoned task, even though the client reports `Connected` and accepts new commands.

**Reproduced:** connected a client and thread stream to an isolated HTTPS host, stopped its listener until the client's default retries finished, restarted the listener at the same address with the same identity, and explicitly reconnected. A consumer waiting during retries remained incomplete. After an acknowledged turn, a fresh observer received two messages while the original subscription still had zero.

This also affects terminal subscriptions through the same supervisor wait. Changing threads is not a reliable workaround because the runtime caches and reuses the old thread subscription. Closing and reopening the remote window creates a new runtime.

**Correction:** preserve pending waiters across retry-to-disconnected transitions, or use an explicit connection-generation/state notification mechanism that always wakes waiters when their generation ends. Wait for identity validation and synchronization before exposing a usable connection. Cover retry exhaustion followed by manual reconnect, manual reconnect during automatic retry, and resumed chat and terminal streams.

**T3 comparison:** [the supervisor](../../t3code/packages/client-runtime/src/connection/supervisor.ts#L858) maintains connection intent and publishes scoped session replacements; [thread state](../../t3code/packages/client-runtime/src/state/threads.ts#L650) follows session changes and resubscribes with a cursor. PiStation can adopt those lifecycle properties without adopting Effect.

### 2. [P1] Valid file saves exceed the transport limit and disconnect the client

**Locations:** [RemoteEnvironmentHost.cs:62](../src/PiStation.Host/Hosting/RemoteEnvironmentHost.cs#L62), [SshEnvironmentHost.cs:118](../src/PiStation.Host/Hosting/SshEnvironmentHost.cs#L118), and [EmbeddedEnvironmentHost.cs:67](../src/PiStation.Host/Hosting/EmbeddedEnvironmentHost.cs#L67).

All three listeners leave SignalR's `MaximumReceiveMessageSize` at its resolved default of **32,768 bytes**. The file contract permits **1,048,576 UTF-8 bytes** through [FileReadDefaults](../src/PiStation.Protocol/Models/FileSearch.cs#L141), and file content travels inside a JSON hub invocation. Increasing Kestrel's attachment request-body limit does not increase the hub-message limit.

**Reproduced:** read a file revision, then submitted a 40,000-byte ASCII replacement through the authenticated HTTPS client. The server closed the connection with a hub error. Passing the identical save request directly to `EnvironmentService.SaveProjectFileAsync` succeeded and wrote all 40,000 bytes. This is shared by local and remote transports; it is not exclusive to remote access.

**Correction:** align the transport and operation limits across all listeners. Either configure a bounded hub-message allowance that accounts for the largest supported JSON representation, including escaping, or move large file writes to a bounded authenticated HTTP endpoint. Reject oversized input with a useful operation error. Do not make message limits unlimited. Add tests around the transport boundary and the advertised file limit, including non-ASCII and heavily escaped content.

**T3 comparison:** T3 has a different WebSocket RPC stack and separates large thread snapshots into HTTP reads with pagination. Its exact inbound file-write limit was not exercised here; this finding comes from PiStation's executable behavior, not an assumption that T3's transport is unlimited. Relevant separation: [thread snapshot loading](../../t3code/packages/client-runtime/src/state/threads.ts#L710).

### 3. [P2] Every visited thread retains a live stream until the whole client closes

**Locations:** [ShellViewModel.cs:457](../src/PiStation.App/ViewModels/ShellViewModel.cs#L457) and [EnvironmentClient.cs:826](../src/PiStation.ClientRuntime/EnvironmentClient.cs#L826).

Changing the selected thread removes the view's event handler but does not release its subscription. `EnvironmentClient.SubscribeThread` stores every subscription in a dictionary; that dictionary is cleared only when the entire client is disposed. Each subscription continues receiving and applying events for an offscreen thread.

Consequently, navigation increases the number of live detail streams, retained projections, and reconnect work for the lifetime of the remote window. Active offscreen threads continue consuming network bandwidth and client processing. The retention is established from source; no bandwidth or memory threshold was benchmarked.

**Correction:** separate a bounded snapshot/cursor cache from live subscriptions. Release a stream when its last consumer leaves and recreate it on demand. Add removal handling for disposed thread subscriptions, as terminal subscriptions already have; simply disposing the current object would otherwise leave a dead object in `_subscriptions`.

**T3 comparison:** [threads.ts:816](../../t3code/packages/client-runtime/src/state/threads.ts#L816) deliberately separates cached resume data from live resources. Live atoms have zero idle TTL, while cached snapshots can outlive them without retaining environment or RPC scopes. This is a particularly useful pattern for PiStation's remote performance.

### 4. [P2] Revoked clients finish automatic reconnect in the wrong recovery state

**Location:** [ConnectionSupervisor.cs:150](../src/PiStation.ClientRuntime/ConnectionSupervisor.cs#L150); affected presentation: [ShellViewModel.cs:2435](../src/PiStation.App/ViewModels/ShellViewModel.cs#L2435).

Authentication rejection is classified only in the explicit `ConnectAsync` catch. Automatic retries eventually call `OnClosedAsync`, which always publishes `Disconnected`. The UI's specific expired/revoked-access guidance is displayed only for `AuthenticationRequired`, so revoking an already-connected device initially produces generic connection recovery instead of the re-pair instructions.

**Reproduced:** revoked a connected client's grant using the shared access store. Its subsequent states were `Retrying, Disconnected`; it never entered `AuthenticationRequired`. Authorization enforcement itself worked: the connection was terminated and the credential was rejected. A subsequent explicit connection attempt can reach the correct authentication state.

**Correction:** carry structured authentication failures through automatic reconnect, stop retrying a rejected credential, and publish the state that offers re-pairing. Distinguish certificate/identity failures and transient connectivity failures as well, without relying solely on exception-message substring matching.

**T3 comparison:** [connection/errors.ts:120](../../t3code/packages/client-runtime/src/connection/errors.ts#L120) maps invalid authentication to a blocked authentication failure, while network failures remain transient. [The supervisor](../../t3code/packages/client-runtime/src/connection/supervisor.ts#L917) handles those categories differently.

## Comparison of the implementations

| Area | PiStation today | T3 in the reviewed checkout | Assessment |
| --- | --- | --- | --- |
| Execution boundary | Remote clients use the host's environment, projects, Pi processes, files, Git, and terminals. | Same environment-owned execution model. | Keep this architecture. |
| Direct trust | HTTPS required; invitation delivers a SHA-256 certificate pin; HTTP and WebSocket clients enforce it and do not follow redirects. | Supports direct pairing, browser sessions, bearer/DPoP authorization, and HTTPS routes supplied by hosting/tunnel infrastructure. | PiStation's approach fits its native Windows client. |
| Pairing | Random single-use invitation, client-generated credential, host approval, six-digit verification code, local QR generation, fragment-held secret. | Single-use pairing delegates scopes; hosted pairing keeps the secret in a fragment. | PiStation's explicit host approval is a useful property to retain. |
| Stored secrets | Server stores credential hashes; Windows DPAPI protects saved client credentials and durable certificate material. | Separate credential/session services and platform-specific persistence. | PiStation already has appropriate Windows primitives. |
| Authorization | Explicit RPC allowlist; read-only or operate; authentication administration is local UI/CLI. | Explicit RPC scope map with separate orchestration, terminal, access-management, relay, and review scopes. | T3 is more flexible; extra scopes are a product decision, not a prerequisite for fixing remote reliability. |
| Revocation | Per-call validation plus active-connection cancellation; cross-process SQLite changes are monitored. | Session revocation and auth-access streams. | Enforcement works in PiStation; improve client recovery messaging. |
| Reconnect | Four retries with delays of 0, 1, 2, and 5 seconds, then manual recovery. No network/wake-driven supervisor. | Persistent capped backoff, network status, wake probes, explicit blocked states, session replacement. | High-value next improvement after findings 1 and 4. |
| Detail synchronization | Thread/terminal cursors, epochs, replay, snapshot fallback, bounded host journals. | Cursor replay, completion markers, scoped streams, cached/paginated HTTP snapshots. | Preserve PiStation's recovery primitives; fix lifecycle and bound retained detail. |
| Multi-device catalogs | Project/thread lists refresh through reads and local command responses; only thread/terminal detail has streams. | Lightweight environment shell stream plus separate detail streams. | New projects/threads and metadata do not automatically appear everywhere in PiStation. |
| Managed SSH | Strict OpenSSH trust, config aliases/ports, password helper, pinned forwarded HTTPS, Windows x64 bundle, ownership-aware cleanup. | Desktop-managed launch/forward/reconnect with POSIX shell/Node bootstrap and ownership rules. | PiStation's Windows path is substantial; it does not provide a Linux/macOS SSH host implementation. |
| Host lifetime | Sharing listener can stop without stopping local work. A managed SSH control session owns only a server it starts. | Also separates access from process ownership; background services and update handoffs are supported. | Keep ownership rules; service-style hosting is a separate expansion. |
| Endpoints | One saved direct HTTPS origin per environment; desktop sharing binds a selected IPv4 address. SSH profiles are stored separately. | Shared connection catalog and advertised endpoint metadata; Tailscale HTTPS and T3 Connect routes. | Address migration and reachability assistance are materially less developed in PiStation. |
| Internet reachability | User supplies a reachable LAN/VPN address or SSH route. | Tailscale Serve integration and managed T3 Connect tunnels/account discovery. | Real feature gap; pairing alone cannot solve reachability. |
| Client surfaces | Windows native desktop. | Web, Electron desktop, and native mobile. | PiStation's QR is currently a credential-transfer aid, not a mobile client. |
| Remote previews/editors | Host-local editor launch and preview discovery are excluded from remote capabilities; users supply reachable preview URLs. | Additional remote preview/open-target infrastructure. | An intentional limitation, not an auth defect. |
| Diagnostics | Bounded metadata-only remote log; no formatted request state or raw exception messages in the diagnostic provider. | Typed errors, tracing, connection stages, diagnostic IDs and richer runtime status. | Preserve secret-safe logging while adding actionable connection state. |

Reference points for the comparison: [T3 remote architecture](../../t3code/docs/internals/remote.md), [environment authentication](../../t3code/docs/internals/environment-auth.md), [RPC policy](../../t3code/apps/server/src/auth/RpcAuthorization.ts), [connection catalog](../../t3code/packages/client-runtime/src/connection/catalog.ts), [advertised endpoints](../../t3code/packages/contracts/src/remoteAccess.ts), [SSH implementation](../../t3code/packages/ssh/src/tunnel.ts), [Tailscale integration](../../t3code/packages/tailscale/src/tailscale.ts), and [T3 Connect trust model](../../t3code/docs/internals/t3-connect.md).

## Important gaps to address separately from the defects

**Persistent recovery.** The default retry ladder has eight seconds of scheduled delay in total, plus connection-attempt time. A longer outage leaves a healthy saved connection disconnected until the user intervenes. The isolated test confirmed that a fresh client could connect after the host returned while the original remained disconnected. Preserve the user's intent to remain connected, use capped backoff, and react to network restoration and resume from sleep. Permanent authentication or identity failures should wait for a user correction.

**Live project and thread lists.** A second connected client does not learn about a newly created thread automatically. The isolated two-client check confirmed that the second client's `ThreadMetadata` still lacked the thread after creation and another successful RPC round trip. The host has no environment catalog stream to deliver it. [T3's shell stream](../../t3code/packages/client-runtime/src/state/shell.ts#L187) provides a suitable model: lightweight metadata for all threads, full detail only where needed. Add this with cursor/snapshot recovery rather than keeping every thread detail stream open.

**Endpoint changes without re-pairing.** [RemoteAccessController](../src/PiStation.App/Composition/RemoteAccessController.cs#L74) requires its saved listener address to be an active IPv4 address. [RemoteConnectionStore](../src/PiStation.ClientRuntime/RemoteConnectionStore.cs#L49) saves one endpoint per environment; the settings surface has no direct endpoint-edit workflow. DHCP or adapter changes can therefore require manual host reconfiguration and a new pairing link even when the environment and certificate identity remain valid. Allow a route to change while retaining credentials, verifying the certificate and expected environment before saving it.

**Reachability and platform expansion.** Tailscale discovery/status and a verified private-network endpoint are a smaller first expansion than a hosted relay/account system. Linux/macOS host support requires a server/runtime and bootstrap design beyond changing an SSH command. Web/mobile support likewise needs real clients and a browser-compatible authentication/asset transport strategy; the current native pinning callback cannot simply be moved into a browser.

**Session visibility.** PiStation lists grant identity, label, access, expiry, and optional subject, but does not track last-used/connected status. T3's session model includes connection metadata. Such visibility would make it easier to identify and revoke an obsolete device without changing the existing trust model.

## Security properties verified or checked in source

- The remote HTTPS listener rejects the desktop's separate loopback bootstrap credential.
- Wrong certificate pins and unexpected environment identities fail connection setup.
- Every exposed hub method has an explicit access policy or local-only decision; a reflection-based test guards policy coverage.
- Read-only access rejects mutation RPCs and attachment uploads. Passive draft/configuration/subscription reads do not launch or restart Pi.
- Pairing invitations cannot be replayed; invitations, pending requests, device counts, request sizes, and pairing rate are bounded.
- Host/CLI stores share SQLite transactions, so approval and revocation do not rely on a stale process-local credential cache.
- Stopping direct sharing leaves local work running. SSH forward-only repair preserves a healthy owned control session; cleanup does not stop an externally owned host.
- SSH uses strict host-key checking, encoded literal remote paths, disabled agent forwarding, bounded bootstrap output, and a hash-verified bundled transfer.

No authentication or read-only authorization bypass was found in the reviewed paths. T3's DPoP, socket tickets, and browser cookie support solve additional client/broker requirements; their absence is not itself a vulnerability in PiStation's native, pinned-TLS, Authorization-header flow.

## Validation and limits

**123 existing focused tests passed:**

| Test project and filter | Passed |
| --- | ---: |
| ClientRuntime: `Remote`, `Ssh`, `Reconnect`, or `Connection` | 72 |
| Host: `Remote` or `PassiveRemote` | 23 |
| CommandSystem: `Remote` or `DesktopLifecycle` | 22 |
| Protocol: `RemoteInvitation` or `Ssh` | 6 |

All were run with `dotnet test`, `--no-restore`, a `FullyQualifiedName` filter, and the minimal console logger. Passing tests do not contradict the findings: the existing paired-client integration test reconnects explicitly, and the listener-restart test creates a fresh client. They do not cover exhausted automatic retries with a retained live subscription, or a valid file save above the hub-message limit.

The additional [reproducer source](C:/Users/Bacon21/AppData/Local/Temp/pistation-remote-review-c44247e61123426eae4ad45921296a01/Program.cs) is outside the repository. It references the current Host and ClientRuntime projects, creates fresh isolated data, uses loopback HTTPS with a generated test certificate, and invokes only the FakePi test executable for turns. Run `dotnet run --project ReviewProbe.csproj --no-restore` in that directory; `-- --limits` resolves the installed SignalR message limit without starting a host.

Observed results:

```text
Host reachable through fresh client: Connected; original client: Disconnected
Original client after explicit reconnect: Connected; consumer registered during retry completed: False
Other client thread catalog knows new thread without refresh: False
After acknowledged turn: fresh observer messages=2; original subscription messages=0
40,000-byte file save over HTTPS: HubException: ... Connection closed with an error.
Same save directly through host service: succeeded, 40000 bytes
Revocation recovery states: Retrying, Disconnected; final state: Disconnected
Resolved SignalR MaximumReceiveMessageSize: 32768
```

No live remote machine, real SSH login, Windows Firewall change, Tailscale configuration, WinUI interaction, or T3 service was used. Real-machine checks remain useful for sleep/wake, adapter changes, SSH password prompts, restrictive forwarding policies, and actual slow-link behavior. Subscription bandwidth and long-session memory were not benchmarked.

## Suggested implementation order

1. Fix the abandoned connection waiters and add outage/reconnect stream tests.
2. Align file/RPC payload limits across direct, SSH, and local listeners.
3. Classify authentication/identity failures during reconnect and add persistent recovery for transient failures.
4. Separate cached projections from live streams; add the lightweight environment catalog stream.
5. Add verified endpoint editing, network status, and private-network discovery.
6. Expand host platforms, background hosting, browser/mobile clients, or managed tunnels according to the intended product scope.

The current environment boundary, certificate pinning, host-approved pairing, shared auth store, passive read-only behavior, and ownership-aware SSH lifecycle should remain the foundation.
