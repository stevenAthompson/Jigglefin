# Local folder client

This is Jigglefin's bundled, offline web UI, not a fork of the full Jellyfin Web dashboard.
It uses standard Jellyfin authentication, live folder/item DTOs, playback negotiation,
stream/subtitle endpoints and playback reports. `Jigglefin/Cache/Clear` is the one small
Jigglefin-specific administrative operation. Native Jellyfin clients do not need it.

## Build and test

Use Node 24 or later:

```powershell
cd Jigglefin.Web
npm ci --ignore-scripts --no-audit --no-fund
npm run check
npm run build
cd ..
npm ci --prefix tests/WebClientSmoke --ignore-scripts --no-audit --no-fund
tests/WebClientSmoke/node_modules/.bin/playwright.cmd install chromium
node tests/WebClientSmoke/live-folders.cjs
```

The last command requires a Debug server build and prepared Jellyfin FFmpeg in
`publish/ffmpeg-prepared-test`. Override with `JIGGLEFIN_TEST_SERVER` (DLL),
`JIGGLEFIN_TEST_DOTNET` and `JIGGLEFIN_TEST_FFMPEG` if necessary. The test always uses its
own temporary profile, generated media, random loopback port and headless browser.
It does not use an installed server or real library. Temporary fixtures/screenshots are
retained for diagnosis, and no passwords are stored in source.

To test the actual portable package, including its Windows PowerShell 5.1 launcher:

```powershell
scripts/package-win.ps1 -FfmpegDirectory publish/ffmpeg-prepared-test -OutputDirectory publish/MyNewTestBuild
scripts/smoke-live-package-win.ps1 -PackageDirectory publish/MyNewTestBuild
```

The packager accepts only a verified folder-client manifest and copies only its listed
assets, never a stale full Jellyfin Web build. hls.js and its license are bundled locally.
There are no CDNs, analytics, server discovery, casting, service workers or remote fonts.
Server response headers and an HTML policy restrict resource requests to this server.
The test records attempted external browser requests before blocking them, and fails on
any attempt. This is browser evidence, not proof of the complete server/helper offline audit.

## Behavior

- Setup/account login; configured folder groups; read-only immediate directory listings.
- Local filename filtering and sorting, selection-time NFO/artwork, favorites.
- Direct browser playback and local HLS conversion, local text subtitles, audio-track choice,
  playback speed, seek controls, and durable resume after cache clear and restart.
- Accounts with explicit folder access; new accounts start with no accessible media.
- Only administrative users can add/remove roots, change folder permissions or clear caches.

No recursive search, catalog categories, metadata editing, scanning, plugin management or
internet settings are offered. Unknown files are visible but not executable/playable. The
browser is not a document editor. Native-app UI compatibility and legacy-profile migration
are separate release gates; see `../JIGGLEFIN-LIVE-DESIGN.md`.
