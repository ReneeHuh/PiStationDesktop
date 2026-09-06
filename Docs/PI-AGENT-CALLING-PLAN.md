# Pi Station Desktop — Pi Calling Plan

Status (2026-09-04): **Phases 0–3, the first vertical slice, and protocol-v20 active-turn queues are implemented and validated.**
Phase 4 has not started. Phase 5 is partially implemented through bounded local operation,
recovery states, diagnostics, and deterministic failure tests; remote and sustained load work
remain.

This is a clean-sheet implementation plan for calling Pi from a C# WinUI 3 application and
serving that interaction to another computer running the same application. It covers only the Pi
runtime boundary, the environment host, the client connection, streaming, recovery, and the
minimum security required for remote control.

It deliberately does not cover general UI design, Git workflows, worktrees, terminals, cloud
relays, mobile/browser clients, or multiple agent providers.

## 1. Target behavior

Every installed application can perform two roles at the same time:

1. **Client:** Display and control local or remote Pi threads.
2. **Environment host:** Own local projects, Pi processes, Pi sessions, and authoritative thread
   state.

A thread always belongs to one environment. Connecting two applications does not replicate or
synchronize the thread between computers. The client controls the thread on its owning computer.

```text
Computer A                                      Computer B
┌─────────────────────┐                         ┌─────────────────────┐
│ WinUI client        │                         │ WinUI client        │
│ ClientRuntime       │◄──── HTTPS/SignalR ────►│ ClientRuntime       │
└──────────┬──────────┘                         └─────────────────────┘
           │ loopback
┌──────────▼──────────────────────────────────────────────────────────┐
│ Environment host on Computer A                                    │
│                                                                    │
│ ThreadController → PiRpcConnection → pi --mode rpc                 │
│                                                                    │
│ Owns project files, Pi session files, credentials, and processes  │
└────────────────────────────────────────────────────────────────────┘
```

The local client uses the same logical contract and SignalR connection as a remote client. There
must not be a direct ViewModel-to-Pi path for local threads.

### Required outcomes

- Start and resume a persistent Pi session in a selected project directory.
- Send prompts, steering messages, follow-ups, aborts, model changes, thinking changes, and
  extension UI responses.
- Stream assistant text, reasoning, tool execution, queue state, retry state, compaction state, and
  extension UI requests.
- Allow several clients to observe the same thread.
- Continue a running Pi turn when all clients disconnect.
- Reconnect without missing or duplicating projected events.
- Retry a network command without sending the same prompt to Pi twice during normal connection
  failure.
- Recover a durable Pi session after the host restarts.
- Keep slow clients from blocking Pi stdout or growing host memory without a bound.

## 2. Architectural boundaries

Use five logical components. They may begin as projects in one solution, but their dependency
direction must remain fixed.

```text
PiStation.App ─────────────► PiStation.ClientRuntime ───► PiStation.Protocol
    │
    └──────────────────────► PiStation.Host ────────────► PiStation.Protocol
                                      │
                                      └───────────────► PiStation.PiRpc
```

### PiStation.App

- WinUI windows, controls, navigation, notifications, and tray lifetime.
- ViewModels depend only on `IEnvironmentClient` from ClientRuntime.
- Starts the embedded local host as the application composition root.
- Does not parse Pi JSON, manage processes, or open host storage.

### PiStation.ClientRuntime

- Owns one connection supervisor per known environment.
- Exposes commands and observable thread projections to ViewModels.
- Reconnects, resubscribes with cursors, and stores client-owned state.
- Treats local loopback and remote endpoints identically after bootstrap.

### PiStation.Protocol

- Contains versioned network DTOs only.
- Defines identifiers, commands, receipts, snapshots, stream envelopes, errors, environment
  descriptors, capabilities, and pairing messages.
- Uses `System.Text.Json` source generation.
- Does not expose Pi wire DTOs.

### PiStation.Host

- Owns projects, threads, Pi controllers, subscriptions, host persistence, pairing, and
  authorization.
- Translates application commands into operations on `PiThreadController`.
- Maintains the authoritative live `ThreadProjection`.
- Never lets a network subscriber directly consume Pi stdout.

### PiStation.PiRpc

- Locates and validates the Pi executable.
- Launches and shuts down `pi --mode rpc`.
- Implements exact JSONL framing, request correlation, event decoding, and stderr diagnostics.
- Contains no WinUI, SignalR, host database, or network-contract dependencies.

## 3. Identity and ownership

Define strongly typed identifiers for every boundary:

| Identifier | Owner | Purpose |
| --- | --- | --- |
| `EnvironmentId` | Host | Stable identity for one installation/environment. |
| `ClientId` | Client | Stable identity for one paired application installation. |
| `ProjectId` | Host | Identifies one host-local working directory. |
| `ThreadId` | Host | Identifies an application thread in a project. |
| `TurnId` | Host | Identifies one user-to-agent run. |
| `CommandId` | Client | Idempotency key for a network mutation. |
| `PiRequestId` | PiRpc | Correlates one Pi command and response. |
| `SessionGeneration` | Host memory | Rejects events from an obsolete Pi process binding. |
| `ProjectionEpoch` | Host | Invalidates all previous stream cursors after continuity is lost. |
| `Sequence` | Host | Orders projection events within an epoch and thread. |

Every client cache key, command, snapshot, notification, and subscription must include
`EnvironmentId`. Raw `ThreadId` values from two environments are allowed to collide without
cross-talk.

## 4. Pi process lifecycle

Use one Pi process per active thread. Do not pool or switch processes between threads in the first
implementation.

### Starting a new thread

1. Validate that the project directory still exists and remains allowed by host policy.
2. Resolve the configured Pi executable to a canonical absolute path.
3. Run `pi --version` and validate it against the application's tested compatibility range. The
   first range begins at `0.84.4`, which supplies `clear_queue` and the required Windows abort/resume
   fixes.
4. Generate a Pi-compatible session ID, persist it with the new application thread, and commit that
   mapping before process launch.
5. Start Pi with the project directory as `WorkingDirectory` and pass
   `--mode rpc --session-dir <canonical-host-session-root> --session-id <known-id>`. The session
   root belongs to the environment host and remains below its application data root. Do not pass
   `--no-session` for a durable application thread.
6. Redirect stdin, stdout, and stderr with strict UTF-8 and no shell. Configure stdin with
   `new UTF8Encoding(false, true)` so the first JSON record has no BOM.
7. Start all three pipe consumers before sending a command.
8. Call `get_state`, verify that its `sessionId` matches the preassigned ID, and record the reported
   `sessionFile`.
9. Treat a reported session path whose file does not exist yet as provisional, not as a failure.

`--session-id` is part of the pinned Pi CLI compatibility surface even though it is not listed in
the RPC command section. It opens an exact project session when present and creates it when missing.
The compatibility suite must verify both behaviors for every supported Pi range.

### Windows launcher resolution

Do not execute an npm `pi.cmd` shim through `cmd.exe`. Resolve the real launcher before starting the
runtime:

1. If the configured Pi path is a native executable, launch it directly.
2. If it is an npm `pi.cmd`, locate the corresponding
   `@earendil-works/pi-coding-agent/package.json`.
3. Read and validate the `bin.pi` entry, resolve links, and prove that the resulting CLI file remains
   inside the package root.
4. Resolve a compatible `node.exe` from the installation or `PATH` and launch
   `node.exe <absolute-cli-path> <pi-arguments>` using `ProcessStartInfo.ArgumentList`.
5. If a command shim cannot be resolved safely, fail with an actionable setup error instead of
   falling back to shell execution.

Direct Node launch removes command-shell injection from ordinary startup. It does not remove the
need for bounded process-tree termination because Pi tools may create descendant processes.

### Resuming a thread

1. Validate the owning project directory and the saved session identity.
2. Always pass the thread's canonical host `--session-dir`. When a validated session path is
   available, start Pi with `--mode rpc --session <absolute-session-path>`. When the path was not
   recorded before a crash, start with `--mode rpc --session-id <known-id>` so Pi opens the exact
   project session if it was persisted or recreates that known provisional identity if it was not.
3. Increment `SessionGeneration` before binding the new process.
4. Hydrate using `get_state`, verify the returned session identity, then query `get_entries`,
   `get_session_stats`, `get_available_models`,
   `get_available_thinking_levels`, and `get_commands`.
5. Rebuild the active conversation branch using entry IDs, parent IDs, and the returned leaf ID.
6. Publish one reconciled projection before accepting new mutations.

### Idle and shutdown behavior

- Start a process lazily when a thread first needs Pi.
- A settled process may be reaped after an idle timeout only when it has no pending command,
  queued message, extension UI request, retry, compaction, or active turn.
- Closing a client tab only unsubscribes the client. It does not stop Pi.
- Closing the last window keeps the application in the notification area while local Pi work or
  remote hosting is active.
- Explicit application Quit performs bounded graceful shutdown: clear queued input if requested by
  the user, abort active work, cancel pending UI requests, close stdin, wait for exit, and only then
  kill the captured process tree as a fallback.
- An unexpected exit fails every pending Pi request, resolves every pending interaction as
  unavailable, and marks the thread runtime crashed. Recovery always starts a new process; it never
  attempts to reattach pipes to an existing PID.

## 5. PiRpcConnection

`PiRpcConnection` is a multiplexed request/event transport, not a sequential request loop.

```csharp
public interface IPiRpcConnection : IAsyncDisposable
{
    Task<PiResponse> RequestAsync(PiCommand command, CancellationToken cancellationToken);
    Task SendNotificationAsync(PiNotification notification, CancellationToken cancellationToken);
    IAsyncEnumerable<PiEvent> ReadEventsAsync(CancellationToken cancellationToken);
    Task<PiExit> Completion { get; }
}
```

`RequestAsync` is only for commands that produce a correlated `response` record.
`SendNotificationAsync` writes a valid one-way Pi record and completes after the record is flushed.
`extension_ui_response` uses the notification path: its `id` is the original interaction ID, not a
new `PiRequestId`, and Pi does not acknowledge it with a response.

### Writer

- Assign a unique `PiRequestId` to every request command. One-way notifications retain their
  protocol-defined identity and do not receive a correlation ID.
- Serialize with source-generated `System.Text.Json` metadata where the shape is known.
- Write exactly one BOM-less UTF-8 JSON object followed by LF and flush it. Use
  `new UTF8Encoding(false, true)` rather than a BOM-emitting encoding.
- Protect stdin with a single writer lock.
- Never build launch arguments or commands by concatenating untrusted shell fragments.

### Reader

- Implement LF framing explicitly over bytes or decoded character chunks.
- Decode with strict BOM-less UTF-8 semantics and reject malformed byte sequences. Do not use
  `StreamReader.ReadLine()`, which also accepts bare CR and an unterminated final record.
- Accept CR only when it immediately precedes LF.
- Enforce a maximum record size and fail the runtime with a useful protocol error if exceeded.
- Parse the top-level `type` discriminator once.
- Route `response` records with a known request ID directly to the pending request table.
- Route every other valid record to the event decoder.
- Convert unknown event types into bounded diagnostic events instead of terminating the session.
- A Pi parse-error response has no request ID. Never guess its request by matching the `command`
  field. Because the application owns serialization, treat it as a connection-wide protocol fault:
  retain bounded diagnostics, fail all pending requests, and recover through a new runtime.
- Treat locally detected malformed JSON, EOF with an incomplete record, duplicate response IDs, and
  unknown response IDs as explicit protocol diagnostics with defined severity. Malformed framing is
  fatal; late/duplicate IDs may remain non-fatal diagnostics when the stream is otherwise valid.

### Pending requests

- Use a concurrent map from `PiRequestId` to a completion source and timeout registration.
- Remove the entry exactly once on response, cancellation, timeout, exit, or disposal.
- Use command-specific, configurable timeouts. Start with ten minutes for `prompt` and `compact`
  because extension slash commands and compaction may perform long-running work before responding;
  use 30 seconds for ordinary state and mutation commands. All waits remain cancellable.
- A blocking extension interaction may legitimately hold a `prompt` response open. The controller
  may pause or extend that prompt deadline while a blocking interaction is pending, but it must keep
  the operation visibly cancellable rather than silently waiting forever.
- Pi process exit atomically fails and clears the entire map.
- For an ordinary prompt, a successful `prompt` response means accepted or queued. It never means
  that the turn completed. For a known extension command, it means the awaited command handler
  returned; use the separate receipt rule in section 11.

### Stderr

- Drain stderr continuously so Pi cannot block on a full pipe.
- Store a byte-bounded tail for crash diagnostics.
- Do not forward arbitrary stderr directly to every network client.
- Redact or limit diagnostics before sending them across the network because they may contain paths,
  prompt data, or provider errors.

## 6. PiThreadController

Each active thread has one controller and one state machine:

```text
Stopped → Starting → Hydrating → Ready → Running → Ready
                      │                    │
                      └──────► Crashed ◄───┘
Ready/Running → Stopping → Stopped
```

The controller owns:

- The current Pi process and `SessionGeneration`.
- The current app `TurnId`.
- Pending extension UI requests.
- Pi session identity and active entry cursor.
- The authoritative `ThreadProjection`.
- A serialized lifecycle mailbox.
- An urgent control lane.

Serialize operations that replace or materially mutate session state, including process start,
hydration, model changes, thinking changes, compaction, fork/clone when added, session replacement,
and shutdown.

The following must bypass an occupied lifecycle mailbox so they can unblock Pi:

- `extension_ui_response`
- `clear_queue`
- `abort`
- forced runtime shutdown

The transport still uses one stdin writer, but request correlation permits these commands to be in
flight independently. `extension_ui_response` is a one-way notification and therefore never enters
the pending request map.

Pi's current `abort` implementation cancels an active retry delay before aborting the agent and
waiting for idle. `ThreadStopTurn` therefore uses `clear_queue` followed by `abort`. Pin this
behavior with a compatibility test. If a supported future Pi version stops providing that invariant,
the explicit sequence must be `clear_queue`, `abort_retry`, then `abort`—never `abort_retry` after an
already awaited `abort`.

Before applying an event to the projection, compare its captured `SessionGeneration` with the
controller's current generation. An obsolete process may finish diagnostics, but it cannot mutate
the rebound thread.

## 7. Application commands and Pi mapping

Do not expose a generic raw-Pi-command endpoint to remote clients. Define a closed application
command union and translate it inside the host.

| Application command | Pi operation | Network completion meaning |
| --- | --- | --- |
| `ThreadStartTurn` | `prompt` | Pi accepted the prompt; turn remains active. |
| `ThreadQueueSteering` | `prompt` with `streamingBehavior: steer` | Queued only while a turn is active and Pi reports streaming; a supplied expected turn must still match. |
| `ThreadQueueFollowUp` | `prompt` with `streamingBehavior: followUp` | Queued only while a turn is active and Pi reports streaming; a supplied expected turn must still match. |
| `ThreadClearQueue` | `clear_queue` | Previously queued steering/follow-up messages are returned and the projection becomes cleared. |
| `ThreadRefreshQueue` | `get_state` | Pending count and effective delivery modes are reconciled. |
| `ThreadSetQueueDeliveryMode` | `set_steering_mode` or `set_follow_up_mode` | The selected explicit mode is applied and reconciled. |
| `ThreadInterruptAgent` | parent-turn `clear_queue` plus `abort` | Pi aborts the parent turn and the structured subagent extension propagates cancellation to children. |
| `ThreadStopTurn` | `clear_queue`, then `abort` | Stop initiated; settlement arrives as an event. |
| `ThreadSetModel` | `set_model` | Model mutation accepted and projection updated. |
| `ThreadSetThinkingLevel` | `set_thinking_level` | Thinking mutation accepted. |
| `ThreadCompact` | `compact` | Compaction command finished; retries may still emit events. |
| `ThreadExecuteExtensionCommand` | `prompt` containing a known extension slash command | Extension handler completed; any agent work is tracked separately. |
| `ThreadRespondToInteraction` | one-way `extension_ui_response` | Host won arbitration and flushed the response to Pi. |
| `ThreadRefreshState` | Parallel state queries | Reconciliation completed. |

Use the `get_commands` catalog and its `source` information to classify slash input:

- A known extension command uses `ThreadExecuteExtensionCommand`. Its command receipt completes on
  the Pi `prompt` response because an extension command may finish without producing
  `agent_settled`. If the extension starts agent work, that work has its own runtime/turn events.
- A prompt template or skill uses `ThreadStartTurn`; Pi expands it and the resulting turn completes
  on `agent_settled`.
- Unknown slash text uses `ThreadStartTurn` and is treated as an ordinary prompt.

`ThreadQueueSteering` and `ThreadQueueFollowUp` are explicit active-turn operations, not unconditional
queue insertion. The host validates a supplied `ExpectedTurnId`, requires a current turn, and confirms
Pi still reports `isStreaming` immediately before dispatch. This avoids the dedicated `steer`/`follow_up` idle
behavior, which can enqueue without starting a run. If settlement wins the race, the host rejects
the command instead of silently promoting it to a fresh turn.

The host validates command availability against thread state. For example, an initial prompt may
start a stopped runtime, while a model change cannot race with hydration or shutdown.

## 8. Normalized runtime events

Pi wire records are private to PiStation.PiRpc and PiStation.Host. Translate them into a smaller,
versioned application event union.

| Pi record | Application event |
| --- | --- |
| `agent_start` | `TurnRuntimeStarted` |
| `agent_end` | `TurnAttemptEnded` with `willRetry`; never final settlement |
| `agent_settled` | `TurnSettled` |
| `message_start` | `MessageStarted` |
| `message_update` text/thinking delta | `ContentDelta` |
| `message_update` tool-call data | `ToolCallComposed` |
| `message_end` | `MessageCompleted` |
| `tool_execution_start` | `ToolStarted` |
| `tool_execution_update` | `ToolOutputReplaced` |
| `tool_execution_end` | `ToolCompleted` |
| `queue_update` | `QueueStateChanged` |
| structured subagent tool start/update/end | persisted `AgentActivityChanged` records |
| `compaction_start/end` | `CompactionChanged`, including `willRetry` |
| `auto_retry_start/end` | `RetryChanged` |
| `summarization_retry_scheduled/attempt_start/finished` | `SummarizationRetryChanged` |
| `extension_ui_request` blocking method | `InteractionOpened` |
| `extension_ui_request` notification/status | `ExtensionPresentationChanged` |
| `extension_error` | `RuntimeWarning` |
| unknown record | `UnknownRuntimeEvent` diagnostic |

Rules:

- Assemble message deltas by `contentIndex`.
- Treat `message_end.message` as authoritative and replace the streamed assembly.
- Use Pi's `toolCallId` for tool identity.
- Treat `tool_execution_update.partialResult` as cumulative replacement content, not a delta.
- Generate the app `TurnId` before sending the initial prompt. Pi low-level `turn_start` and
  `turn_end` records are substeps, not separate user-facing turns.
- `agent_end` is a low-level attempt boundary. It may be followed by automatic retry, compaction,
  summarization retry, or queued work and therefore never settles the application turn.
- Do not retain arbitrary raw JSON in the live projection. Preserve only bounded previews for
  unknown records and diagnostics.

## 9. Host projection and stream continuity

Maintain one authoritative projection per thread. At minimum it contains:

- Runtime state and current turn.
- Final and in-progress messages.
- Tool calls and bounded output previews.
- Steering and follow-up queues.
- Effective steering and follow-up delivery modes reported by `get_state`.
- Pending extension interactions.
- Model, available models, thinking level, and available levels.
- Compaction/retry state.
- Token, cost, and context statistics.
- Pi session identity and last reconciled entry ID.
- Last error and bounded diagnostics reference.

Every committed projection change receives the next `(ProjectionEpoch, Sequence)` and is appended
to a per-thread in-memory event journal.

### Race-free subscription

Expose one server-streaming operation:

```csharp
IAsyncEnumerable<ThreadEnvelope> SubscribeThreadAsync(
    ThreadId threadId,
    ThreadCursor? cursor,
    CancellationToken cancellationToken);
```

Under the thread controller's serialization boundary:

1. Inspect the requested cursor.
2. Register the subscriber to receive events after the selected base sequence.
3. If the cursor is valid and retained, enqueue the missing events.
4. Otherwise enqueue a complete snapshot carrying the current epoch and sequence.
5. Release the boundary and continue with live events.

`ThreadEnvelope` is a union of `Snapshot`, `Event`, and `ResyncRequired`. The snapshot and live
subscription are never established as two unrelated calls, so there is no gap between them.

### Backpressure

- Bound the journal by both event count and encoded byte size.
- Give each subscriber a bounded outgoing queue.
- Coalesce high-frequency text and reasoning deltas to approximately one update every 16–33 ms.
- Send lifecycle completion, errors, interactions, and command-state changes immediately.
- Truncate tool previews independently from Pi's own complete session data.
- If a subscriber cannot keep up, enqueue `ResyncRequired` if possible and close that subscription.
- Network delivery never blocks the Pi stdout reader or thread projection.

## 10. SignalR protocol

Use ASP.NET Core Kestrel with SignalR. The embedded local client uses authenticated HTTP bound only
to loopback; every non-loopback endpoint uses HTTPS. Start with JSON and source-generated
serializers; consider MessagePack only after profiling demonstrates a need.

The hub surface should remain small:

```text
GetEnvironmentDescriptor
ExecuteThreadCommand
GetCommandReceipt
SubscribeThread
ListProjects
ListThreads
PairDevice / RevokeDevice through an administrative HTTP surface
```

`ExecuteThreadCommand` accepts:

```text
ProtocolVersion
EnvironmentId
ClientId
CommandId
ThreadId
ExpectedProjectionEpoch (optional)
ExpectedTurnId (optional, for strict active-turn delivery)
Command union
```

The handshake returns:

```text
EnvironmentId
EnvironmentName
ServerVersion
Protocol minimum/maximum
Pi availability/version
Capabilities
Authenticated client/scopes
```

Reject incompatible protocol versions and insufficient scopes before dispatching a mutation.

## 11. Command receipts and idempotency

Persist a receipt before attempting a mutation. Key it by `(ClientId, CommandId)` and store the
target environment/thread plus a hash of the command body so a reused ID with different content is
rejected.

```text
Received → Dispatching → Accepted → Completed
                     ├─► Rejected
                     ├─► Failed
                     └─► DispatchUncertain
```

- A retry with the same ID and body returns the existing receipt.
- A Pi `prompt` response moves an ordinary turn-start command to `Accepted`.
- `agent_settled` moves its associated ordinary turn receipt to `Completed`.
- A known extension slash command is different: its `prompt` response means the awaited extension
  handler completed, so that command receipt moves directly to `Completed`. Any agent run the
  extension initiated is observed and settled independently.
- Network loss after Pi acceptance does not duplicate the prompt because the host still has the
  receipt.
- A host crash between writing the prompt and recording Pi's response is inherently ambiguous.
  On restart, reconcile Pi entries. If acceptance cannot be proven, use `DispatchUncertain` and
  require an explicit user choice; never resend automatically.
- Retain receipts long enough to cover realistic offline/reconnect periods, then prune them by a
  documented policy.

This provides practical idempotency but does not make an unsupported claim of exactly-once Pi
execution across every host crash boundary.

## 12. Persistence and recovery

Pi's session JSONL remains the durable conversation authority. Host storage must not become a
second competing transcript.

### Host persistence

Use SQLite for:

- Stable environment identity and host settings.
- Projects and canonical working directories.
- Threads and their Pi `sessionId`/`sessionFile` mapping.
- The canonical environment-owned Pi session root passed to every process through `--session-dir`.
- Preassigned Pi session IDs are committed before process launch; the session path may remain
  provisional until Pi first persists it.
- Thread model/thinking preferences.
- Command receipts.
- Paired devices, scopes, and revocation state.
- Optional finalized projection cache for fast startup.

Do not write every token delta to SQLite. Persist final/reconciled entries and essential recovery
metadata at controlled boundaries.

### Client persistence

Store:

- Known environment endpoints and labels.
- Non-secret environment metadata.
- Protected device credentials through Windows credential protection.
- Last thread cursor and optional read-only snapshot cache.
- Drafts and local UI state.

### Host restart recovery

1. Mark any formerly running controller as interrupted.
2. Load thread/session mappings without launching every Pi process.
3. On demand, validate and start Pi with the saved session.
4. Run hydration and active-branch reconciliation.
5. Compare pending `Dispatching` receipts with durable Pi entries.
6. Resolve provable receipts; mark ambiguous ones `DispatchUncertain`.
7. Rotate `ProjectionEpoch` if the previous event sequence cannot be continued safely.
8. Publish a fresh snapshot before enabling mutations.

## 13. Connection supervision

ClientRuntime owns one supervisor per environment with explicit states:

```text
Disconnected
Connecting
Authenticating
Synchronizing
Connected
Retrying
AuthenticationRequired
Incompatible
```

- One connection attempt performs handshake and authentication; it does not recursively retry.
- The supervisor owns retry with exponential backoff, jitter, and a reasonable cap.
- A stable connection resets the retry ladder.
- Reconnect restarts every active thread subscription with its last cursor.
- Cached projections remain readable while offline, but mutation controls remain disabled.
- An environment becomes writable only after its connection and required subscriptions are live.
- Removing a saved environment deletes client credentials and cache only. It never deletes data on
  the host.

## 14. Pairing and remote security

Remote listening is disabled by default.

### Local bootstrap

- Bind the embedded host to loopback on an available port.
- Use HTTP only for this loopback endpoint and bind an exact loopback address, never a wildcard
  interface. Remote endpoints do not inherit this exception.
- Generate a high-entropy ephemeral local bootstrap secret at application startup.
- Pass the resolved endpoint and secret directly to ClientRuntime in memory. Send the secret in an
  authorization header, never a URL, and exclude it from logs.
- Require authentication even on loopback so local and remote authorization paths do not diverge.

### Remote access

- Require an explicit host setting to bind a selected LAN or private-network interface.
- Require HTTPS for non-loopback endpoints.
- Give the host a stable certificate identity and display its fingerprint during pairing.
- Create short-lived, single-use pairing codes.
- Show the requesting device on the host and require confirmation.
- Exchange the pairing code for a revocable device credential.
- Store the credential using Windows-protected storage, not plaintext application settings.
- Use simple initial scopes: `environment.read`, `thread.operate`, and `access.admin`.
- Allow the host to list and revoke paired devices.

Long-lived credentials should not be placed in the SignalR URL. Use the credential over an
authenticated HTTPS request to obtain a short-lived, single-purpose connection ticket. Put only
that ticket in the WebSocket negotiation when a header cannot be retained across transport
upgrade.

The first release supports direct LAN and user-managed VPN/Tailscale connectivity. Public relay,
automatic port forwarding, and NAT traversal are separate future projects.

## 15. Extension UI and multiple clients

Pi's blocking extension UI methods produce `extension_ui_request`. Store each pending request in
the owning thread controller and publish it to all authorized subscribers. When Pi includes a
`timeout`, derive and publish a host deadline from the moment the request is ingested.

Resolve responses atomically:

```text
Pending → Responding → ResponseFlushed
   ├───────────────► Expired
   └───────────────► Failed/Cancelled
```

- The first valid response wins.
- When the host deadline expires, move the request to `Expired` and remove it from the active
  projection. Pi auto-resolves independently and emits no resolution event.
- Later responses receive `InteractionExpired` or `InteractionAlreadyResolved` and are not written
  to Pi.
- Closing a client tab or losing one connection does not cancel the request.
- Turn settlement, turn stop, Pi exit, thread session replacement, and explicit host shutdown clear
  it.
- A response accepted just before the deadline is a best-effort one-way write. Pi sends no
  acknowledgement, so the host records `ResponseFlushed`, not a claim that the extension consumed
  it before an agent-side timeout race.
- Fire-and-forget notifications and status changes never create pending response state.

Start the first Pi-calling slice without a custom Pi extension. Add a small application-owned
extension only when permission interception or another capability cannot be expressed by the base
RPC protocol. Keep policy on the host; the extension should remain a thin Pi integration bridge.

## 16. Error model

Expose typed, user-actionable errors across the application contract:

- `PiNotFound`
- `PiVersionUnsupported`
- `PiLaunchFailed`
- `PiProtocolViolation`
- `PiCommandRejected`
- `PiCommandTimedOut`
- `PiRuntimeCrashed`
- `SessionMissing`
- `SessionCorrupt`
- `ThreadBusy`
- `CommandConflict`
- `DispatchUncertain`
- `InteractionAlreadyResolved`
- `InteractionExpired`
- `TurnAlreadySettled`
- `AuthenticationRequired`
- `PermissionDenied`
- `ProtocolIncompatible`
- `ResyncRequired`

Transport disconnection and Pi runtime failure are different states. A disconnected client must not
show a running host-side Pi process as crashed, and a healthy SignalR connection must not hide a
crashed Pi runtime.

## 17. Verification strategy

### Fake Pi executable

Build a deterministic console fixture that behaves like `pi --mode rpc`. It must support scripted
stdin expectations, arbitrarily chunked stdout, stderr output, delayed/out-of-order responses,
events, malformed records, and controlled process exit.

Cover:

- LF records split across arbitrary byte chunks.
- Several records in one read.
- UTF-8 characters split across reads.
- Command responses arriving in a different order than requests.
- Unknown and duplicate request IDs.
- Unknown event types and fields.
- Malformed and oversized records.
- Text/thinking/tool-call assembly by content index.
- Cumulative tool output replacement.
- Prompt acceptance followed much later by settlement.
- A long-running extension slash command that does not respond within an ordinary 30-second window.
- An extension slash command that completes without starting an agent run and therefore emits no
  `agent_settled`.
- Extension UI response while a turn is running.
- One-way extension UI responses never enter the correlated pending-request map.
- Extension UI timeout expiry and a response racing the deadline.
- `clear_queue` followed by `abort`.
- `abort` during an `auto_retry_start` delay settles without requiring a later `abort_retry`.
- Steering/follow-up dispatch when settlement wins the race becomes a new turn, or fails with
  `TurnAlreadySettled` when `ExpectedTurnId` is supplied.
- An uncorrelated Pi parse-error response fails the connection rather than being matched by command
  name.
- Stderr flood and bounded diagnostic retention.
- Timeout, cancellation, stdout EOF, and abrupt process death.
- Events arriving from an obsolete session generation.

### Host integration tests

- Local SignalR client starts a turn and receives snapshot plus ordered events.
- Two subscribers see the same thread sequence.
- A reconnect cursor receives exactly the missing events.
- An expired cursor receives one snapshot without a gap.
- A slow subscriber is disconnected without affecting Pi ingestion or other subscribers.
- Retrying a command ID does not send a second Pi command.
- Reusing a command ID with a different body is rejected.
- Two clients race one extension response and exactly one reaches Pi.
- Host restart resumes a durable Pi session and rotates the epoch when required.
- Ambiguous dispatch becomes `DispatchUncertain`, not an automatic resend.
- A preassigned `--session-id` is recoverable even when the host stops before recording the final
  session path.

### Security tests

- Unauthenticated loopback and remote hub connections are rejected.
- Pairing codes expire and can be used only once.
- Revoked devices cannot obtain new connection tickets.
- Read-only clients cannot mutate threads.
- Connection tickets expire and cannot be replayed.
- A certificate fingerprint mismatch blocks reconnection.

### Real Pi smoke test

Keep this opt-in because it needs a configured provider. It should:

1. Discover and validate Pi.
2. Start a persistent RPC session in a temporary project.
3. Send a deterministic prompt.
4. Observe prompt acceptance, text streaming, `message_end`, and `agent_settled`.
5. Stop the process gracefully.
6. Resume the recorded session and verify entries and state.

## 18. Implementation phases

### Phase 0 — Freeze contracts and invariants

Implementation status (2026-09-02): **complete**.

Deliver:

- Identifier types and protocol versioning.
- Thread commands, receipts, projections, and stream envelopes.
- Pi command/event DTO strategy with unknown-record handling.
- Fake Pi executable and fixture scripts.

Exit criteria:

- Contracts serialize round-trip through source-generated JSON.
- The architecture has no raw Pi JSON type in WinUI or network contracts.
- The fake Pi can reproduce streaming, interaction, crash, and framing scenarios.

### Phase 1 — Local Pi RPC proof

Implementation status (2026-09-02): **complete**. The automated PiRpc suite passes, and the
opt-in real-Pi smoke test completed and resumed an isolated session against Pi `0.84.4` on
September 1, 2026.

Deliver:

- Pi discovery/version validation.
- Safe Windows resolution from `pi.cmd` to `node.exe` plus the validated package CLI, without
  invoking `cmd.exe`.
- `PiProcess` and `PiRpcConnection`.
- Exact framing, correlation, stderr drainage, shutdown, and diagnostics.
- Event normalization and streaming assembly.
- Console harness for starting, prompting, stopping, and resuming Pi.

Exit criteria:

- Every pending request resolves or fails under response, timeout, cancellation, and process death.
- One-way notifications never create pending requests, and an uncorrelated parse error fails all
  correlated requests immediately.
- Prompt success is separated from turn settlement.
- Extension-command completion is separated from agent-turn settlement.
- Message and tool streams assemble correctly.
- A real Pi opt-in smoke test completes and resumes one session.

### Phase 2 — Authoritative thread host

Implementation status (2026-09-02): **complete**.

Deliver:

- `PiThreadController` and per-thread registry.
- Lifecycle mailbox, urgent lane, and session generation checks.
- Thread projection, epoch/sequence assignment, event journal, and subscriber backpressure.
- SQLite thread/session mappings and command receipts.

Exit criteria:

- Several threads can run isolated Pi processes.
- A stopped process resumes the correct session.
- Slow subscribers cannot block Pi ingestion.
- Duplicate network command IDs do not duplicate Pi prompts.

### Phase 3 — Local WinUI through SignalR

Implementation status (2026-09-02): **complete**.

Deliver:

- Embedded Kestrel host on loopback.
- Local bootstrap authentication.
- Environment handshake, command execution, receipt lookup, and thread subscription.
- Client connection supervisor and WinUI-facing projection API.
- Minimal WinUI surface: prompt, streaming text, tool status, stop, and runtime errors.

Exit criteria:

- The local WinUI app reaches Pi only through ClientRuntime and SignalR.
- Closing and reopening a tab does not stop the turn or duplicate transcript content.
- Restarting the host recovers the durable thread through Pi session hydration.

### Phase 4 — Computer-to-computer calling

Implementation status (2026-09-02): **not started**. Loopback authentication is implemented, but
remote listening, TLS identity, pairing, device credentials, scopes, tickets, revocation, and saved
remote environments are not shipped.

Deliver:

- Explicit remote-listen configuration.
- Host TLS identity, pairing approval, device credentials, scopes, connection tickets, and
  revocation.
- Saved remote environments and independent connection supervisors.
- Multiple-client interaction arbitration.

Exit criteria:

- Computer B starts and observes a Pi turn owned by Computer A.
- Disconnecting B does not stop A's turn.
- B reconnects from its cursor or a snapshot without gaps or duplicates.
- Revocation immediately prevents new commands and subscriptions.
- Local and remote environments can operate concurrently without identifier collisions.

### Phase 5 — Failure and load hardening

Implementation status (2026-09-02): **partial**. Crash reconciliation, uncertain-dispatch
presentation, bounded journals/tool previews/uploads/searches, explicit recovery states, structured
diagnostics, and deterministic recovery tests exist. A packaged hardening journey now covers visible
Stop behavior, two active isolated threads, transport disconnect/reconnect, bounded-journal snapshot
resync, and uncertain dispatch without prompt replay. Sustained large-output, slow-network, and
multi-client load validation remains.

Deliver:

- Crash reconciliation and `DispatchUncertain` UX state.
- Configurable byte limits, delta coalescing, and slow-consumer behavior.
- Structured diagnostics and privacy limits.
- Long-running multi-thread and multi-client tests.

Exit criteria:

- Host and client memory remain bounded during large tool output and slow network tests.
- Pi crashes, host restarts, network drops, stale cursors, and conflicting interaction responses all
  produce explicit recoverable states.
- No tested failure path silently resends a potentially accepted prompt.

## 19. Fixed initial decisions

- WinUI 3 client and ASP.NET Core Kestrel host in the same desktop application.
- SignalR over authenticated HTTP for the embedded loopback connection; SignalR over HTTPS for all
  non-loopback application traffic.
- JSON contracts first; MessagePack only after measurement.
- One Pi process per active thread.
- Pi session JSONL is the conversation authority.
- New durable threads receive and persist a known Pi `--session-id` before process launch.
- The first supported Pi compatibility range begins at `0.84.4`.
- SQLite stores host metadata, command receipts, checkpoints, and bounded agent/workflow activity
  events, not token-by-token assistant streams.
- Typed application commands cross the network; raw Pi commands do not.
- Local and remote clients use the same environment protocol.
- The host continues work after clients disconnect.
- The desktop process stays alive in the notification area while hosting; an external Windows
  service is deferred.
- Direct LAN and private VPN/Tailscale access first; no managed relay.
- Vanilla Pi RPC first; application-specific extension bridge only when demonstrated necessary.
- Protocol v20 preserves and projects Pi's reported `steeringMode` and `followUpMode`; visible queue
  controls call Pi's setters only after an explicit user action.

## 20. First vertical slice

Implementation status (2026-09-02): **complete**. The authenticated WinUI-to-SignalR-to-host-to-Pi
path, incremental projection, cursor resume, process relaunch, and session hydration are covered by
the packaged vertical-slice journey.

The first end-to-end proof is intentionally narrow:

1. Start the embedded loopback host.
2. Connect ClientRuntime through authenticated SignalR.
3. Create one project and one thread.
4. Lazily start `pi --mode rpc` in that project.
5. Send `ThreadStartTurn` with a `CommandId`.
6. Receive the Pi acceptance response.
7. Stream normalized assistant text into one WinUI view.
8. Observe `message_end` and `agent_settled`.
9. Reopen the subscription from its last cursor without duplication.
10. Stop and resume the same Pi session.

Rich tool presentation is implemented through protocol-v10 bounded arguments and native grouped
expanders. Protocol v11 now adds Pi-reported per-turn token usage, active-model context-window
presentation, host-measured elapsed time, and hydration-safe metrics. Remote pairing, permission
extensions, and multiple clients should be built only after this slice proves the complete local
call path and recovery semantics.
