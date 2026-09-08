# Remote access implementation and qualification

Implementation date: September 7, 2026. This accompanies [the completion plan](REMOTE-ACCESS-COMPLETION-PLAN.md).

## Compatibility

The wire protocol is now **20**. Update the desktop and host together. Protocol 19 hosts require an initial local/owner update before these RPCs can be used. No public update feed is assumed. Existing environment identities, grants, projects, and threads remain in their existing data directories.

Catalog journaling and session activity add SQLite tables and triggers. Catalog changes share the transaction that updates project/thread metadata. Each host lifetime has a new catalog epoch; clients resnapshot after an epoch change. Retention is 4,096 catalog changes, with snapshots paged in groups of 128 records.

## User workflows

- **Recovery:** transient failures keep retrying with capped backoff. Retry, activation, and network restoration use the same supervisor. Authentication, certificate, identity, and protocol failures require the corresponding user action. Unknown/changed SSH host keys stop recovery without prompting for a password; incompatible SSH bootstrap versions stop as compatibility failures. A TLS connection interruption alone does not imply a certificate mismatch. Reconnect synchronizes the catalog before reporting Ready and preserves the selected workspace/thread. Existing subscriptions replay or resnapshot; an open remote thread shows synchronization status.
- **Re-pairing:** pair with a fresh host link and choose Open. The existing client identity, window, drafts, and dirty file editors are retained while the new credentials are verified.
- **Address changes:** Connections → Saved environments → edit Host address → Test connection or Verify and save address. Verification checks the pinned certificate, environment, and protocol before saving. Persistence/connection failure restores the previous route.
- **Editing:** text saves support the existing 1 MiB UTF-8 limit through all listeners. Oversize content is rejected before dispatch; revision conflicts remain enforced. A mutation with an uncertain result is not automatically resent.
- **Synchronization:** project/thread catalog changes update other connected desktops. Active consumers share one stream per thread/terminal; closing the last consumer cancels the server stream. Inactive thread snapshots retain at most 20 entries, 32 MiB serialized size, and ten minutes of cache age, pruned on cache access.
- **Previews:** operate clients can discover host-local development servers and open them in the embedded preview through direct HTTPS or SSH. Discovered ports are host-wide candidates, not proof of project ownership. Each route uses an isolated temporary browser origin and a native, authenticated forwarding channel. Host control ports and non-loopback targets are rejected. HTTP, forms, assets, redirects, cookies, SSE and WebSocket forwarding are implemented. Upstream HTTPS uses normal certificate validation. Closing Preview releases its route and leaves the development server running.
- **Diagnostics:** Copy diagnostics exports route, state, failure category, retries, client/host versions, catalog state, and active stream counts. It excludes credentials, addresses, raw errors, project paths, and conversation content. Host session details show active connection count and recent activity. Expiring heartbeat leases avoid permanently stale online sessions after a crash.

## Host updates

Remote updates require operate access and an explicit opt-in by the host owner. Uploaded packages use a separate authenticated HTTP channel, bounded at 512 MiB. The host checks the declared length/hash and its owner's package policy before allowing activation. Uploads, validation, waiting, restart, success/failure and cancellation have durable request IDs and receipts. A client disconnect does not cancel an accepted activation.

**Connection-owned SSH server:** the existing Update / reconnect with bundled host command replaces the runtime owned by that connection. Its confirmation describes interruption of agents/terminals. A reused desktop or standalone server uses its own update owner.

**Standalone server:** start `PiStation.Server.exe supervise --data-root PATH --enable-remote-updates true` with the same optional `--pi-executable`, `--host`, and `--port` settings as `serve`. Stop an existing unowned server once before moving that data root to `supervise`. The launcher owns only the child it starts and holds an exclusive owner lock. ZIPs must contain a compatible Windows x64 server and `pistation-update.json`; extraction rejects traversal, duplicate paths and symlinks. The launcher stages immutable runtime directories, restarts its child, checks pinned health/environment/version, and records the result. Normal client disconnect leaves this owner running.

The standalone package trust policy deliberately permits packages supplied by approved operate devices; a SHA-256 check is an integrity check, not a publisher signature. The local opt-in and update confirmation disclose this policy. Runtime packages declare database compatibility version 1. Failed startup restoration has been tested with the current compatible schema; any future incompatible migration must change that compatibility contract before enabling rollback.

**Packaged desktop:** enable remote updates in the owning desktop's local Connections settings. The adapter checks package identity, publisher, architecture and version, then asks Windows to validate/stage the MSIX. Activation refuses unsaved editor/composer changes. A helper copied outside the package waits for the exact owning process to exit, installs with Windows signature enforcement, relaunches the app and verifies its existing host identity before recording success. This path needs an MSIX signed by the installed publisher and trusted on the host.

On a client, use **Install host update** (direct) or **Install owner-managed update** (SSH). Select the appropriate ZIP/MSIX, review the validated target and interruption policy, then Install. Activation waits for agents and terminals to finish unless interruption is explicitly selected. **Check update status** retrieves durable receipts after reconnect and can cancel pending activation. After an uncertain response, inspect the existing request rather than submitting another package. Hosts retain at most 32 staged request directories; the host owner can remove old completed package directories when that limit is reached.

Build artifacts with [Publish-RemoteUpdate.ps1](../Publish-RemoteUpdate.ps1):

```powershell
./Publish-RemoteUpdate.ps1 -Kind Server -Version 1.0.1.0
./Publish-RemoteUpdate.ps1 -Kind Desktop -Version 1.0.1.0 -CertificateThumbprint YOUR_PUBLISHER_THUMBPRINT
```

The script emits the artifact and its SHA-256 sidecar. Desktop packaging requires a valid matching publisher certificate with a private key in the current user's certificate store. It does not install certificates, alter trust, or deploy to a live environment.

## Qualification record

Automated verification uses isolated temporary data directories and FakePi. It does not modify the user's live host data, SSH daemon, firewall or certificate trust.

Verified focused cases include prolonged outage/manual recovery, interrupted TLS negotiation, authentication revocation, catalog convergence, consumer churn, supported file payloads on local/direct/SSH listeners, verified endpoint replacement and persistence rollback, forms/cookies/SSE/WebSockets through both remote listener types, owner policy/idempotency/cancellation, session activity, and standalone launcher activation/startup-failure restoration with preserved data/grants. The desktop helper's PowerShell was parsed and its embedded C# compiled without running deployment.

The solution build completes with zero warnings/errors and all **296 solution tests** pass (114 client runtime, 91 host, 36 Pi RPC, 29 protocol, 26 command-system tests). The visual contract passes 24 states, four responsive layouts, and three text scales. The packaged journeys found and drove fixes for an unnamed SSH settings container, workspace selection during catalog refresh, archive filtering after thread restoration, and the banner reconnect path clearing selection/draft context.

All **11 packaged UI journeys passed** across the corrected runs: DriverContract, DraftSlice, VerticalSlice, RecoverySlice, InteractionSlice, PiConfigurationSlice, ThreadLifecycleSlice, InputAccessibilitySlice, HardeningSlice, WorkbenchSlice, and CompatibilitySlice. Hardening verifies stop/isolation, reconnect/snapshot recovery, and no duplicate uncertain dispatch. Workbench verifies file/Git operations, terminal search/appearance and nested pane isolation, preview, narrow overlay geometry, and settings persistence across restart. The workbench harness retries only transient read-only UIA lookups; terminal-search navigation verifies its result after a stale-peer report, without replaying the action. Its preview fixture also shuts down without a blocking socket accept.

The final solution TRX files are in `TestResults/remote-complete-suite/`. The final code/compatibility log is `TestResults/remote-completion-validation.log`; the successful workbench log is `TestResults/remote-workbench-verified.log`. Earlier UI pass records are in `remote-ui-suite.log`, `remote-ui-suite-rest-2.log`, `remote-final-validation.log`, and `remote-ui-final-four.log` in the same directory. These are component checks across recorded runs, rather than a claim that the original pull-request script attempt was uninterrupted.

`Publish-RemoteUpdate.ps1 -Kind Server -Version 1.0.1.0` produced a Windows x64 package with protocol 20/database compatibility 1. The two standalone integration cases then passed using that package: **1.0.0 → 1.0.1 activation**, and deliberately failed startup followed by restoration of 1.0.0, retaining the environment, project, and device grant. Launcher restart also resolves unconfirmed activation receipts without overwriting completed results. The package is under `artifacts/remote-updates/bf9172d8f3354d97988f2c5e30084680/`, SHA-256 `E9F9A2A920A94D0A74F6CFED2123238C2A9F60ECB746F217CE87F795D5484A4F`.

| Feature | Implementation status | Qualification |
| --- | --- | --- |
| Automatic reconnect | Implemented | Outage, manual recovery, TLS interruption and revocation tests pass. |
| Stream recovery | Implemented | Replay/snapshot/synchronization markers and retained subscriptions covered. |
| Subscription management | Implemented | Shared leases, bounded cache and server stream cancellation covered. |
| Remote file editing | Implemented | Supported UTF-8 payloads save through all three listeners. |
| Multi-device synchronization | Implemented | Catalog convergence, atomic transfer, tombstones and revision precedence covered. |
| Authentication recovery guidance | Implemented | Structured blocked states and preserved re-pairing client identity. |
| Endpoint management | Implemented | Verified swap, wrong identity, persistence rollback and retained lease tested. |
| Remote updates | Partial qualification | Standalone activation/recovery tested; signed desktop activation still pending. |
| Remote previews | Implemented | HTTP and WebSocket data plane passes through both remote listener types. |
| Connection diagnostics | Implemented | Redacted export and durable expiring session activity covered. |

**Release qualification still requires:** a trusted publisher-signed desktop MSIX installation/relaunch on a disposable packaged host, and two physical Windows machines exercising direct/real OpenSSH access, sleep/wake, adapter changes and preview/update recovery. These cases cannot be certified by loopback fixtures. The workstation's publisher signing material and a second host have not been supplied; no production MSIX activation or two-machine run is claimed.

Visual review remains pending: both `winapp` window capture and `--capture-screen` returned entirely black images in this session. UI Automation and the static visual contract still provide behavioral/geometry evidence; those results do not certify the rendered appearance. The workbench and compatibility test helpers now normalize logical/physical pixels at the workstation's 125% display scaling rather than comparing different coordinate units.
