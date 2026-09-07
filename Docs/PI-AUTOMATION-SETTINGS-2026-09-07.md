# PI-07: persistent Pi automation preferences

PiStation now stores nullable auto-compaction and auto-retry overrides in the host database, with revision-checked saves. Defaults are unmanaged: opening PiStation does not issue automation setters.

## Scope and behavior

- Preferences are host-wide, shared by parent threads (including independent background tasks and forks), not per-thread. Pi's native setters persist to its shared settings directory; other Pi clients using that directory can therefore be affected.
- Preferences apply during process initialization and before a new parent turn. Saving does not change active turns. Settings also offers explicit application to the selected idle runtime.
- Clearing Manage releases PiStation control; it does not restore an earlier Pi value. Child-agent sessions retain their own preset, in-memory automation policy.
- Settings shows the saved revision and last applied revision separately. Auto-compaction is verified only from an explicitly reported state field. Auto-retry is acknowledged by the setter response, not claimed to be verified by `get_state`, which does not expose it.
- Application failures retain the saved preferences but clear application verification. Partial native changes may have persisted. Prompt dispatch is blocked before creating a turn or consuming its draft. Retry after fixing the runtime/settings issue, or release the override.
- Status is the last application observation, not a live poll of external settings edits. Recreated runtimes reapply saved overrides. Protocol version is 34.

## Verification

Integration coverage includes all four boolean combinations, two threads, host restart, idle process restart, revision conflicts, saving during an active turn, native command rejection, preservation of an unsent draft, releasing control after partial application, and an unreported state field.

The opt-in real-Pi test exercises both native setters and verifies isolated settings-file persistence across process restart without invoking a model or accessing user credentials. UI source contracts check the automation controls. Native Windows settings interaction/visual acceptance remains a manual follow-up.

Verification results: 115 client, 128 host, 29 protocol, 66 Pi RPC and 4 command tests passed (342 total). Seven opt-in real-Pi tests were skipped in the default suite; the automation test was separately enabled and passed. The existing `SettledTurnCapturesCheckpointAndConfirmedCommandRewindsWorkspaceAndConversation` test was run separately and exceeded its 20-second cancellation budget waiting on Git. This is not a clean full-suite pass; the checkpoint timing issue remains open.
