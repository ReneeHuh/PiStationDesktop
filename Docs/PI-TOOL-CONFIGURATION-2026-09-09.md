# Pi tool configuration and Windows PowerShell

Implementation: TOOL-05 and TOOL-07 in [the master tracker](../tracking.md).
Wire protocol: **46**. Update both Windows computers manually before connecting.
Dedicated tool selection requires **Pi 0.85.0 or newer**. Existing Pi 0.84.4
workflows remain supported with Pi defaults and no dedicated exclusions.

## Configure the host

Open Settings → Pi / runtime → Agent tools and Windows PowerShell. These are
settings for the selected environment's host, not global Pi TUI settings.
Saving requires operate access. Choose:

- **Pi defaults / existing launch flags:** preserve Pi's default tools and any
  existing advanced tool-selection arguments.
- **Only the listed tools:** use exact, case-sensitive built-in or extension tool
  names, separated by commas or newlines. An empty allowlist enables no tools.
- **No agent tools:** disable all model-callable tools, including extension tools.

Exclusions take precedence. Lists are limited to 128 names each; names are limited
to 128 ASCII letters, digits, underscores, hyphens, or dots. Wildcards are not supported.
Conflicting advanced `--tools`, `--exclude-tools`, `--no-tools`, or
`--no-builtin-tools` flags (including short forms) must be removed before using a
dedicated allowlist, no-tools mode, or exclusions. Pi defaults with no exclusions
keeps legacy launch arguments intact.

The Read/search preset selects `read, grep, find, ls`. The Windows coding preset
adds `powershell, edit, write`; it does not silently enable other extension tools.
Presets retain existing exclusions. Add required extension names explicitly—for
example, `pistation_subagent` for delegation. The Add PowerShell button appends it
to the allowlist without duplicating it or clearing exclusions.

Choose **Save and connect**, finish any active work, and restart each idle thread
that should use the new policy. Existing processes keep their launch policy.
Runtime settings survive host/app restart; rejected settings do not replace the
previous saved configuration. Saving does not edit shared Pi settings or install Pi.

## Runtime inventory and enforcement

Refresh selected thread's tool inventory reports Pi SDK `getAllTools()` and
`getActiveTools()` for that idle runtime. It shows names, short descriptions,
source attribution, registered/active state, and the policy captured at launch.
The inventory is bounded to 256 entries. Missing reports and truncated inventories
are explicitly identified; a missing PowerShell entry is not called installed or
available. Requested names absent from a complete registry are reported separately.
This is a snapshot, not live monitoring. Refresh after restart, plan changes, or
extension changes. Runtime/epoch/plan transitions clear the displayed tool inventory.

Pi's allowlist/exclusion filters the registry, so an extension calling
`setActiveTools` cannot re-add a filtered-out tool. Equivalent `pistation_plan_*`
read/search names are included or excluded together with their ordinary built-ins.
Planning still blocks non-planning tools at dispatch, even if another extension
selects a registered mutation tool. Active means selected for the model, not
permission to execute without review.

PiStation-owned child sessions intersect their preset tools with the dedicated
host policy. A child cannot restore an excluded tool by selecting a broader
preset. Worker presets support PowerShell when the Windows Pi SDK provides it;
existing saved presets are not rewritten. If `pistation_subagent` is filtered out,
the Agents panel reports delegation unavailable and preparation is rejected.
Legacy raw CLI flags retain their existing parent-runtime semantics.

This is **not an OS sandbox**. Trusted extension code can execute Node APIs,
arbitrary external-extension children have their own contracts, and user-run
terminal/Pi-shell commands are separate from model-callable tool selection.

## PowerShell behavior

The model calls Pi's real Windows `powershell` tool. Pi resolves `pwsh.exe`, then
`powershell.exe`, on the host. Default Pi selection is unchanged until the user
chooses a policy/preset. PowerShell output, failures, and cancellation use existing
tool timeline/recovery paths. Supervised, Auto-accept edits, and Auto permission
modes require explicit shell approval; only Full access skips that approval.
Planning restrictions remain applicable in every permission mode.

## Verification

Managed policy/serialization tests cover defaults, no tools, empty allowlists,
planning aliases, exclusions, invalid names/limits, and conflicting arguments.
Host/client tests cover persistence, legacy-version rejection, process environment
readback, authenticated inventory transport, unchanged drafts, and preservation of
tool settings while editing unrelated launch fields. Desktop logic tests cover
presets, parsing, and truthful active/missing/unknown/truncated presentation.

The opt-in `RealPiToolSelectionTests` use an installed Pi 0.85.0 runtime with an
isolated deterministic provider. They generate actual model tool calls without
network inference or paid requests, covering PowerShell execution, failure,
cancellation, approval/denial in all non-Full-access modes, registry restrictions,
planning aliases and dispatch denial, reload/restart, no tools, and child exclusion.
Existing planning, resource, agent, and permission tests also run against Pi 0.84.4
with unchanged defaults. See the tracker evidence log for current results.

Run the real-Pi tests with `PISTATION_RUN_REAL_PI_OFFLINE=1` and
`PISTATION_PI_PATH` pointing to an installed Pi 0.85.0+ package or executable:

```powershell
dotnet test tests/PiStation.PiRpc.Tests/PiStation.PiRpc.Tests.csproj -c Debug --filter FullyQualifiedName~RealPiToolSelectionTests
```

Native acceptance remains **pending**: the native automation pipe could not connect
after retry/reset. On an interactive desktop, manually verify settings keyboard/
screen-reader behavior, narrow/High Contrast layouts, persistence after relaunch,
host-versus-client scope, older-Pi rejection, selected-thread inventory, and the
absence of write controls for read-only remote profiles. Verify shell approval,
denial, Stop, and readable PowerShell output in a disposable project with an
authorized provider. Live-provider, clean-machine, and physical two-PC results
must be recorded separately; offline-provider tests do not certify them.
