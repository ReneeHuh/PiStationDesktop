# Increment 0 driver spike

- WinApp CLI: `0.6.1`
- Official WinUI template pack: `0.0.6-alpha`
- Target: packaged x64 WinUI 3 app on `net10.0-windows10.0.26100.0`
- Result: `winapp run` returned the launched `PiStationDesktop` PID.
- Result: `winapp ui wait-for AppMainWindow` resolved the stable Automation ID.
- Result: all visible shell IDs were present, and the app-owned Add Project dialog exposed
  `ProjectPathInput` and `AddProjectConfirmButton`.
- Result: no coordinate clicks or fixed sleeps were required.

Reproduce with:

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-DriverContract.ps1
```
