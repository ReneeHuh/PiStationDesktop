# Thread read/unread state — Windows / Pi only

The desktop now shows a separate **Unread** label for unseen turn completions. It does not replace running, attention, draft, settled or snoozed indicators.

- Completion and read counters are stored in the local host database and survive restart.
- Existing history starts at zero; importing/opening historical sessions does not light up every old thread.
- A settled Pi turn advances its completion counter before publishing the settled projection. Opening an active-window thread marks only the completion counter present in that displayed projection as read.
- Inactive-window completions stay unread until the window is activated and their completed projection is displayed.
- Thread actions, the right-click menu and multi-selection Bulk menu provide **Mark unread**. Threads without a recorded completion are unchanged.
- Manual unread for the selected thread survives metadata refreshes and automatic reads queued behind it. Switching away and back establishes a new visit; a later displayed completion can also clear it.
- Read commands are idempotent. Older reads cannot clear a newer completion; future counters cannot pre-clear work. Read changes do not modify activity timestamps or settlement state.
- Protocol version 30 adds optional completion counters to descriptors/projections and a read-state command. Both host and client reducers carry the displayed completion counter.

This follows the local T3 checkout's unseen-completion and Mark unread behavior, using monotonic counters instead of local timestamps to avoid clock skew and delayed-read races. PiStation stores the state in its local SQLite database rather than web local storage.

Tests cover restart persistence, empty history, stale/future read requests, idempotence, metadata ordering, streamed completion counters, explicit unread and runtime rehydration. Native acceptance is still needed for focus changes, context/bulk actions, keyboard interaction and large-text layouts.

Validation on September 7: Debug x64 solution and final app builds passed with zero warnings/errors. Across the regression run and isolated retry, 318 distinct tests passed and 6 opt-in real-Pi tests were skipped. One checkpoint integration test timed out during Git baseline capture in the suite, then passed independently in 9 seconds; no read-state assertion failed. Results are in `TestResults/read-state-validation` and `TestResults/read-state-validation-retry`. Visual contract checks passed for 24 states, 4 layouts and 3 text scales, including read-state action/focus wiring checks.
