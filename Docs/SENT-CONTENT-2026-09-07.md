# Sent attachments and citations — Windows / Pi only

Implemented:

- Retain host-owned attachment identities, paths, hashes and source citation metadata independently of the composer draft.
- Store metadata before dispatch; clearing an accepted draft no longer deletes its sent files. Deleting a thread/project releases files only when no other draft, stash or sent record references them.
- Include an opaque message reference in Pi's persisted prompt. Resolve it only against host-owned records for that thread, then remove it from display text and queue previews. Ordinary prompts without attachments/citations are unchanged.
- Restore rich transcript content after host/app restart and checkpoint hydration. Queued content appears in the transcript when Pi reports delivery.
- Offer image preview with zoom, explicit confirmed opening through Windows, Save as and Copy path. Verify file length/hash before access; report missing or changed files.
- Show expandable saved citation text with Open source. File citations reuse workspace/line navigation. Response citations use their original ID, with a full-response fingerprint fallback after Pi replaces live IDs on hydration. Missing or ambiguous sources retain their quote rather than guessing a destination.
- Protocol version 29; new optional message content and citation fingerprint fields preserve legacy JSON readability.

Validation covers persistence, stale draft rejection, serialization, source resolution, file integrity and a real embedded-host/Fake Pi send → clear → restart → delete round trip. The solution build and automated tests are run separately from native UI acceptance.

Verified on September 7: Debug x64 solution build passed with zero warnings/errors; 315 distinct automated tests passed, with 6 opt-in real-Pi tests skipped. The visual contract passed for 24 states, 4 responsive layouts and 3 text scales, including source checks for the new transcript actions. Results are under `TestResults/sent-content-validation` and `TestResults/sent-content-validation-final`.

Limits:

- Existing files already deleted by earlier versions cannot be recovered; old messages remain readable text.
- Failed/uncertain dispatch and cleared queues conservatively retain content until thread/project deletion. This avoids deleting a file that Pi may have received.
- Session JSONL exports/imports are not portable attachment bundles; image/video transcoding and HEIC conversion are not implemented here.
- Native Windows picker, external file launch, zoom/keyboard interaction and large-text layout still require interactive acceptance testing.
