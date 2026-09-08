# Remote access: review and implementation plan

**September 7 follow-up:** this document records the initial release. The [completion implementation](REMOTE-ACCESS-IMPLEMENTATION.md) supersedes its original reconnect, re-pairing, update, and preview limitations. Protocol 20 adds persistent recovery, live catalog synchronization, verified address replacement, owner-mediated updates, and authenticated preview forwarding. Signed desktop update and physical two-machine qualification remain pending there.

## T3 Code review

Reviewed the local `C:/Users/Bacon21/Workspace/t3code` checkout at commit
`0a590fa01af66ec135d2ebf2d5542b08a37dc275` on 2026-09-06.

- `apps/desktop/src/backend/DesktopServerExposure.ts` separates local-only and
  network exposure and advertises reachable endpoints. Tailscale supplies another
  endpoint; it does not change the environment's domain model.
- `apps/server/src/auth/EnvironmentAuthPolicy.ts` distinguishes trusted desktop
  bootstrap from remote pairing. `PairingGrantStore.ts` issues expiring, single-use
  grants; `persistence/AuthPairingLinks.ts` consumes them atomically.
- `apps/server/src/auth/SessionStore.ts` persists client sessions and publishes
  revocations. `RpcAuthorization.ts` assigns scopes to individual RPC methods.
- `apps/desktop/src/settings/DesktopSavedEnvironments.ts` saves environment identity
  and endpoints, encrypts bearer tokens using Electron safe storage, and replaces
  registry files atomically.
- `docs/user/remote-access.md` describes direct LAN/private-network access,
  Tailscale HTTPS, managed SSH, and T3 Connect. Those transports all reach the same
  host-owned projects, agents, terminals, and files.

## PiStation implementation

1. Add shared pairing, device, and connection contracts without changing existing
   thread commands. Validate endpoints and encode invitation secrets in a URL
   fragment so they never become HTTP request paths or query strings.
2. Add an explicitly enabled HTTPS listener beside the existing authenticated
   loopback listener. Both use the same `EnvironmentService`; stopping remote
   access must leave local turns running. Bind a selected address and port. Persist
   a certificate identity with Windows-protected private-key storage.
3. Issue five-minute, single-use invitations. A receiving desktop pins the supplied
   SHA-256 certificate fingerprint, submits a device name and random client secret,
   and waits for approval on the host. Store only hashes of device credentials in
   SQLite. Support read-only and operate grants, expiration, listing, and revocation.
4. Authorize every remote HTTP request and hub invocation. Revocation terminates
   existing connections and streams. Keep access administration local to the host.
   Native .NET clients retain bearer headers on HTTP and WebSockets, so connection
   tickets and browser cookies are unnecessary in this release.
5. Store saved remote connections and credentials using Windows protection. Open
   each environment in an independent desktop window with its own ViewModel,
   connection supervisor, subscriptions, and layout. Verify the saved environment
   ID when reconnecting. Remote windows can coexist with the local window and each
   other; closing a remote window does not shut down the remote host.
6. Add Settings controls for starting/stopping sharing, generating invitations,
   approving/rejecting devices, revoking access, pairing, opening, and forgetting
   saved environments. Label remote state accurately. Disable local-only folder
   launch and loopback preview discovery in remote windows.
7. Test real HTTPS/SignalR pairing, pin mismatch, invitation replay/expiry, approval,
   read-only enforcement, revocation of an established stream, persistence across
   host restart, and a FakePi turn observed from independent local/remote clients.
   Build the WinUI app and run existing protocol/client/host tests and visual gate.

## Boundaries

Direct sharing delivers Windows desktop-to-desktop access over a reachable LAN or
VPN address and requires the hosting desktop to remain open. Managed SSH now also
supports a shared desktop/headless Windows environment, described below. Neither mode installs
a background service, changes firewall/router rules, creates a relay, or manages Tailscale.
Browser/mobile clients and remote preview tunneling remain future work. A remote
project path always denotes the host filesystem; attachments are selected on the
client and uploaded through the existing authenticated path.

## Progress

- [x] Review the T3 implementation and record the adaptation.
- [x] Implement host exposure and authorization.
- [x] Implement protected client connections and desktop workflow.
- [x] Verify behavior and update user documentation.

## Initial implementation verification record

- `dotnet build PiStationDesktop.slnx --no-restore`: zero warnings and errors.
- Code suites: 171 passing tests across protocol (28), RPC (36), host (64),
  client runtime (39), and commands (4). One pre-existing checkpoint integration
  test timed out during the concurrent code/UI run; the complete host suite then
  passed on its own (64/64). Its implementation and timeout were not changed.
- HTTPS tests use independent clients and a real Kestrel listener on this machine,
  including a FakePi turn, reconnect, certificate mismatch, invitation replay,
  read-only permissions, active-connection revocation, and host restart.
- Protected storage tests verify persistence, credential protection, and forgetting.
  The access store also excludes a second host process from using a stale device cache.
- The packaged driver verifies the Connections controls through UI Automation.
  The visual contract passes all 24 states, four responsive layouts, and three text scales.
  Sharing and client-settings screenshots were inspected; keyboard/UIA focus brings
  the connection controls into the visible scroll area.
- Physical two-machine networking and the other packaged workbench journeys were
  not exercised in this session. Firewall reachability remains deployment-specific.

## Review hardening

- Desktop shutdown contains failed/uncertain draft saves and independently releases
  resources. Re-pairing replaces a window's old client instead of reconnecting stale
  credentials. Replacement does not flush its old draft; in-flight composer work
  is canceled during teardown.
- Stopping sharing closes the listener before persisting its disabled preference.
  A persistence failure warns that sharing could resume on the next app launch.
- Read-only hub calls use passive persisted configuration/draft/history reads.
  They never launch or restart Pi, and existing subscriptions still observe activity
  started by local/Operate clients.
- A six-digit verification code is derived from the server-generated request ID
  and client credential. Both desktops calculate it independently; the host UI
  requires the user to confirm a match before approval.
- Pending approvals have a 30-second boundary allowance; approved results have a
  separate 45-second polling grace. Invitation redemption still expires after five
  minutes. The client bounds the overall attempt and explains expiry/rejection.
- Pairing is limited per source IP (120/minute) and globally (1,200/minute). Ordinary
  authenticated traffic is not charged to either pairing limit. Persisted expired
  devices are purged at startup, and approval rechecks the 100-device limit.
- Bounded, redacted diagnostics retain transport warning/error metadata and selected
  TLS authentication failures. The latter are Debug-level events in the
  [ASP.NET Core TLS middleware](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Servers/Kestrel/Core/src/Middleware/HttpsConnectionMiddleware.cs),
  so they are explicitly selected without enabling request-content logging.
- Network choices show adapter names/types, preserving VPN adapters and labeling
  link-local addresses. Settings explains re-pairing after revocation/expiry.

### Hardening verification

- Final solution build: zero warnings and errors, with analyzers enabled.
- Full serial code run (`dotnet test PiStationDesktop.slnx --no-build --no-restore -m:1`):
  **195 passed** — protocol 29, RPC 36, host 75, client runtime 46, desktop commands/lifecycle 9.
  A preceding concurrent run hit the existing Git checkpoint timeout; the complete
  host suite subsequently passed alone and again in the full serial run.
- Regressions cover exception-safe shutdown, stop/persistence failure ordering,
  saved-profile replacement decisions, approval boundary/grace, verification codes,
  expired-row cleanup, approval caps, rate-limit isolation, redacted diagnostics,
  and friendly HTTP pairing failures.
- Actual HTTPS/SignalR tests prove read-only RPCs do not start Pi on a fresh thread
  or restart a crashed runtime, and that passive viewers observe later local turns.
  A persisted-history test uses a process factory that throws if anything launches.
- Packaged UI/visual checks verify the connection controls and disabled approval
  controls before verification; sharing/client settings screenshots were inspected.
  Physical two-machine reachability and a complete interactive two-device pairing
  journey remain untested.

## Windows managed SSH

The T3 checkout uses native `ssh.exe` on a Windows client, but its remote launch
script is POSIX `sh`/`nohup`, not a native Windows bootstrap. PiStation adapts the
same start/reuse/forward model with an encoded PowerShell command and a headless
`PiStation.Server.exe` (`attach` and foreground `serve` modes).

- The host is bundled with the desktop (or explicitly preinstalled), discovers Pi, holds an exclusive environment-data
  lock, binds HTTPS only to loopback, and exposes the same remote RPC allowlist.
- A Windows current-user-only named pipe discovers a running desktop or headless host.
  Credentials and the certificate private key are DPAPI-protected at rest; bootstrap
  metadata travels over authenticated SSH, not command arguments or a plaintext file.
- An open SSH control session owns a host it starts. Disconnect stops that owned
  host; a separately started host is left running. Other clients depending on an
  owned host lose access if its owner disconnects. This is not a Windows service.
- Native OpenSSH handles aliases, keys and agents. Host-key verification is strict;
  first-use verification happens in the user's terminal. Config/known-host discovery
  preserves explicit ports, and authentication failures offer up to two password
  retries through an in-app prompt and an OpenSSH askpass helper. Secrets are kept
  only for the connection. Service setup and Linux/macOS hosting remain out of scope.
- New connections share the desktop default data directory. Existing profiles retain
  their former SSH-only default; package-local desktop data is reused in place.
  The MSIX package disables file-write virtualization to keep shared files visible
  to the standalone host, including custom data roots and project paths.
- Empty server paths use a self-contained Windows x64 host archive produced by the
  desktop build. A bounded, checksum-verified SSH upload stages a content-addressed
  installation before activation. No environment data or installed version is replaced.
  An explicit Update/reconnect action can restart a connection-owned host; externally
  owned hosts require updating at their source and are never stopped by that action.
- The desktop owns the control/tunnel processes in Windows jobs. Closing the app
  also closes those jobs. A forwarding-only failure repairs only the forward,
  retaining a healthy control session and host. SignalR reconnects re-establish
  transport and preserve client/environment identity; uncertain writes are not resent.
- Settings saves protected SSH profiles without transient ports or bearer tokens.
  Connections can be canceled, reopened, disconnected and forgotten. Forwarded
  HTTPS and WebSockets still pin the certificate learned over SSH.

The earlier SSH implementation's validation included native PowerShell launch/reuse/stop with FakePi, persistent
host identity, wrong-pin rejection, single-instance exclusion, Windows job cleanup,
and a real TLS/SignalR session automatically reconnecting across a dropped TCP
forward without restarting the host. The self-contained Windows x64 host publishes
successfully to `artifacts/ssh-host`; see README for deployment steps. The new UI
controls compile and are included in the driver contract, but visual verification
was blocked by Windows screen-capture/activation access errors. No two-machine SSH
test, SSH service installation, firewall changes or user SSH-key changes were performed.

Before the shared-host/install/password changes, solution build completed with zero warnings/errors; all
**213 code tests passed** (client runtime 64, desktop lifecycle 9, host 75, Pi RPC 36,
protocol 29), including 18 new SSH cases. The static visual contract passed all
24 states, four responsive layouts and three text scales. The published host's
`--help` command was also smoke-tested. Those results do not validate the subsequent
shared-host, installer or password changes. Focused regression cases were added for
this batch; execution and UI validation were skipped at the user's request. Changes
remain uncommitted.

This batch compiles with zero warnings/errors in the desktop (including its bundled
host), client-runtime tests and host tests. Installer/askpass PowerShell syntax and
`git diff --check` also pass. These are compile/static checks, not a two-machine
installation, password-authentication, update, or UI runtime validation.

## T3-style authentication CLI

Implemented `pair`, `status`, `auth pairing create/list/revoke`, and
`auth session issue/list/revoke`, with labels, lifetimes, JSON, terminal QR and
session token-only output. Added `auth pairing pending/approve/reject` because
PiStation retains its verification-code approval step, including on a headless host.

Like T3's `apps/server/src/cli/auth.ts` and SQLite runtime layer, administration
opens the shared authentication database directly. There is no privileged
authentication-management HTTP or named-pipe RPC. PiStation preserves its existing
ReadOnly/Operate access levels instead of copying T3's administrative scope model.
The current-user discovery pipe is used only to find/probe a running host and
obtain its actual pairing endpoint; list/status never serialize bootstrap secrets.

- Pairing invitations, pending approvals, and device sessions share SQLite WAL with
  a five-second busy timeout. Immediate transactions make caps, single-use redemption,
  and approvals atomic across CLI/host processes. Only credential hashes are stored.
  Existing device rows migrate in place; an older exclusive-cache host still blocks
  concurrent access through its compatibility lock.
- HTTP authentication and every hub invocation recheck the authoritative database.
  A one-second revocation monitor also aborts idle/streaming connections after a
  different process revokes access; an in-flight operation is not rolled back.
- Both the direct listener and the headless loopback listener expose bounded,
  rate-limited pairing and enforce the device permission policy. The SSH bootstrap
  credential remains full Operate and loopback-only; CLI session revocation does
  not revoke SSH account access.
- Headless `serve --host IP --port PORT` explicitly opts into LAN/VPN exposure,
  reusing the host environment and the desktop's protected LAN certificate (separate
  from the SSH bootstrap identity). Default `serve`/`attach`
  remains loopback-only. No firewall, service, SSH or VPN settings are changed.
- `pair` requires a discovered, responsive host. Offline `auth pairing create`
  can issue a token without a link; explicit HTTPS origin + pin can produce a link.
  Pairing defaults to five minutes, CLI sessions to 30 days; approved devices remain
  180 days. Expired/revoked clients must re-pair. No automatic renewal was added.
- Regression cases were added for independent-store redemption/revocation, migration,
  metadata/expiry, CLI parsing/redaction, headless pairing approval and read-only policy.
  Test execution, UI checks, and two-machine validation remain skipped at the user's
  request. Compile/static results for this batch are recorded separately below.

CLI batch compile/static checks: desktop (including the self-contained bundled
server and QR dependency), client-runtime tests, and host tests all build with zero
warnings/errors. `git diff --check` passes. Tests were compiled but not executed;
no host was started against user data and no new real authentication grants were
created. These results do not establish runtime or two-machine validation.

## Connections Settings parity

The desktop now lists and individually revokes unused invitations, accepts an
optional label and a 1–1440 minute link lifetime (five minutes by default), and
renders an in-memory pairing QR. Like T3's Connections page, it retains recoverable
links only for invitations created during the current Settings visit. Leaving the
page clears those secrets without revoking grants. Consumption, expiry, revocation,
or stopping sharing removes cached links; failed state refresh also clears them.
No QR image is sent to a service or written to a file. The copy button opts out of
Windows clipboard history and roaming; manually copying selected text does not.

Device-session rows show access and expiration; selection also shows subject and
ID, including CLI-issued sessions. Metadata changes refresh independently, keeping
selection stable without resetting unrelated approval confirmation. Actions capture
the clicked record before refreshing so they cannot target a newly selected row.
Asynchronous QR loads cannot restore an old display after selection changes or
Settings closes. Management remains local to the host; Tailscale and remote
administrator scopes are not added by this UI batch.

Focused state/input/metadata/QR regression cases and UI Automation contract entries
were added. Test execution and visual/runtime checks remain skipped at the user's
request; only compilation and static checks are performed for this batch.

Final UI-batch checks: desktop (including its bundled host) and command/state test
project compile with zero warnings/errors. UI contract PowerShell syntax and
`git diff --check` pass. No tests or UI journeys were executed.
