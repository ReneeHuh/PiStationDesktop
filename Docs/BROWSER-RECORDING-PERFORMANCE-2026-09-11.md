# Recording pacing and fresh-frame accounting

WEB-09 remains Partial. This change fixes encoder scheduling, unnecessary decoding and overstated fresh-frame reporting; sustained native 30/60 FPS across viewports and background/resource workloads is still not certified. Wire protocol remains 60: the additional recording diagnostics are fields in the existing JSON result.

## Changes

The WebView callback starts its screencast acknowledgement before copying the frame string and publishes only the latest compressed frame. Base64 conversion and JPEG pixel decoding happen when the encoder consumes that frame. Overwritten frames are not decoded, and repeated samples reuse an immutable decoded pixel buffer. The pending source buffer and its notification remain bounded; no per-frame task or queue is added.

Output uses a per-recording Windows high-resolution waitable timer and deadlines anchored to the recording clock. It does not busy-wait or change the global timer period. The clock begins when the media source starts, excluding encoder preparation. When a source frame arrives just after an output deadline, the encoder can wait up to one frame interval for it instead of immediately emitting a repeat. Sample timestamps preserve elapsed time, including missed deadlines; slower capture is not stretched into slow-motion video. Static pages can have fewer output samples and a low fresh-frame rate while remaining playable.

`freshFrames` counts distinct received frame instances actually included in output. It does not compare pixel content: two separately received frames can look identical. `repeatedFrames` counts reuse of an already included instance, `droppedSourceFrames` counts received instances not included (including overwritten startup frames), and `decodeMilliseconds` measures cumulative base64/stream/pixel decoding time. Effective FPS is now `freshFrames / durationSeconds`. The previous `min(received, encoded) / duration` estimate could overstate freshness after bursts or drops; historical native estimates of approximately 21/28 FPS are not directly comparable to the corrected measurement.

The existing two-minute, 1280-pixel-edge, 4 MiB compressed-frame and 64 MiB output limits remain. Ownership, cancellation, MP4 delivery and access deadlines remain unchanged. Codec queues and total process memory still need native workload qualification; bounded input storage does not establish a total process-memory bound.

## T3 and platform reference

The local T3 [recording implementation](../../t3code/apps/web/src/browser/browserRecording.ts) requests a tab media stream with a maximum frame rate and encodes it with MediaRecorder. Its [desktop preview manager](../../t3code/apps/desktop/src/preview/Manager.ts) owns capture of the selected tab. That supports separating requested FPS from delivered frames, but the Electron tab-stream API is not a drop-in WebView2 backend. PiStation retains its tab-scoped CDP/Windows encoder path.

Microsoft documents [high-resolution waitable timers](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw) and [relative one-shot timer deadlines](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-setwaitabletimer). Chromium documents [screencast parameters and acknowledgement](https://chromedevtools.github.io/devtools-protocol/tot/Page/#method-startScreencast); that API does not promise sustained requested FPS. No newer experimental Chromium recording method is assumed to exist in the installed WebView2 runtime.

## Verification

The unchanged encoder baseline passed its three existing tests ([TRX](../TestResults/recording-performance-baseline/baseline.trx)). The new real Windows encoder tests cover playable MP4s at both requested rates, moving JPEG inputs at 1280×720 and 1280×1280, discarded/repeated-frame accounting, malformed-frame cleanup and timer cancellation/reuse. A burst of 500 source frames before encoding must report exactly one fresh encoded frame, rather than treating later repeats as fresh frames.

Intermediate measurements and first attempts are retained under ignored TestResults: `recording-performance-encoder`, `recording-performance-timer` and `recording-performance-phase`. The phase-aware run passed ten tests and measured 28.55/59.99 fresh FPS at 1280×720 and 29.27/48.66 at 1280×1280 for 30/60 requests. These are three-second synthetic-input encoder measurements on this machine, not native WebView performance or a sustained-rate acceptance pass. Process CPU and working set are recorded in test output; they include the test host and fixture work and are not isolated app resource measurements.

The [integrated gate](../TestResults/code-Debug-20260911-041900-09092b63/summary.json) built the solution/WinUI app with zero warnings/errors and passed **14 tests, zero failures/skips**: ten encoder checks, three local/HTTPS/SSH artifact-transfer cases and one host ownership/bounds/disconnect case. Its synthetic encoder results were 29.60/59.50 FPS at 1280×720 and 20.72/54.85 at 1280×1280. In the slower 30 FPS square case, the fixture itself supplied only 64 frames in three seconds, so that result did not isolate encoder capacity. The fixture was subsequently changed from millisecond Task.Delay polling to an independent high-resolution source clock with one wake per source frame; that improves the measurement fixture, not the product result.

The final [source-clock encoder rerun](../TestResults/recording-performance-source-clock/encoder.trx) passed **10 tests, zero failures/skips** after rebuilding the test project. No production source changed after the integrated gate. Final three-second measurements:

| Dimensions | Requested FPS | Received frames | Fresh encoded frames | Effective fresh FPS |
| --- | --- | --- | --- | --- |
| 1280×720 | 30 | 90 | 90 | 29.99 |
| 1280×720 | 60 | 180 | 180 | 59.91 |
| 1280×1280 | 30 | 86 | 84 | 27.99 |
| 1280×1280 | 60 | 170 | 151 | 50.31 |

The large-viewport results still fall short and the producer itself misses some deadlines under load. These figures substantiate the encoder improvements and remaining limits, not an end-to-end before/after comparison with historical WebView recordings.

Native Computer Use failed with `failed to connect native pipe ... (os error 2)` on the initial check, retry and reset/reinitialization check. No native pass is claimed. The existing browser acceptance script now uses requestAnimationFrame moving content, records paint counts, visibility and precise frame accounting, and supports a longer observation interval with an explicit minimum fresh-rate threshold:

```powershell
./tests/PiStation.UiTests/Invoke-BrowserAutomationSlice.ps1 -FrameRate 30 -RecordingDurationSeconds 15 -MinimumFreshFrameRatio 0.95
./tests/PiStation.UiTests/Invoke-BrowserAutomationSlice.ps1 -FrameRate 60 -RecordingDurationSeconds 15 -MinimumFreshFrameRatio 0.95
```

Without the threshold argument, this remains a functional journey, not a performance acceptance gate. Its PowerShell syntax was checked; the modified native journey has not been executed. These commands exercise the existing background viewport; additional visible/large viewport and loaded-system runs remain necessary before closing WEB-09.
