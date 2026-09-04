# T3 Code Architecture and Feature Guide for PiStationDesktop

This document summarizes the local T3 Code reference checkout and turns its architecture and feature set into guidance for `PiStationDesktop`.

Source snapshot reviewed:

- T3 Code package version: `0.0.37`
- T3 Code commit: `bba79cc25` (`2026-08-31`)
- T3 Code source: [`Core/t3code`](Core/t3code)
- PiStationDesktop source: [`PiStationDesktop`](PiStationDesktop)

PiStationDesktop implementation status last reviewed: **2026-09-02**.

The file map intentionally covers important entry points and ownership boundaries rather than every test, generated file, mobile native binding, or vendored artifact.

## 1. Executive summary

T3 Code is an agent-harness control surface. It does not implement a coding model itself. It runs authenticated provider CLIs on the machine that owns a workspace, converts each provider's protocol into one common domain model, persists authoritative project and thread state, and exposes that state to web, desktop, and mobile clients.

Its central architecture is:

```text
Web / Electron / Mobile clients
        |
        | authenticated typed RPC over WebSocket
        v
Shared client runtime
  - connection supervision
  - cached projections
  - domain operations
        |
        v
T3 server (the execution boundary)
  - projects and threads
  - event-sourced orchestration
  - SQLite projections and receipts
  - filesystem, Git, terminal, preview
  - provider registry and adapters
        |
        v
Codex / Claude / Cursor / Grok / OpenCode processes
```

The most important lesson for PiStationDesktop is not the React or Electron UI. It is the boundary design:

1. The host owns durable state and every side effect.
2. The wire contract is typed and versioned.
3. Provider-specific behavior stops at an adapter boundary.
4. Clients render projections and dispatch commands; they do not own agent processes.
5. Commands are idempotent, state streams can resume, and reconnect is a first-class state machine.
6. Each feature is complete across entry points, reverse actions, connection states, and tests.

PiStationDesktop already follows much of this shape with `PiStation.Protocol`, `PiStation.Host`, `PiStation.PiRpc`, `PiStation.ClientRuntime`, and `PiStation.App`. T3 Code is therefore best used as a feature and domain-design reference, not as a framework to transplant.

## 2. Product model and terminology

| Term | T3 Code meaning | PiStationDesktop equivalent |
| --- | --- | --- |
| Environment | One running server plus its filesystem, credentials, providers, and state | One `EnvironmentService`/embedded host today; potentially local or remote later |
| Project | Environment-local workspace record rooted at a directory | `ProjectDescriptor` managed by `ProjectService` |
| Workspace root | Base filesystem directory for a project | `ProjectDescriptor.CanonicalPath` |
| Thread | Durable conversation and work history for one project | `ThreadDescriptor` plus `ThreadProjection` and Pi session |
| Turn | One user-to-agent work cycle | `ThreadStartTurnCommand` through `TurnSettledEvent` |
| Provider | Agent runtime such as Codex or Claude | Pi is the only provider today |
| Provider adapter | Translation between a provider's native protocol and common orchestration events | `PiStation.PiRpc` plus `PiThreadController` |
| Command | Typed request to change state | `ExecuteThreadCommandRequest` and `ThreadCommand` |
| Domain event | Persisted fact produced after deciding a command | PiStation has similarly shaped thread stream events, but not a durable orchestration event store yet |
| Projection | Read-optimized state derived from events | `ThreadProjection` |
| Reactor | Async side-effect worker triggered by domain intent | Pi process/controller operations currently perform this role directly |
| Checkpoint | Hidden Git snapshot used for diff and restore | Not implemented yet |
| Receipt | Durable result for an idempotent command | `CommandReceipt` and the receipt table |

## 3. How T3 Code works

### 3.1 Startup and surfaces

The published `t3` CLI starts the server and can serve the built web UI. The Electron desktop app starts and supervises a desktop-scoped server, then loads the same web application through a custom protocol. The React Native mobile app connects to an existing server.

The server is always the execution boundary. Provider processes, terminals, Git commands, filesystem reads/writes, checkpoints, and preview discovery happen on the server machine. A remote client never interprets a workspace path as a path on the client device.

### 3.2 Connection and authentication

Clients keep a catalog of known environments. A connection target can be:

- the platform-managed primary/local server;
- a directly paired bearer endpoint;
- a managed relay endpoint;
- a desktop-managed SSH endpoint.

The shared client runtime resolves a target, authenticates it, creates one RPC session, and supervises retries. It distinguishes offline, preparing, opening, synchronizing, connected, reconnecting, and blocked/error states. Cached shell and thread projections remain available while offline.

Remote WebSocket authentication uses a short-lived socket ticket obtained with a longer-lived credential. Authorization is also checked per RPC method; possessing a valid socket does not grant every operation.

### 3.3 Projects and threads

A project points at one workspace root and can define a default model, default workspace mode, icon, and project scripts. A thread belongs to a project and records its selected provider/model, permission mode, interaction mode, branch, and optional worktree path.

Threads can be created, renamed, title-regenerated, pinned and reordered, snoozed, settled, archived, deleted, linked to a pull request, and restored through the inverse actions. The sidebar is a projection of this durable state rather than an independent client-only list.

### 3.4 Sending a turn

The client dispatches a typed `thread.turn.start` command with:

- stable command, thread, and message IDs;
- text and attachments;
- model/provider selection;
- permission and interaction modes;
- optional new-thread and worktree bootstrap data.

The orchestration engine serializes commands through one queue. For each command it:

1. checks the durable receipt so a retry is idempotent;
2. validates invariants;
3. calls a pure decider to produce domain events;
4. appends events, updates persisted projections, and writes the receipt in one SQLite transaction;
5. updates the in-memory read model and publishes committed events.

A provider-command reactor observes the persisted intent and calls the provider adapter. Provider output is normalized back into internal orchestration commands, persisted, projected, and streamed to subscribed clients.

### 3.5 Provider isolation

The common orchestration layer does not know whether a thread runs Codex, Claude, Cursor, Grok, or OpenCode. A driver declares configuration and creates an adapter. Registries own configured instances and live adapters. `ProviderService` routes operations by thread/session.

This keeps provider-specific launch arguments, authentication, model discovery, approvals, native event shapes, and resume behavior out of the rest of the product.

### 3.6 Streaming and recovery

The client subscribes separately to shell summaries, individual thread detail, terminal streams, VCS status, server configuration, lifecycle, auth state, previews, and telemetry. It requests only the data it needs.

The client runtime owns reconnection. A failed domain subscription does not automatically tear down a healthy transport. A transport failure waits for the connection supervisor to supply a replacement RPC session. Cached snapshots cannot overwrite newer live data during a fast reconnect.

### 3.7 Checkpoints, diffs, and restore

Each agent turn is bracketed by workspace checkpoints stored as hidden Git refs. The server computes per-turn and full-thread diffs from those refs. Revert restores both the workspace and the provider conversation state. Checkpoint work is asynchronous; a turn is considered complete when its provider session leaves `running`, not when later diff work finishes.

### 3.8 Performance model

T3 Code treats performance as a product feature:

- subscribe to narrow streams instead of broadcasting all state;
- keep transport and retry logic outside React components;
- keep terminal rendering outside React frames;
- use read projections rather than rebuilding the event log in the UI;
- paginate and window large thread timelines;
- avoid continuously repainting animations;
- coalesce or buffer high-volume runtime updates where appropriate.

## 4. Feature inventory

### 4.1 Agent and conversation features

- Multiple provider instances: Codex, Claude, Cursor, Grok, and OpenCode.
- Provider/model discovery, provider status, model search, legacy model classification, account display names, and accent colors.
- Four permission modes: Supervised, Auto-accept edits, Auto, and Full access.
- Default and Plan interaction modes, proposed plan cards, and plan follow-up flows.
- Streaming or buffered assistant delivery.
- Rich Markdown, code blocks, file citations, images, tool activity, terminal context, and copy/open actions.
- Inline approvals, structured user questions, interrupt/stop, failure states, and resume.
- Codex sub-agent display with model and reasoning information when available.
- Thread title generation/regeneration and provider feedback upload.
- Context-window and usage presentation.

### 4.2 Composer features

- Large prompts with explicit length validation.
- Image and generic file attachments with upload progress, retry, limits, and recovery after reload.
- Drag/drop, paste, HEIC/HEIF conversion, attachment preview, and server-side file paths.
- Slash commands and skill search with inline tokens.
- File/path mentions, terminal context, selected browser elements, and review comments as prompt context.
- Prompt stash with attachments and environment affinity.
- Start a new thread in the background.
- Mobile photo/file sharing and on-device voice transcription.

### 4.3 Project and workspace features

- Add a local directory or clone from a Git URL/hosting provider.
- Create missing workspace directories when requested.
- Automatic or user-selected project icons.
- Per-project default provider/model and workspace mode.
- `t3.json` project configuration and named scripts.
- Optional scripts that run when a worktree is created.
- File tree, filename search, content search, read/write, editor integration, Markdown preview, image preview, and file mentions.
- Group equivalent repositories across environments for presentation while keeping projects environment-local.

### 4.4 Thread organization

- Active, pinned, snoozed, settled, and archived states.
- Synced pinned ordering across clients.
- Rename, regenerate title, delete, restore, and search.
- Link a pull request to a thread and optionally auto-settle when it merges.
- Optional Git worktree per thread for safe parallel agent work.
- Cached shell/thread snapshots for offline display.

### 4.5 Git, review, and source control

- Live Git/VCS status and refresh.
- Branch/ref listing, creation, switching, pulling, and repository initialization.
- Worktree create/remove and setup scripts.
- Turn diff and full-thread diff from checkpoints.
- Changed-files tree, syntax-aware diff view, inline review comments, and revert.
- Clone and publish repositories.
- GitHub, GitLab, Bitbucket, and Azure DevOps integration.
- Pull/merge request list, details, timeline, checks, diffs, comments, replies, reactions, reviewers, resolution, and in-app editing.

### 4.6 Terminal and preview

- Server-owned PTY sessions with attach, input, resize, clear, restart, and close.
- Multiple/split terminals and terminal links.
- Raw terminal byte streaming; each client chooses its renderer.
- Local development-server discovery.
- Embedded browser tabs, navigation, refresh, responsive viewport, zoom, color scheme, and dev tools.
- Screenshot, recording, picture-in-picture, element picking, and prompt annotations.
- Agent browser automation exposed through an MCP preview toolkit.

### 4.7 Remote and multi-device operation

- Web, Electron desktop, iOS, and Android clients.
- Direct LAN/HTTPS pairing with one-time credentials.
- Tailscale endpoint discovery and optional Tailscale Serve HTTPS.
- Desktop-managed SSH launch and local port forwarding.
- Managed T3 Connect tunnel/relay discovery.
- Multiple known environments with independent connection supervisors and caches.
- Mobile notifications, agent awareness/live activity, background outbox, incoming share support, and adaptive layouts.

### 4.8 Settings and operations

- Provider instances, environment variables, redacted secrets, binary paths, and multi-account support.
- User-editable keybindings with `when` conditions and conflict reporting.
- Built-in, imported, custom, and environment-published themes.
- Source-control credential discovery.
- Desktop app updates, server version-skew warnings, and supported remote server updates.
- Process diagnostics, trace diagnostics, resource telemetry, process signals, and usage reports.
- Linux/macOS background service support.
- WSL-hosted backend support in the desktop app.

## 5. T3 Code repository structure

### 5.1 Top-level map

| Path | Responsibility |
| --- | --- |
| [`AGENTS.md`](Core/t3code/AGENTS.md) | Product constraints, architecture summary, terminology, verification rules, and contributor guidance |
| [`README.md`](Core/t3code/README.md) | Product overview, installation, supported providers, and documentation links |
| [`package.json`](Core/t3code/package.json) | Monorepo scripts for development, build, test, packaging, and releases |
| [`pnpm-workspace.yaml`](Core/t3code/pnpm-workspace.yaml) | Workspace packages, dependency catalog, patches, and native build permissions |
| [`t3.json`](Core/t3code/t3.json) | This repository's own project icon and worktree setup scripts; also an example of T3 project configuration |
| [`apps/`](Core/t3code/apps) | Shipped server, web, desktop, mobile, and marketing applications |
| [`packages/`](Core/t3code/packages) | Typed contracts and reusable runtimes/utilities |
| [`infra/relay/`](Core/t3code/infra/relay) | Hosted T3 Connect relay and environment discovery infrastructure |
| [`native/`](Core/t3code/native) | Native resource monitor and terminal-related sources/artifacts |
| [`scripts/`](Core/t3code/scripts) | Dev runner, packaging, release, icon, and mobile showcase automation |
| [`docs/user/`](Core/t3code/docs/user) | Shipped user behavior and setup documentation |
| [`docs/internals/`](Core/t3code/docs/internals) | Architecture, runtime, remote, provider, and operations design |
| [`assets/`](Core/t3code/assets) | Dev, nightly, and production branding assets |
| [`patches/`](Core/t3code/patches) | Pinned upstream dependency patches |
| [`oxlint-plugin-t3code/`](Core/t3code/oxlint-plugin-t3code) | Repository-specific lint rules |
| [`.repos/`](Core/t3code/.repos) | Read-only vendored reference repositories; not production imports |

### 5.2 Server: `apps/server`

`apps/server` is the authoritative runtime and published `t3` CLI. It owns every machine-local side effect.

#### Entry points and composition

| File | What it does |
| --- | --- |
| [`src/bin.ts`](Core/t3code/apps/server/src/bin.ts) | Defines the `t3` CLI and subcommands: start/serve, pair, auth, project, service, theme, diagnostics/triage, and connect |
| [`src/cli/server.ts`](Core/t3code/apps/server/src/cli/server.ts) | Converts CLI options into a running server command |
| [`src/server.ts`](Core/t3code/apps/server/src/server.ts) | Main dependency composition root for HTTP, RPC, persistence, orchestration, providers, Git, terminals, auth, telemetry, and shutdown |
| [`src/serverRuntimeStartup.ts`](Core/t3code/apps/server/src/serverRuntimeStartup.ts) | Ordered startup lifecycle and readiness signaling |
| [`src/http.ts`](Core/t3code/apps/server/src/http.ts) | Static web, environment HTTP APIs, attachments/assets, compression, CORS, and observability routes |
| [`src/ws.ts`](Core/t3code/apps/server/src/ws.ts) | Authenticated Effect RPC WebSocket route and method handlers |
| [`src/config.ts`](Core/t3code/apps/server/src/config.ts) | Server runtime configuration |
| [`src/serverSettings.ts`](Core/t3code/apps/server/src/serverSettings.ts) | Durable server settings and client-safe redaction |
| [`src/service-launcher.ts`](Core/t3code/apps/server/src/service-launcher.ts) | Stable background-service launcher entry point |

#### Orchestration and persistence

| File | What it does |
| --- | --- |
| [`src/orchestration/Layers/OrchestrationEngine.ts`](Core/t3code/apps/server/src/orchestration/Layers/OrchestrationEngine.ts) | Serial command worker, idempotency check, transactional event append/projection, and publication |
| [`src/orchestration/decider.ts`](Core/t3code/apps/server/src/orchestration/decider.ts) | Pure command plus state to event decisions |
| [`src/orchestration/commandInvariants.ts`](Core/t3code/apps/server/src/orchestration/commandInvariants.ts) | Command preconditions and consistency rules |
| [`src/orchestration/projector.ts`](Core/t3code/apps/server/src/orchestration/projector.ts) | Applies events to the in-memory read model |
| [`src/orchestration/Layers/ProjectionPipeline.ts`](Core/t3code/apps/server/src/orchestration/Layers/ProjectionPipeline.ts) | Projects committed events into SQLite read tables |
| [`src/orchestration/Layers/ProjectionSnapshotQuery.ts`](Core/t3code/apps/server/src/orchestration/Layers/ProjectionSnapshotQuery.ts) | Loads shell/thread snapshots and paged detail |
| [`src/orchestration/Layers/ProviderCommandReactor.ts`](Core/t3code/apps/server/src/orchestration/Layers/ProviderCommandReactor.ts) | Turns persisted provider intent into adapter calls |
| [`src/orchestration/Layers/ProviderRuntimeIngestion.ts`](Core/t3code/apps/server/src/orchestration/Layers/ProviderRuntimeIngestion.ts) | Normalizes provider streams into orchestration commands |
| [`src/orchestration/Layers/CheckpointReactor.ts`](Core/t3code/apps/server/src/orchestration/Layers/CheckpointReactor.ts) | Captures baselines/completions, calculates diffs, and performs reverts |
| [`src/persistence/Layers/Sqlite.ts`](Core/t3code/apps/server/src/persistence/Layers/Sqlite.ts) | SQLite connection and persistence layer composition |
| [`src/persistence/Layers/OrchestrationEventStore.ts`](Core/t3code/apps/server/src/persistence/Layers/OrchestrationEventStore.ts) | Durable orchestration event log |
| [`src/persistence/Layers/OrchestrationCommandReceipts.ts`](Core/t3code/apps/server/src/persistence/Layers/OrchestrationCommandReceipts.ts) | Durable command idempotency receipts |
| [`src/persistence/Layers/ProjectionThreads.ts`](Core/t3code/apps/server/src/persistence/Layers/ProjectionThreads.ts) | Thread projection persistence; sibling files persist messages, turns, activities, approvals, sessions, projects, plans, and checkpoints |
| [`src/persistence/Migrations.ts`](Core/t3code/apps/server/src/persistence/Migrations.ts) | Registers ordered SQLite schema migrations |

#### Providers

| File | What it does |
| --- | --- |
| [`src/provider/ProviderDriver.ts`](Core/t3code/apps/server/src/provider/ProviderDriver.ts) | Provider driver contract |
| [`src/provider/Services/ProviderAdapter.ts`](Core/t3code/apps/server/src/provider/Services/ProviderAdapter.ts) | Common runtime adapter interface |
| [`src/provider/builtInDrivers.ts`](Core/t3code/apps/server/src/provider/builtInDrivers.ts) | Registers the five built-in drivers |
| [`src/provider/Drivers/CodexDriver.ts`](Core/t3code/apps/server/src/provider/Drivers/CodexDriver.ts) | Codex configuration and adapter construction |
| [`src/provider/Layers/CodexAdapter.ts`](Core/t3code/apps/server/src/provider/Layers/CodexAdapter.ts) | Codex app-server/session translation |
| [`src/provider/Drivers/ClaudeDriver.ts`](Core/t3code/apps/server/src/provider/Drivers/ClaudeDriver.ts) | Claude configuration and adapter construction |
| [`src/provider/Layers/ClaudeAdapter.ts`](Core/t3code/apps/server/src/provider/Layers/ClaudeAdapter.ts) | Claude Agent SDK translation |
| [`src/provider/Drivers/CursorDriver.ts`](Core/t3code/apps/server/src/provider/Drivers/CursorDriver.ts) | Cursor ACP driver |
| [`src/provider/Drivers/GrokDriver.ts`](Core/t3code/apps/server/src/provider/Drivers/GrokDriver.ts) | Grok ACP driver |
| [`src/provider/Drivers/OpenCodeDriver.ts`](Core/t3code/apps/server/src/provider/Drivers/OpenCodeDriver.ts) | OpenCode driver |
| [`src/provider/Layers/ProviderInstanceRegistryLive.ts`](Core/t3code/apps/server/src/provider/Layers/ProviderInstanceRegistryLive.ts) | Creates and owns configured provider instances |
| [`src/provider/Layers/ProviderAdapterRegistry.ts`](Core/t3code/apps/server/src/provider/Layers/ProviderAdapterRegistry.ts) | Resolves instance IDs to live adapters |
| [`src/provider/Layers/ProviderService.ts`](Core/t3code/apps/server/src/provider/Layers/ProviderService.ts) | Routes thread/session operations without leaking provider type |
| [`src/provider/ModelManifest.ts`](Core/t3code/apps/server/src/provider/ModelManifest.ts) | Loads the current/legacy model classification manifest |

#### Workspace, Git, terminal, preview, and remote support

| Path/file | What it does |
| --- | --- |
| [`src/workspace/WorkspaceFileSystem.ts`](Core/t3code/apps/server/src/workspace/WorkspaceFileSystem.ts) | Workspace read/write operations |
| [`src/workspace/WorkspaceEntries.ts`](Core/t3code/apps/server/src/workspace/WorkspaceEntries.ts) | Directory entries and workspace search data |
| [`src/workspace/WorkspaceSearchIndex.ts`](Core/t3code/apps/server/src/workspace/WorkspaceSearchIndex.ts) | Content search index |
| [`src/project/T3ProjectFileLoader.ts`](Core/t3code/apps/server/src/project/T3ProjectFileLoader.ts) | Loads project-local `t3.json` behavior |
| [`src/project/ProjectSetupScriptRunner.ts`](Core/t3code/apps/server/src/project/ProjectSetupScriptRunner.ts) | Runs worktree/project setup scripts |
| [`src/vcs/VcsDriver.ts`](Core/t3code/apps/server/src/vcs/VcsDriver.ts) | VCS abstraction including checkpoint operations |
| [`src/vcs/GitVcsDriverCore.ts`](Core/t3code/apps/server/src/vcs/GitVcsDriverCore.ts) | Git implementation of the VCS contract |
| [`src/git/GitWorkflowService.ts`](Core/t3code/apps/server/src/git/GitWorkflowService.ts) | Pull, refs, worktrees, branch workflows, and stacked actions |
| [`src/checkpointing/CheckpointStore.ts`](Core/t3code/apps/server/src/checkpointing/CheckpointStore.ts) | Hidden Git checkpoint refs |
| [`src/checkpointing/CheckpointDiffQuery.ts`](Core/t3code/apps/server/src/checkpointing/CheckpointDiffQuery.ts) | Turn and full-thread diff queries |
| [`src/terminal/Manager.ts`](Core/t3code/apps/server/src/terminal/Manager.ts) | PTY session lifecycle and subscriptions |
| [`src/preview/Manager.ts`](Core/t3code/apps/server/src/preview/Manager.ts) | Preview sessions and state |
| [`src/preview/PortScanner.ts`](Core/t3code/apps/server/src/preview/PortScanner.ts) | Local development-server discovery |
| [`src/mcp/PreviewAutomationBroker.ts`](Core/t3code/apps/server/src/mcp/PreviewAutomationBroker.ts) | Connects provider MCP calls to a client-hosted preview browser |
| [`src/auth/EnvironmentAuth.ts`](Core/t3code/apps/server/src/auth/EnvironmentAuth.ts) | Environment authentication, session, pairing, and WebSocket-ticket behavior |
| [`src/auth/RpcAuthorization.ts`](Core/t3code/apps/server/src/auth/RpcAuthorization.ts) | Per-method scope enforcement |
| [`src/cloud/ManagedEndpointRuntime.ts`](Core/t3code/apps/server/src/cloud/ManagedEndpointRuntime.ts) | Managed tunnel endpoint lifecycle |

#### Source control and operations

| Path/file | What it does |
| --- | --- |
| [`src/sourceControl/SourceControlProvider.ts`](Core/t3code/apps/server/src/sourceControl/SourceControlProvider.ts) | Common repository hosting provider contract |
| [`src/sourceControl/GitHubSourceControlProvider.ts`](Core/t3code/apps/server/src/sourceControl/GitHubSourceControlProvider.ts) | GitHub repository/source-control implementation; sibling implementations cover GitLab, Bitbucket, and Azure DevOps |
| [`src/pullRequest/PullRequestService.ts`](Core/t3code/apps/server/src/pullRequest/PullRequestService.ts) | Pull request queries, mutation routing, and caching |
| [`src/review/ReviewService.ts`](Core/t3code/apps/server/src/review/ReviewService.ts) | Local diff preview and file contents |
| [`src/usage/UsageService.ts`](Core/t3code/apps/server/src/usage/UsageService.ts) | Provider history scan and usage aggregation |
| [`src/resourceTelemetry/ResourceTelemetry.ts`](Core/t3code/apps/server/src/resourceTelemetry/ResourceTelemetry.ts) | Resource sample stream/history exposed to clients |
| [`src/diagnostics/ProcessDiagnostics.ts`](Core/t3code/apps/server/src/diagnostics/ProcessDiagnostics.ts) | Process diagnostics and inspection |

### 5.3 Contracts: `packages/contracts`

This package is the shared wire/domain language. It contains schemas and small derived helpers, not heavy runtime logic.

| File | What it defines |
| --- | --- |
| [`src/rpc.ts`](Core/t3code/packages/contracts/src/rpc.ts) | Complete typed RPC group, unary calls, streams, inputs, outputs, and errors |
| [`src/orchestration.ts`](Core/t3code/packages/contracts/src/orchestration.ts) | Project/thread commands, events, messages, activities, checkpoints, projections, permission modes, plan mode, attachments, and subscriptions |
| [`src/provider.ts`](Core/t3code/packages/contracts/src/provider.ts) | Provider capabilities and snapshots |
| [`src/providerInstance.ts`](Core/t3code/packages/contracts/src/providerInstance.ts) | Configured provider instance identity and settings |
| [`src/providerRuntime.ts`](Core/t3code/packages/contracts/src/providerRuntime.ts) | Normalized provider runtime events and requests |
| [`src/project.ts`](Core/t3code/packages/contracts/src/project.ts) | Project operations and descriptors |
| [`src/filesystem.ts`](Core/t3code/packages/contracts/src/filesystem.ts) | Filesystem browsing contracts |
| [`src/vcs.ts`](Core/t3code/packages/contracts/src/vcs.ts) | VCS status, refs, worktrees, and actions |
| [`src/git.ts`](Core/t3code/packages/contracts/src/git.ts) | Higher-level Git workflow contracts |
| [`src/terminal.ts`](Core/t3code/packages/contracts/src/terminal.ts) | Terminal lifecycle, byte stream, and metadata |
| [`src/preview.ts`](Core/t3code/packages/contracts/src/preview.ts) | Preview session, navigation, and discovered-server contracts |
| [`src/previewAutomation.ts`](Core/t3code/packages/contracts/src/previewAutomation.ts) | Agent browser automation requests/responses |
| [`src/auth.ts`](Core/t3code/packages/contracts/src/auth.ts) | Pairing, sessions, credentials, scopes, and socket tickets |
| [`src/environment.ts`](Core/t3code/packages/contracts/src/environment.ts) | Stable environment identity and descriptors |
| [`src/settings.ts`](Core/t3code/packages/contracts/src/settings.ts) | Server/client settings schemas |
| [`src/pullRequest.ts`](Core/t3code/packages/contracts/src/pullRequest.ts) | Pull request details, reviews, comments, checks, and mutations |
| [`src/resourceTelemetry.ts`](Core/t3code/packages/contracts/src/resourceTelemetry.ts) | CPU/memory/process resource samples |
| [`src/usage.ts`](Core/t3code/packages/contracts/src/usage.ts) | Usage summaries, buckets, and provider/model breakdowns |

### 5.4 Shared client runtime: `packages/client-runtime`

This package contains non-visual client behavior shared by web and mobile.

| File/path | What it does |
| --- | --- |
| [`src/connection/layer.ts`](Core/t3code/packages/client-runtime/src/connection/layer.ts) | Composes connection services |
| [`src/connection/resolver.ts`](Core/t3code/packages/client-runtime/src/connection/resolver.ts) | Converts saved targets into authenticated endpoints |
| [`src/connection/driver.ts`](Core/t3code/packages/client-runtime/src/connection/driver.ts) | Performs one prepared connection attempt |
| [`src/connection/supervisor.ts`](Core/t3code/packages/client-runtime/src/connection/supervisor.ts) | Desired state, offline handling, retry/backoff, probes, and active session ownership |
| [`src/connection/registry.ts`](Core/t3code/packages/client-runtime/src/connection/registry.ts) | Catalog and per-environment runtime scopes |
| [`src/rpc/session.ts`](Core/t3code/packages/client-runtime/src/rpc/session.ts) | Opens one typed RPC/WebSocket session; deliberately does not retry |
| [`src/rpc/client.ts`](Core/t3code/packages/client-runtime/src/rpc/client.ts) | Query, mutation, and subscription behavior across replacement sessions |
| [`src/state/shell.ts`](Core/t3code/packages/client-runtime/src/state/shell.ts) | Environment shell projection, cache, and synchronization |
| [`src/state/threads.ts`](Core/t3code/packages/client-runtime/src/state/threads.ts) | Thread summaries, pagination, subscriptions, and cached state |
| [`src/state/threadDetail.ts`](Core/t3code/packages/client-runtime/src/state/threadDetail.ts) | Individual thread detail window/state |
| [`src/state/threadCommands.ts`](Core/t3code/packages/client-runtime/src/state/threadCommands.ts) | Typed thread mutations and command metadata |
| [`src/state/projects.ts`](Core/t3code/packages/client-runtime/src/state/projects.ts) | Project state |
| [`src/state/attachments.ts`](Core/t3code/packages/client-runtime/src/state/attachments.ts) | Attachment upload/delete state |
| [`src/state/filesystem.ts`](Core/t3code/packages/client-runtime/src/state/filesystem.ts) | Workspace file operations |
| [`src/state/vcs.ts`](Core/t3code/packages/client-runtime/src/state/vcs.ts) | VCS status and actions |
| [`src/state/terminal.ts`](Core/t3code/packages/client-runtime/src/state/terminal.ts) | Terminal sessions and subscriptions |
| [`src/state/preview.ts`](Core/t3code/packages/client-runtime/src/state/preview.ts) | Preview operations and state |
| [`src/state/pullRequests.ts`](Core/t3code/packages/client-runtime/src/state/pullRequests.ts) | Pull request client state |
| [`src/state/subagentRuntime.ts`](Core/t3code/packages/client-runtime/src/state/subagentRuntime.ts) | Sub-agent presentation state |
| [`src/voice-input/`](Core/t3code/packages/client-runtime/src/voice-input) | Platform-neutral recording/transcription lifecycle |

The package intentionally has no broad root export. Applications import narrow domain subpaths so internal files can change without widening the public API.

### 5.5 Web UI: `apps/web`

The web application is the main visual surface and is also rendered inside Electron.

| File/path | What it does |
| --- | --- |
| [`src/main.tsx`](Core/t3code/apps/web/src/main.tsx) | React root, web/Electron routing history, optional cloud auth, and global styles |
| [`src/AppRoot.tsx`](Core/t3code/apps/web/src/AppRoot.tsx) | Renderer-wide state runtime, router, preview automation hosts, persistent Electron browser host, and quit overlay |
| [`src/router.ts`](Core/t3code/apps/web/src/router.ts) | TanStack Router setup |
| [`src/routes/`](Core/t3code/apps/web/src/routes) | Chat, draft, pull request, project, usage, connection, pairing, and settings pages |
| [`src/connection/runtime.ts`](Core/t3code/apps/web/src/connection/runtime.ts) | Web platform composition for the shared connection runtime |
| [`src/components/ChatView.tsx`](Core/t3code/apps/web/src/components/ChatView.tsx) | Main thread workspace layout |
| [`src/components/chat/MessagesTimeline.tsx`](Core/t3code/apps/web/src/components/chat/MessagesTimeline.tsx) | Conversation timeline |
| [`src/components/chat/ChatComposer.tsx`](Core/t3code/apps/web/src/components/chat/ChatComposer.tsx) | High-level composer |
| [`src/components/chat/ComposerSurface.tsx`](Core/t3code/apps/web/src/components/chat/ComposerSurface.tsx) | Composer editor, attachments, menus, banners, and actions |
| [`src/components/chat/ComposerPendingApprovalPanel.tsx`](Core/t3code/apps/web/src/components/chat/ComposerPendingApprovalPanel.tsx) | Inline approval UX |
| [`src/components/AgentsPanel.tsx`](Core/t3code/apps/web/src/components/AgentsPanel.tsx) | Sub-agent hierarchy/status |
| [`src/components/files/FileBrowserPanel.tsx`](Core/t3code/apps/web/src/components/files/FileBrowserPanel.tsx) | Workspace tree and file navigation |
| [`src/components/files/FilePreviewPanel.tsx`](Core/t3code/apps/web/src/components/files/FilePreviewPanel.tsx) | File view/edit/preview surface |
| [`src/components/DiffPanel.tsx`](Core/t3code/apps/web/src/components/DiffPanel.tsx) | Thread/local diff surface |
| [`src/components/ThreadTerminalDrawer.tsx`](Core/t3code/apps/web/src/components/ThreadTerminalDrawer.tsx) | Thread terminal UI |
| [`src/components/preview/PreviewPanel.tsx`](Core/t3code/apps/web/src/components/preview/PreviewPanel.tsx) | Embedded browser preview UI |
| [`src/components/pullRequest/PullRequestDetailPanel.tsx`](Core/t3code/apps/web/src/components/pullRequest/PullRequestDetailPanel.tsx) | Pull request summary, code, timeline, and review tabs |
| [`src/components/CommandPalette.tsx`](Core/t3code/apps/web/src/components/CommandPalette.tsx) | Central action discovery |
| [`src/components/Sidebar.tsx`](Core/t3code/apps/web/src/components/Sidebar.tsx) | Environment/project/thread navigation |
| [`src/components/settings/SettingsPanels.tsx`](Core/t3code/apps/web/src/components/settings/SettingsPanels.tsx) | Settings navigation and panels |
| [`src/components/usage/UsagePage.tsx`](Core/t3code/apps/web/src/components/usage/UsagePage.tsx) | Usage charts and summaries |

### 5.6 Electron desktop: `apps/desktop`

Electron is a shell and local process manager around the web UI; it does not duplicate the conversation UI.

| File/path | What it does |
| --- | --- |
| [`src/main.ts`](Core/t3code/apps/desktop/src/main.ts) | Electron dependency composition and application entry point |
| [`src/app/DesktopApp.ts`](Core/t3code/apps/desktop/src/app/DesktopApp.ts) | Ordered bootstrap, local backend start, window creation, WSL reconciliation, and shutdown |
| [`src/preload.ts`](Core/t3code/apps/desktop/src/preload.ts) | Narrow renderer-to-main bridge for settings, dialogs, SSH, WSL, updates, window controls, and previews |
| [`src/backend/DesktopBackendPool.ts`](Core/t3code/apps/desktop/src/backend/DesktopBackendPool.ts) | Owns one or more desktop-managed server backends |
| [`src/backend/DesktopServerExposure.ts`](Core/t3code/apps/desktop/src/backend/DesktopServerExposure.ts) | Loopback/network exposure and advertised endpoints |
| [`src/window/DesktopWindow.ts`](Core/t3code/apps/desktop/src/window/DesktopWindow.ts) | Main window lifecycle |
| [`src/ssh/DesktopSshEnvironment.ts`](Core/t3code/apps/desktop/src/ssh/DesktopSshEnvironment.ts) | SSH host discovery, remote launch, tunnel, and reconnect bridge |
| [`src/wsl/DesktopWslBackend.ts`](Core/t3code/apps/desktop/src/wsl/DesktopWslBackend.ts) | Windows/WSL backend lifecycle |
| [`src/preview/Manager.ts`](Core/t3code/apps/desktop/src/preview/Manager.ts) | Embedded browser tabs, automation, element picking, screenshots, recording, and picture-in-picture |
| [`src/updates/DesktopUpdates.ts`](Core/t3code/apps/desktop/src/updates/DesktopUpdates.ts) | Desktop update state machine and installer operations |
| [`src/ipc/DesktopIpcHandlers.ts`](Core/t3code/apps/desktop/src/ipc/DesktopIpcHandlers.ts) | Registers main-process handlers used by the preload bridge |

### 5.7 Other applications and packages

| Path | Responsibility |
| --- | --- |
| [`apps/mobile`](Core/t3code/apps/mobile) | Expo/React Native iOS and Android client with native navigation, terminal, Markdown, review diff, sharing, voice, and notifications |
| [`apps/marketing`](Core/t3code/apps/marketing) | Astro marketing site |
| [`packages/shared`](Core/t3code/packages/shared) | Framework-neutral workers, Git helpers, auth/signing, logging, paths, themes, search ranking, and other utilities |
| [`packages/ssh`](Core/t3code/packages/ssh) | SSH config, authentication prompts, command execution, remote server launch, and tunneling |
| [`packages/tailscale`](Core/t3code/packages/tailscale) | Tailscale CLI and Serve lifecycle wrapper |
| [`packages/effect-acp`](Core/t3code/packages/effect-acp) | Agent Client Protocol client/agent implementation for ACP providers |
| [`packages/effect-codex-app-server`](Core/t3code/packages/effect-codex-app-server) | Typed client for Codex app-server JSON-RPC |
| [`infra/relay`](Core/t3code/infra/relay) | Hosted connect discovery/auth and notification infrastructure; not the application data path after connection |

## 6. Current PiStationDesktop structure and T3 mapping

PiStationDesktop already has the correct five-way separation:

| PiStationDesktop project | Current responsibility | Closest T3 Code area |
| --- | --- | --- |
| [`PiStation.Protocol`](PiStationDesktop/src/PiStation.Protocol) | Versioned identifiers, commands, receipts, errors, projections, and stream envelopes | `packages/contracts` |
| [`PiStation.Host`](PiStationDesktop/src/PiStation.Host) | Authoritative environment, project/thread metadata, command receipts, thread journals, loopback SignalR host | `apps/server` orchestration/persistence/RPC |
| [`PiStation.PiRpc`](PiStationDesktop/src/PiStation.PiRpc) | Pi discovery, subprocess launch, JSONL transport, native event decoding, and stream assembly | T3 provider driver plus adapter/protocol package |
| [`PiStation.ClientRuntime`](PiStationDesktop/src/PiStation.ClientRuntime) | SignalR connection supervision, protocol handshake, subscriptions, projection cache, and uncertain-command handling | `packages/client-runtime` |
| [`PiStation.App`](PiStationDesktop/src/PiStation.App) | WinUI shell, projects/threads, transcript, tools, composer, and recovery banners | `apps/web` plus the native desktop shell |

### 6.1 Important PiStationDesktop files today

| File | What it does now |
| --- | --- |
| [`Protocol/Commands/ThreadCommands.cs`](PiStationDesktop/src/PiStation.Protocol/Commands/ThreadCommands.cs) | Runtime, interaction, draft, configuration, and revisioned lifecycle commands in an idempotent request envelope |
| [`Protocol/Projections/ThreadProjection.cs`](PiStationDesktop/src/PiStation.Protocol/Projections/ThreadProjection.cs) | Authoritative runtime state, messages, thinking, tools, Pi session identity, cursor, and error |
| [`Protocol/Streaming/ThreadStreaming.cs`](PiStationDesktop/src/PiStation.Protocol/Streaming/ThreadStreaming.cs) | Snapshot, incremental event, and resync-required envelopes |
| [`Host/EnvironmentService.cs`](PiStationDesktop/src/PiStation.Host/EnvironmentService.cs) | Environment facade, command receipt acquisition, dispatch, validation, and subscription |
| [`Host/Threads/PiThreadController.cs`](PiStationDesktop/src/PiStation.Host/Threads/PiThreadController.cs) | Per-thread Pi process/session lifecycle and command handling |
| [`Host/Threads/ThreadEventJournal.cs`](PiStationDesktop/src/PiStation.Host/Threads/ThreadEventJournal.cs) | In-memory projection updates, bounded event retention, replay from a retained cursor, and snapshot/resync fallback |
| [`Host/Persistence/HostDatabase.cs`](PiStationDesktop/src/PiStation.Host/Persistence/HostDatabase.cs) | SQLite environment, revisioned project/thread metadata, lifecycle filtering/search, schema migration, and command receipts |
| [`Host/Hosting/EmbeddedEnvironmentHost.cs`](PiStationDesktop/src/PiStation.Host/Hosting/EmbeddedEnvironmentHost.cs) | Authenticated loopback Kestrel/SignalR host on an ephemeral port |
| [`PiRpc/Transport/PiRpcConnection.cs`](PiStationDesktop/src/PiStation.PiRpc/Transport/PiRpcConnection.cs) | Pi JSONL request/response/event connection |
| [`PiRpc/Decoding/PiEventDecoder.cs`](PiStationDesktop/src/PiStation.PiRpc/Decoding/PiEventDecoder.cs) | Native Pi event decoding |
| [`PiRpc/Decoding/PiStreamAssembler.cs`](PiStationDesktop/src/PiStation.PiRpc/Decoding/PiStreamAssembler.cs) | Incremental assistant/thinking/tool stream assembly |
| [`ClientRuntime/ConnectionSupervisor.cs`](PiStationDesktop/src/PiStation.ClientRuntime/ConnectionSupervisor.cs) | SignalR lifecycle and automatic reconnect states |
| [`ClientRuntime/EnvironmentClient.cs`](PiStationDesktop/src/PiStation.ClientRuntime/EnvironmentClient.cs) | Host calls, handshake, lifecycle/search operations, revision-safe metadata/configuration caches, subscriptions, reconnect refresh, and receipt resolution |
| [`ClientRuntime/ProjectionStore.cs`](PiStationDesktop/src/PiStation.ClientRuntime/ProjectionStore.cs) | Applies snapshots/events and detects resync needs |
| [`App/ViewModels/ShellViewModel.cs`](PiStationDesktop/src/PiStation.App/ViewModels/ShellViewModel.cs) | Shell coordinator for environment calls and cross-feature project/thread/turn actions |
| [`App/ViewModels/WorkspaceViewModel.cs`](PiStationDesktop/src/PiStation.App/ViewModels/WorkspaceViewModel.cs) | Workspace/project/thread selection, search, archive-mode, and lifecycle presentation state |
| [`App/ViewModels/ThreadViewModel.cs`](PiStationDesktop/src/PiStation.App/ViewModels/ThreadViewModel.cs) | Active-thread projection and normalized conversation timeline |
| [`App/ViewModels/ComposerViewModel.cs`](PiStationDesktop/src/PiStation.App/ViewModels/ComposerViewModel.cs) | Per-thread draft text, attachment, persistence, and send-state presentation |
| [`App/ViewModels/PiConfigurationViewModel.cs`](PiStationDesktop/src/PiStation.App/ViewModels/PiConfigurationViewModel.cs) | Capability-driven model/reasoning selections and revision status |
| [`App/ViewModels/ConnectionViewModel.cs`](PiStationDesktop/src/PiStation.App/ViewModels/ConnectionViewModel.cs) | Connection, runtime-error, transport-error, and uncertain-command presentation state |
| [`App/ViewModels/FileMentionViewModel.cs`](PiStationDesktop/src/PiStation.App/ViewModels/FileMentionViewModel.cs) | File-mention suggestions, selection, visibility, and search feedback |
| [`App/Views/ShellPage.xaml`](PiStationDesktop/src/PiStation.App/Views/ShellPage.xaml) | Current project selector, thread tabs, transcript, tools, composer, and recovery UI |

### 6.2 Capability comparison

| Capability | PiStationDesktop status | Guidance |
| --- | --- | --- |
| Provider process isolation | Implemented for Pi | Keep all Pi-native JSONL details inside `PiStation.PiRpc` |
| Typed/versioned contract | Implemented for the MVP | Expand before adding new UI features |
| Durable command IDs/receipts | Implemented | Reuse for every new mutation; never retry an uncertain command with a new ID automatically |
| Resumable thread stream | Implemented | Preserve snapshot plus cursor/event semantics |
| Crash versus transport error | Implemented | Keep these as separate state families |
| Durable project/thread metadata | Implemented for rename/archive/pin/search | Protocol v9 and SQLite own revisioned title, archive, and pin state with migration-safe persistence, filtering, and title search. ClientRuntime provides typed operations, a revision-safe cache, and reconnect refresh. WinUI exposes debounced search, active/archived shelves, inline rename, pin/archive actions, accessible status, and relaunch-tested persistence |
| General event-sourced orchestration | Not implemented; related primitives exist | The current bounded journal supports streaming but is not a durable event store; commands call services directly. Introduce a pure decider/projector only when broader lifecycle features require it |
| Rich conversation timeline | Implemented | Typed assistant, thinking, tool, approval, question, status, error, and turn-boundary items are projected end to end. Assistant messages render native CommonMark with safe links, inert raw HTML, selectable text, and highlighted copyable code. Native expanders collapse completed reasoning, keep active reasoning visible, and group adjacent tools with accessible running/completed/failed state plus bounded arguments/output. Protocol v11 aggregates Pi-reported token usage per user turn and combines the last trustworthy response usage with the active model context window; the compact completion footer also shows host-measured elapsed time and survives session hydration without estimating absent values |
| Approvals and structured questions | Implemented for local flows | Typed approve, reject/cancel, and answer commands render inline, reject obsolete responses, and are covered by a packaged-app journey |
| Attachments and file mentions | Core implemented | Host-owned durable drafts and attachments feed Pi turns; bounded workspace filename search backs the composer `@` picker |
| Model/mode selection | Implemented for reported capabilities | Introduced in protocol v8, Pi-reported capabilities are validated, persisted, reapplied, and receipt-tracked by the host; ClientRuntime caches revisioned snapshots across reconnects; and WinUI renders model/reasoning selectors. Runtime-mode controls remain hidden while Pi reports none |
| Checkpoint diff/revert | Missing | High-value differentiator; implement behind a VCS service |
| Files, search, and editor integration | Partial | Host-owned bounded filename search and composer mentions are implemented. File browsing, content search, read/write, preview, and editor integration remain |
| Terminal | Missing | Server-owned PTY with a separately streamed byte channel |
| Git branches/worktrees/status | Missing | Add after checkpoint/VCS abstraction |
| Remote pairing/multiple environments | Architecture anticipated, not shipped | Strengthen auth and connection catalog before binding beyond loopback |
| Mobile/web clients | Not in current scope | Do not distort the WinUI MVP for these until requested |

## 7. Recommended PiStationDesktop feature direction

### Guiding rules

1. Keep Pi as the only provider until Pi-specific daily use is excellent. Do not copy T3's multi-provider registry prematurely.
2. Keep `PiStation.Host` authoritative. WinUI should never launch Pi, run Git, read workspace files, or own retry policy directly.
3. Add protocol contracts first, host behavior second, client-runtime state third, and UI last.
4. Treat recovery states as product states: connecting, synchronizing, offline, Pi crashed, command uncertain, permission requested, and resync required must remain distinct.
5. Every mutation needs a stable command ID, a durable receipt, validation against current state, and an inverse action when the domain permits one.
6. Add narrow subscriptions by domain instead of expanding one high-volume global stream forever.
7. Prefer feature slices that complete host, protocol, runtime, UI, deterministic tests, and accessibility IDs together.

### Priority 0: preserve and strengthen the foundation

- **Complete (2026-09-02):** Split presentation ownership out of the large `ShellViewModel` while retaining it as the cross-feature coordinator. Focused workspace, thread, composer, Pi-configuration, connection/recovery, and file-mention models now own their observable state, and `ShellPage` binds to those models directly.
- Expand the current `EnvironmentDescriptor.Capabilities` list into a structured capability snapshot as optional features and Pi-version conditions multiply.
- Make connection retry/backoff, explicit retry, offline state, and synchronization state independently observable.
- Add pagination/windowing contracts before transcripts become large.
- Add schema migration tests and preserve old Pi-session hydration/protocol serialization tests for every persisted contract change.
- Introduce a host-side command dispatcher/decider boundary before project/thread lifecycle commands multiply. It does not need to reproduce T3's Effect architecture; a small C# pure decision layer is enough.

### Priority 1: complete the agent conversation loop

#### 1. Rich timeline and activity model

Implementation status (2026-09-02): **complete**. Assistant text,
thinking, tool state, approval, question, progress/status, command/runtime error, and turn-boundary
items are typed and rendered end to end. Assistant messages use an app-owned native WinUI renderer:
Markdig provides the CommonMark syntax tree, `RichTextBlock` renders formatted prose and safe links,
and dedicated ColorCode-backed code surfaces provide syntax highlighting, selection, scrolling,
language labels, and accessible copy actions. Raw HTML is shown as inert text and user messages stay
plain. Protocol v10 introduced bounded Pi tool arguments alongside the existing bounded output.
Completed reasoning collapses by default while active reasoning stays open. Consecutive tool calls
in one turn render in a compact grouped surface; each keyboard-native row exposes accessible
running/completed/failed state and expandable arguments/output, with running and failed rows open by
default. Protocol v11 adds per-turn metrics: the host measures elapsed time, aggregates Pi's reported
input/output/cache/reasoning usage across every response in the user turn, and pairs the latest
trustworthy response usage with Pi's active-model context window. The completion divider presents a
compact elapsed/token/context summary with an accessible breakdown. Missing usage remains unknown,
and persisted Pi timestamps and usage reconstruct the same metadata after relaunch. The packaged
vertical journey covers streamed Markdown, code, copy feedback, raw-HTML safety, reasoning
disclosure, two-tool grouping, arguments/output, live metrics, and hydrated metrics.

Add normalized timeline items for:

- assistant text and thinking;
- tool calls with running/completed/failed states;
- approvals and structured user questions;
- progress/status activities;
- command/runtime failures;
- turn boundary, elapsed time, token/context information when Pi reports it.

Suggested ownership:

- `PiStation.Protocol`: timeline projection/event union.
- `PiStation.PiRpc`: map Pi events into the union.
- `PiStation.Host`: persist/project/order items.
- `PiStation.ClientRuntime`: incremental reducer and retention.
- `PiStation.App`: item templates and accessible actions.

#### 2. Approval and user-input workflows

Implementation status (2026-09-02): **complete for the current local architecture**. Typed
approval, rejection/cancellation, and structured-answer commands flow through Protocol, Host,
ClientRuntime, and inline WinUI cards. Pending items are part of the reconnectable thread
projection, obsolete responses are disabled/rejected, and the deterministic interaction journey
verifies that FakePi receives exactly one visible response.

Add protocol commands for approve, reject/cancel, and structured answers. Pending requests must be durable enough to reappear after reconnect. The UI should show them inline at the point where the agent paused and disable obsolete responses.

#### 3. Composer essentials

Implementation status (2026-09-02): **partial**. Per-thread draft persistence, attachment
validation, authenticated host-owned uploads, durable attachment IDs, image and generic file turns,
receipt-led exact-revision clearing, bounded workspace filename search, and the keyboard/mouse `@`
picker are implemented. General send/stop shortcuts, explicit prompt-length UX, slash commands/Pi
skills, and prompt history or stash remain.

Add:

- send/stop keyboard behavior;
- draft persistence per environment/thread;
- prompt length and attachment validation;
- image and generic file attachments;
- file/path mention search;
- slash commands and Pi skills if Pi exposes them;
- prompt history or stash.

Uploads should go to the host first and receive durable attachment IDs/paths before a turn command references them.

#### 4. Per-thread Pi configuration

Implementation status (2026-09-02): **complete for Pi's currently reported capabilities**. Protocol
v8 defines typed model, thinking-level, runtime-mode capability, configuration, snapshot, and update
contracts. The host discovers model/thinking options from Pi RPC, rejects unsupported values,
persists optimistic revisions in SQLite, reapplies settings after restart, and uses durable command
receipts. Pi currently advertises no permission/runtime modes, so non-null runtime-mode requests are
rejected. ClientRuntime reads and updates settings through stable command IDs, retains capability
snapshots without allowing stale revisions to win, refreshes tracked threads after reconnect, and
surfaces typed conflict/unsupported/invalid, disconnected, and uncertain-dispatch failures. WinUI
shows only reported model/reasoning options, safely falls back to reasoning `Off` for a
non-reasoning model, reloads conflicts, and hides unavailable runtime-mode controls. A packaged-app
journey verifies selection, revision updates, capability fallback, and relaunch persistence.

Expose only settings Pi can actually honor:

- model/provider configuration exposed by Pi;
- thinking/reasoning level;
- permission/runtime mode;
- default versus plan behavior if Pi supports it;
- session resume/restart controls.

Persist selections on the thread. The host should validate capability/version support rather than trusting the UI.

#### 5. Thread lifecycle and organization

Implementation status (2026-09-02): **complete end to end for rename, archive/unarchive, pin/unpin,
and title search**. Protocol v9 adds lifecycle commands and revisioned descriptor fields.
The host validates optimistic revisions, persists lifecycle state through SQLite schema migration,
uses durable command receipts, excludes archived threads from normal lists/search by default, and
sorts pinned threads first. Host tests cover replay, stale conflicts, reverse operations, migration,
search limits/filtering, ordering, and restart persistence without deleting or starting Pi sessions.
ClientRuntime exposes typed lifecycle/search methods, applies revision/timestamp-safe metadata,
refreshes tracked projects after reconnect, and separates conflict, invalid, disconnected, and
uncertain-dispatch outcomes. WinUI adds debounced title search, separate active and archived shelves,
inline keyboard-friendly rename, pin/unpin and archive/restore actions through both overflow and
context menus, live empty/result/status feedback, and conflict-safe refresh. A packaged-app journey
verifies search, rename, pinning, archive/restore, and persistence across relaunch. Delete/restore,
manual ordering, title regeneration, snooze, and settled/reactivated remain later lifecycle
extensions.

Add rename, delete, archive/unarchive, pin/unpin and ordering, search, and settled/reactivated states. Add reverse commands at the same time as forward commands. Keep timestamps and sorting rules in projections so multiple future clients agree.

### Priority 2: make PiStationDesktop a coding workspace

#### 6. Checkpoints, changed files, diff, and revert

This is the best next architectural feature after the conversation loop.

- Add a `IVersionControlService`/driver boundary in `PiStation.Host`.
- Capture a baseline and completion checkpoint for each turn as hidden Git refs.
- Project changed-file summaries into the thread.
- Query full patches separately so the main thread stream stays small.
- Revert through a command with explicit confirmation, durable receipt, and conflict/error states.
- Coordinate Pi conversation rewind if Pi provides a safe resume point; otherwise state clearly that workspace-only revert does not rewind agent memory.

#### 7. File explorer, search, and preview

Add host-owned browse, filename search, content search, and read operations. Add writes only when the editor feature is ready. Normalize paths relative to the project root, enforce root containment, and return explicit binary/large-file errors. Start with source/Markdown/image preview and open-in-editor.

#### 8. Git status, branches, and worktrees

Add live status, refresh, branch list/create/switch, pull, and worktree create/remove. A thread worktree should be durable thread metadata and the Pi process working directory. Run setup scripts as an explicit, visible operation with logs and failure state.

#### 9. Integrated terminal

Keep PTYs in the host. Use a dedicated terminal contract for open/attach/write/resize/restart/close and stream raw bytes independently of thread events. Preserve terminal metadata across reconnect while treating a dead PTY as different from a lost SignalR connection.

### Priority 3: desktop power features

#### 10. Command palette and keybindings

Create one command registry used by buttons, menus, palette entries, and keybindings. Commands should declare availability from current app state so shortcuts cannot bypass disabled UI invariants.

#### 11. Project configuration and scripts

Introduce a small `pistation.json` only after there are at least two real project-level settings. Likely fields are icon path, setup script, named scripts, default model/mode, and worktree setup. Validate it on the host and surface errors without blocking unrelated project use.

#### 12. Browser preview

Start with detected localhost ports and an embedded WebView2 panel. Add responsive sizes, navigation, reload, dev tools, screenshots, and element-to-prompt annotations in stages. Agent-driven browser automation should be a later, permissioned host/client bridge rather than direct automation from the chat ViewModel.

#### 13. Usage and diagnostics

Show Pi version, executable path, current session/process state, host/connection state, recent errors, and redacted diagnostic export. Add token/cost charts only when Pi supplies trustworthy usage data.

### Priority 4: remote and multi-environment operation

Only bind beyond loopback after these pieces exist:

- a persistent client connection catalog;
- stable environment identity;
- one-time pairing credentials;
- renewable/revocable sessions;
- short-lived WebSocket/SignalR connection tickets or equivalent;
- per-method authorization scopes;
- TLS or a trusted private transport;
- independent supervisor and cache per environment;
- clear protocol/version-skew UX.

Tailscale or SSH can then be endpoint/launch helpers. They should not create separate domain models. A remote environment still owns its own Pi process, files, Git repository, terminal, and state.

## 8. Proposed PiStationDesktop file ownership as features grow

```text
PiStation.Protocol
  Contracts only
  - identifiers and versioning
  - commands, events, receipts, projections
  - capability and error schemas

PiStation.Host
  Authoritative execution environment
  - command validation/decision
  - event and projection persistence
  - projects, threads, files, Git, checkpoints, terminal
  - authentication and subscriptions

PiStation.PiRpc
  Pi adapter boundary
  - discovery and launch
  - JSONL transport
  - native Pi event decoding
  - capability/model/session translation

PiStation.ClientRuntime
  Non-visual client policy
  - connection supervision
  - RPC/SignalR session
  - cached projections and reducers
  - domain operations and uncertain-command recovery

PiStation.App
  WinUI presentation
  - navigation and panes
  - view models and commands
  - accessible controls and templates
  - no direct Pi, filesystem, Git, or database access
```

Suggested future folders:

```text
src/PiStation.Protocol/
  Attachments/
  Checkpoints/
  Files/
  Git/
  Terminal/

src/PiStation.Host/
  Orchestration/
  Checkpoints/
  Files/
  Git/
  Terminal/

src/PiStation.ClientRuntime/
  Connection/
  State/Threads/
  State/Files/
  State/Git/
  State/Terminal/

src/PiStation.App/
  Features/Conversation/
  Features/Composer/
  Features/Files/
  Features/Diffs/
  Features/Terminal/
  Features/Settings/
```

Do not reorganize merely to match this tree. Move code when a new feature creates a real ownership boundary and move its tests in the same change.

## 9. Feature slice checklist

Use this checklist for each PiStationDesktop feature:

- Contract: typed request/event/projection/error and protocol compatibility defined.
- Authority: host owns validation, ordering, persistence, and side effects.
- Idempotency: mutation has a stable command ID and durable receipt.
- Provider mapping: Pi-native capabilities and failure modes are translated explicitly.
- Streaming: snapshot, cursor, resume, and resync behavior are defined.
- Reconnect: UI behavior is correct before dispatch, during uncertain dispatch, and after reconnect.
- Reverse state: remove/restore, archive/unarchive, pin/unpin, or an explicit irreversible confirmation exists.
- UI entry points: page controls, context menu, command palette, and shortcut agree where applicable.
- Accessibility: stable Automation IDs, names, focus order, keyboard operation, and status announcements.
- Performance: large output, high-frequency deltas, pagination, cancellation, and disposal are covered.
- Security: workspace containment, secret redaction, permissions, and remote scope are covered.
- Tests: protocol serialization, pure reducer/decision tests, host integration, FakePi integration, and one black-box WinUI journey.
- Diagnostics: failure includes enough correlation IDs and state to troubleshoot without exposing secrets.

## 10. What not to copy yet

T3 Code solves a larger product problem than the current PiStationDesktop MVP. Defer these unless they become explicit product requirements:

- five-provider abstraction and multi-account provider management;
- React/Effect/Atom patterns themselves;
- hosted relay/cloud authentication infrastructure;
- mobile-specific navigation, voice, sharing, live activities, and push notifications;
- Electron IPC and Chromium-specific browser implementation;
- four source-control hosting providers at once;
- self-update/background-service machinery;
- a broad plugin or project configuration system before concrete use cases exist.

Copy the invariants and boundaries, not incidental technologies.

## 11. Recommended implementation order

1. **Complete (2026-09-02):** Refine thread timeline projection and split the growing shell
   ViewModel. The typed timeline, native assistant Markdown/code presentation, collapsible
   reasoning, grouped expandable tool activity, and protocol-v11 elapsed/token/context turn footer
   are implemented. The shell coordinates focused workspace, thread, composer, Pi-configuration,
   connection/recovery, and file-mention models.
2. **Complete (2026-09-01):** Add approvals and structured user questions end to end.
3. **Complete (2026-09-02):** Add draft persistence, attachments, and file mentions.
4. **Complete (2026-09-02):** Add per-thread Pi
   model/reasoning/permission settings based on reported capabilities. Protocol, Pi RPC mapping,
   host validation, receipts, SQLite persistence, and reconnect-safe ClientRuntime state are
   integrated with capability-driven WinUI selectors and packaged-app persistence coverage.
5. **Complete (2026-09-02):** Add thread rename/archive/pin/search lifecycle. Protocol v9, the
   authoritative host/SQLite layer, and reconnect-safe ClientRuntime APIs/cache are complete,
   including inverse commands, receipts, optimistic conflicts, filtering, ordering, search,
   migration, typed client failures, and restart/reconnect coverage. The first T3-inspired WinUI
   design slice is also complete: dark-first light/dark/high-contrast semantic tokens, compact
   typography and geometry, and reusable surface, button, input, pill, status, sidebar-row, and
   transcript styles are applied to the current shell. The generic `NavigationView` has now been
   replaced by an integrated compact title bar, fixed workspace rail, active-thread header, centered
   reading column, and unified project/thread hierarchy with accessible selection and persistent
   relaunch behavior. The sidebar now includes debounced search, active/archived shelves, inline
   rename, pin and archive actions, accessible live status, keyboard handling, and conflict-safe
   refresh. A packaged-app journey covers the full lifecycle and verifies persistence after relaunch.
6. Rebuild the WinUI presentation shell through the staged GUI-first program in
   `PISTATION-T3CODE-GUI-PARITY-PLAN.md`. The sidebar, header, continuous timeline, integrated
   composer, responsive states, and right-panel host land before their future workbench backends.
7. Add VCS abstraction, per-turn checkpoints, changed-file summary, diff, and revert.
8. Add workspace file browse/search/preview and open-in-editor.
9. Add Git branch/worktree workflows.
10. Add host-owned terminal streaming.
11. Add command palette/keybindings and project scripts.
12. Add WebView2 local preview and annotations.
13. Harden remote authentication, then add multiple saved environments.

This order preserves the working Pi vertical slice while growing outward from the highest-value agent workflow. It also avoids building remote, Git-hosting, or browser infrastructure before the conversation and recovery model can support them reliably.

## 12. Primary T3 Code references

- [`docs/internals/overview.md`](Core/t3code/docs/internals/overview.md) — architecture and event flow
- [`docs/internals/workspace-layout.md`](Core/t3code/docs/internals/workspace-layout.md) — monorepo ownership
- [`docs/internals/glossary.md`](Core/t3code/docs/internals/glossary.md) — domain terminology
- [`docs/internals/providers.md`](Core/t3code/docs/internals/providers.md) — provider driver/adapter model
- [`docs/internals/connection-runtime.md`](Core/t3code/docs/internals/connection-runtime.md) — retry, cache, and session supervision
- [`docs/internals/remote.md`](Core/t3code/docs/internals/remote.md) — environment and remote-access model
- [`docs/internals/environment-auth.md`](Core/t3code/docs/internals/environment-auth.md) — authentication and method authorization
- [`docs/architecture/terminal-renderers.md`](Core/t3code/docs/architecture/terminal-renderers.md) — server-owned PTY and client rendering
- [`docs/user/composer.md`](Core/t3code/docs/user/composer.md) — composer, attachments, stash, commands, and skills
- [`docs/user/permission-modes.md`](Core/t3code/docs/user/permission-modes.md) — user-facing runtime modes
- [`docs/user/source-control.md`](Core/t3code/docs/user/source-control.md) — Git hosting and review features
- [`docs/user/remote-access.md`](Core/t3code/docs/user/remote-access.md) — pairing, Tailscale, headless, and SSH workflows

T3 Code is MIT licensed. If PiStationDesktop later copies implementation code rather than borrowing concepts, preserve the license and required notices.
