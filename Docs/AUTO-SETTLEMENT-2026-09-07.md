# Automatic thread settlement

Settings → General now exposes inactivity (default three days, configurable 1–365 or disabled), merged-PR settlement, and closed-PR settlement. Settings are host-owned SQLite records. The host checks at startup and every minute; UI settings changes take effect on the next sweep. Disabling a rule never reopens already settled threads.

## Safety and activity

- Prompt starts, steering/follow-up submissions and turn completions record task activity independently of metadata timestamps. Reading, renaming, pinning and sidebar refresh do not reset this clock.
- Running/starting/stopping/hydrating runtimes, queued messages, pending approvals/questions, active child agents and pending worktree setup block automatic settlement.
- Ready/executing/paused plans retain a revision-guarded protection marker across restart. Unresolved command receipts and interrupted background submissions conservatively block settlement until resolved.
- A recent prompt has a two-minute dispatch grace period. Actual running work remains protected beyond that period.
- Manual un-settle, including bulk un-settle, persists protection until the next prompt/steering/follow-up. Manual and automatic settlement remove pins, but do not delete conversations or worktrees.
- Automatic writes compare the thread revision after network lookups; controller-backed writes also take the runtime lifecycle lock and recheck live work. Concurrent activity/metadata changes skip the stale decision.
- Snoozed threads stay protected until their snooze expires. This is more conservative than T3's early-wake exceptions.

## Pull requests

The worker fetches merged and closed PR lists from existing supported hosting adapters, with a shared per-project lookup and a 15-second lookup budget. Exact provider/number/URL identifies an explicitly linked PR. Without a link, a unique matching source branch may be used, excluding the repository's default branch.

PR settlement requires a provider-supplied close/merge timestamp at or after the last user activity. Later comments/updates on an old closed PR do not settle resumed work. GitHub requests include `closedAt`/`mergedAt`; available GitLab/Azure closure fields are normalized too. Missing timestamps, ambiguous branches, absent PRs, unsupported adapters and lookup failures never cause PR-based settlement. Inactivity can still apply independently. Explicit links are refreshed to the confirmed terminal state when settling.

## Boundaries and acceptance

- Unlike T3's independently hosted server, the embedded host stops when PiStation closes.
- Existing PR-list limits apply (currently up to 100 per state for GitHub). Older PRs missing from these lists remain unknown; this is not full paginated PR discovery.
- Legacy threads without dedicated activity records are not assigned guessed ages. They enter these rules after new task activity; old metadata dates are not treated as task activity.
- No immediate merge-event sweep is added: the periodic worker detects external or local merges on its next check.
- Native Settings/sidebar click-through and authenticated provider acceptance remain pending.

Tests cover policy boundaries, old/open/merged/closed PRs, missing closure timestamps, runtime/queue/snooze/override protection, persisted plan protection, stale-write rejection, host-only sweeps, settings round-trip/restart and running background-task exclusion. Source-level UI checks do not replace native acceptance.

Verification: 336 tests passed (host 129, client 108, protocol 29, Pi RPC 66, command system 4); six opt-in real-Pi tests skipped. The checkpoint integration test exhausted its existing shared 20-second deadline during full-suite runs, then passed separately; the other 128 host tests passed together. Reports: `TestResults/settlement-verified`; the final focused policy rerun also passed all 11 cases in `TestResults/settlement-policy-final`. The visual contract passed.
