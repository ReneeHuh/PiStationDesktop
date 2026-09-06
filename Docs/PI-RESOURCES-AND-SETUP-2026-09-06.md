# Pi resources and provider setup

September 6, 2026. Follow-up to the [first Pi integration milestone](PI-INTEGRATION-MILESTONE-2026-09-05.md), implemented on base `a3dca808645de8e6fcbbf18adde45bf45569f29c`.

## Delivered behavior

**Settings → Pi resources** now exposes the selected idle thread's effective resource paths and sources, saved resource settings, project trust, provider credential-source status, custom-model setup and startup/configuration messages.

- Enable or disable local and packaged extensions, skills and prompt templates. Settings use Pi's own scoped resource patterns/package filters. Explicit extension paths remain under Pi / runtime. Refreshing the inventory does not install packages.
- Inspect both effective project trust and the saved folder decision. Trust changes use Pi's trust store and apply on restart, subject to Pi's trust extensions. Repository script trust remains separate.
- Keep saved state distinct from the active runtime. A resource is confirmed when Pi reports a registered feature or effective skill/context file. Extensions that register no visible feature may load without confirmation.
- Open Pi login/logout, resource configuration and package management in an owned integrated terminal using the configured runtime. Credentials are handled through Pi. The status panel requests credential metadata, not API-key values.
- Add or update custom provider endpoints and models. Existing provider/model fields survive; comments/formatting are rewritten. Credentials can reference an environment-variable name or use keyless localhost configuration. Invalid input, malformed JSON and stale revisions do not overwrite configuration.
- Preserve drafts and transcripts through configuration actions and explicit restart. Management is idle-only, serialized across threads, uses a correlated extension response and creates no model turn or chat message. Process exit interrupts outstanding management requests. A timeout directs the user to refresh before retrying a potentially saved change.
- Recognize the npm `pi.ps1` launcher as well as `pi.cmd` when selecting a Pi installation.

The application protocol is **v24**. The bundled `pistation-resources.ts` extension uses Pi's public SDK; the desktop command is hidden from composer discovery. Its responses travel through a reserved, intercepted Pi status message. The app does not persist those management messages in the conversation.

Implementation: [Pi extension](../src/PiStation.App/PiExtensions/pistation-resources.ts), [RPC correlation](../src/PiStation.PiRpc/Transport/PiManagement.cs), [host controller](../src/PiStation.Host/Threads/PiThreadController.Resources.cs), [view model](../src/PiStation.App/ViewModels/ShellViewModel.Resources.cs), [usage guide](INSTALL-AND-RECOVERY.md#manage-pi-resources-and-providers).

## Validation

Debug and Release builds passed with zero warnings/errors. Debug and Release code suites each passed **226 tests**, zero failures: Protocol 25, PiRpc 59, Host 94, ClientRuntime 44, CommandSystem 4. Evidence: `TestResults/pi-resources-debug/*-final.trx` and `TestResults/pi-resources-release/*-final.trx`.

- Actual Pi **0.84.4** and an isolated **0.85.0** installation: offline skill/template/tool/extension/resume coverage and native-SDK resource management checks. Resource checks include local/package skill toggles, stale revisions, project-local skill trust, model/credential field preservation, malformed configuration, invalid credential input and restart. They use isolated temporary configuration and an offline provider. Final evidence: `TestResults/pi-resources-debug/real-pi-084-final.trx` (two real-Pi checks plus six locator cases) and `real-pi-085-final.trx` (two real-Pi checks).
- Authenticated smoke: the existing configured `opencode-go / gpt-5.6-luna` account completed a skill-guided model request and a second request after session restart on both Pi versions. Each test used an empty temporary project and disabled tools and discovered project resources. Evidence: `TestResults/pi-resources-debug/authenticated-setup.trx` and `authenticated-085.trx`. This validates an existing account; a fresh login/OAuth onboarding journey was not performed.
- Native functional workflow: FakePi drives resource and trust toggles, model form input, explicit idle restart, app relaunch and guided terminal creation. The unsent draft survives. The runner owns its app process and isolated data. Final run: `TestResults/pi-resources-native/9e5fae128507464a9f9be9ab1107e701/`. All five functional checks passed, and its screenshot passed the blank-image validator and was visually inspected. This establishes the captured Settings state; broader physical DPI, keyboard and accessibility acceptance remains separate. The PR runner now invokes this slice with required screenshot validation.
- Static visual contract passed for 24 states, four responsive layouts and three text scales. This is not physical DPI, High Contrast or screen-reader acceptance.

The early host assertion compared deserialized collection identity; it was corrected to compare durable draft identity/revision and content. Early UI assertions were corrected to inspect the accessibility tree's decoded names. The installed `.ps1` run exposed and fixed launcher discovery. Final results refer to successful reruns; failed attempts remain in ignored test artifacts where retained.

## Reproduce

```powershell
dotnet build PiStationDesktop.slnx -c Debug -p:Platform=x64
# Regular suites exclude opt-in provider/offline categories.
dotnet test tests/PiStation.PiRpc.Tests/PiStation.PiRpc.Tests.csproj --filter 'Category!=RealPi&Category!=RealPiOffline'
dotnet test tests/PiStation.Host.Tests/PiStation.Host.Tests.csproj

$env:PISTATION_RUN_REAL_PI_OFFLINE = '1'
$env:PISTATION_PI_PATH = 'C:/path/to/pi.ps1'
dotnet test tests/PiStation.PiRpc.Tests/PiStation.PiRpc.Tests.csproj --filter 'Category=RealPiOffline'

# Uses the provider credentials already configured in Pi; sends two small requests.
$env:PISTATION_RUN_AUTHENTICATED_SETUP = '1'
$env:PISTATION_TEST_PROVIDER = 'your-provider'
$env:PISTATION_TEST_MODEL = 'your-model'
dotnet test tests/PiStation.PiRpc.Tests/PiStation.PiRpc.Tests.csproj --filter 'FullyQualifiedName~RealPiAuthenticatedSetupTests'

pwsh tests/PiStation.UiTests/Invoke-PiResourcesSlice.ps1 -NoBuild -Capture
```

## Remaining work

PI-04 now has its core native workflow. Exact load/failure attribution for extensions without registered features and broader package/trust-extension acceptance remain. PI-05 remains partial: general runtime arguments/environment editing, automated runtime installation/update and clean-machine onboarding are still pending. One provider smoke test does not validate every provider or subscription state.

The next local milestone is Pi session import/fork/export, followed by plan/subagent workflows and PR review depth. The [48-item audit](FEATURE-COMPLETION-AUDIT-2026-09-05.md) still tracks the larger T3 parity backlog. This change does not upgrade the user's Pi installation, publish a release or complete full parity.
