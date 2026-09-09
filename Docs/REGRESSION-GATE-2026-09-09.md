# Controlled code-test gate and BUG-09

BUG-09 records intermittent startup/subscription/recovery failures during concurrent
build/test activity. The earlier observed 100% CPU is not a proven root cause.
The current investigation's unchanged baseline passed both complete Host and
ClientRuntime suites while they overlapped: **336 passed / 1 opt-in skip** and
**415 passed / 0 skips**, respectively. A later full gate still reproduced one
client startup-readiness timeout; its cause remains unproven. Original and current
failing evidence remain in [tracking.md](../tracking.md).

## Run the gate

```powershell
# Build once, then run all discovered PiStation.*.Tests projects, one at a time.
pwsh ./Invoke-CodeTests.ps1

# A current build already exists.
pwsh ./Invoke-CodeTests.ps1 -NoBuild

# Focused investigation; not a complete-suite result.
pwsh ./Invoke-CodeTests.ps1 -NoBuild -Suite Host -Filter 'FullyQualifiedName~PiResources'

# Exercise the gate's own failure/report/locking contracts without running dotnet.
pwsh ./tests/Test-CodeTestGate.ps1
```

`-Configuration Debug|Release` selects the build. Optional `-Suite` selects project
name portions such as `Host`, `ClientRuntime`, `PiRpc`, `Protocol`, or `CommandSystem`;
comma-separated CLI selections such as `-Suite Host,ClientRuntime` are supported.
All discovered code-test projects run by default, including future matching projects.
Each must be in the solution with an explicit platform mapping. Test commands reuse
that mapping (notably x64 for desktop logic), so `--no-build` cannot silently select
a different platform's stale output directory.
Unknown suite names and filters that execute no tests fail instead of reporting success.

The code and pull-request entry points hold an exclusive checkout-local file handle
through their build/test work. A competing gate fails immediately, without starting
a build or test. The lock file stays under ignored `TestResults`; do not delete it
to bypass a live run. Closing/crashing the owner releases the handle. This does not
intercept direct `dotnet` commands, IDE builds, independent UI runners, another
checkout, or unrelated applications. Avoid those overlaps for baseline certification;
record deliberate contention experiments separately.

The shared [runsettings](../tests/CodeTests.runsettings) select one VSTest testhost
and two conservative xUnit test collections. Concurrency inside each test—multiple
clients, child processes, races, cancellation, and recovery—is unchanged. The runner
sets `DOTNET_PROCESSOR_COUNT=2` only on the testhost and its children, not on the
user's shell, installed app, or build. These are test workload bounds, not a claim
that the application supports only two processors. See the primary
[xUnit RunSettings reference](https://xunit.net/docs/config-runsettings).

Long-running tests are reported after 30 seconds. The outer VSTest hang cutoff is
three minutes by default; it terminates a hung testhost/children and retains failure
evidence without a memory dump. It does not change the tests' cancellation budgets,
Pi startup deadlines, connection retries, or browser permission/heartbeat lifetimes.
For an explicitly authorized long-running opt-in, choose and record
`-HangTimeoutMinutes 15` (range 1–30); a longer cutoff is not a product fix. See the
[VSTest hang and environment options](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-vstest).

## Evidence and failure behavior

Each invocation creates a unique `TestResults/code-<configuration>-<time>-<id>`
directory with one TRX per selected project and a progressively saved `summary.json`.
The summary starts unsuccessful, records configured bounds and explicit skips, and
only passes after every selected suite reports successful executed tests. A nonzero
exit, missing/unreadable TRX, aborted/incomplete report, failed test, or empty run
fails the gate. Other suites continue after reported test failures so their evidence
is not omitted. A build/launch failure stops the run. There are **no automatic retries**;
manual reruns create new evidence and never overwrite the failed attempt.

The 14 standalone runner contracts use synthetic TRX/command fixtures under
`TestResults/code-gate-contract`; those are runner tests, not application acceptance.
They verify project discovery, solution/platform mapping, suite/configuration/filter selection, build order,
no-build behavior, required bounds, actual cross-process lock exclusion/release,
failure propagation, incomplete/missing reports, zero-test rejection, explicit skips,
and preservation of the caller's environment.

The Debug PR and Release code-test CI paths use this runner. The full PR path also
runs its contract checks, then continues the existing native UI journeys. Running
the code-only gate does **not** certify the native journeys, live providers,
clean-machine setup, two physical PCs, or loaded-system behavior. Opt-in tests retain
their existing opt-in requirements; the runner does not enable paid/network tests.
Some historical real-Pi tests return without execution when not opted in, so a passed
TRX count alone must not be presented as real-provider qualification.

BUG-09 remains an investigation with a regression-harness mitigation, not a claimed
production timing fix. The latest complete Debug gate, with local HEIF and Pi 0.85.0
offline tests enabled, finished **966 passed / 1 failed / 2 skipped**:

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| ClientRuntime | 414 | 1 | 0 |
| CommandSystem (x64) | 66 | 0 | 0 |
| Host | 336 | 0 | 1 |
| PiRpc | 91 | 0 | 1 |
| Protocol | 59 | 0 | 0 |

Evidence is retained in `TestResults/code-Debug-20260909-153530-d6aff240`.
All 17 opt-in real-Pi offline tests passed; live-provider/native acceptance was not
run. `ClientSendsDraftAttachmentsClearsOnAcceptanceAndHydratesAPathFreeTranscript`
timed out waiting for its initial `Ready` projection, before saving or uploading
attachments. Its unchanged isolated rerun passed in seven seconds
(`TestResults/code-Debug-20260909-155108-8a8d1865`); that does not clear the failed gate.
Visual Studio-owned MSBuild workers and 100% CPU load were observed around the
failure. This is correlation, not a root-cause finding. IDE builds were allowed to
continue at the user's request; no user processes were stopped. A loaded-system
startup/subscription investigation and a clean full-gate result remain outstanding.
