# Distribution setup — TODO later

Deferred at the owner's request on September 5, 2026.

- [ ] Choose the production publisher distinguished name and display name.
- [ ] Obtain/configure a matching signing certificate and a signing process that keeps private keys outside the repository.
- [ ] Choose the HTTPS App Installer feed URL and artifact hosting location.
- [ ] Run `Build-Release.ps1` with the chosen publisher, version, and feed URL; sign the generated MSIX and publish both artifacts.
- [ ] Validate first installation and an upgrade on another Windows 11 x64 machine, including retention of projects, threads, drafts, attachments, stashes, settings, and Pi sessions.
- [ ] Test the app's update check and App Installer prompt against that feed.

`Build-Release.ps1` currently produces an **unsigned development package**. It accepts publisher/version/feed parameters without modifying the source manifest. The app explicitly reports when it was installed without an App Installer feed. A production package with a different publisher has a different Windows package identity; plan migration from development installations before distribution.

Do not describe public release, signing, or automatic updates as complete until these items pass.
