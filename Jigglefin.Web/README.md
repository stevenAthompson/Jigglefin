# Local folder client

This is Jigglefin's bundled, offline web UI, not a fork of the full Jellyfin Web dashboard.
It uses standard Jellyfin authentication, live folder/item DTOs, playback negotiation,
stream/subtitle endpoints and playback reports. Cache clearing and enabling/disabling a
folder group, dismissing Continue and resolving local playlist files are small Jigglefin-specific operations. Native Jellyfin
clients do not need them.

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

- Setup includes a server-side folder picker; no path typing is required. The picker
  lists drive letters without probing disconnected drives, and opens only your chosen directory.
- Folders, Continue and Favorites navigation. Star any file/folder to keep a shortcut;
  a file shortcut opens its parent and selects it. Continue supports individual removal
  and Clear; dismissal keeps the bookmark/favorite and playback adds it again.
- Filename filtering; playlist/name, reverse name, modified date (newest/oldest),
  size (largest/smallest) and type sorting. Folder sizes are not recursively calculated.
- Folder Play/Shuffle; selected-file Play plays that file, while Play from here queues
  the displayed order starting at the selection. Next/Previous, pause, Stop, ±30 seconds,
  shuffle, repeat-track/repeat-queue and a clickable queue are available.
- Direct browser playback and local HLS conversion, local text subtitles, audio-track choice,
  playback speed, seek controls, and durable resume after cache clear and restart.
- Convert requests a browser-friendly stream from this server (local conversion can use
  more CPU). Direct returns to normal negotiation. Stop automatically saves your position.
- Accounts with explicit folder access; new accounts start with no accessible media.
- Only administrative users can add/remove/disable roots, change folder permissions or clear caches.

No recursive search, catalog categories, metadata editing, scanning, plugin management or
internet settings are offered. Unknown files are visible but not executable/playable. The
browser is not a document editor. Native-app UI compatibility and legacy-profile migration
are separate release gates; see `../JIGGLEFIN-LIVE-DESIGN.md`.

## Local playlists

Opening a folder with M3U, M3U8 or PLS files loads the first alphabetically; a selector
lets you choose another. Playlist sort shows referenced immediate files in playlist
order, followed by unlisted files. The playback queue preserves duplicate tracks and
explicitly referenced child-directory tracks (shown in Queue); it never recursively
discovers media. Choosing another sort uses only displayed playable files in that order.
Selecting a playlist and pressing Play plays just its entries, not unlisted files.

Only UTF-8 relative local paths are accepted. URLs, absolute paths, parent (`..`)
references, links, missing files and non-audio/video entries are ignored. Playlists
cannot contact an online service. Reads are bounded to 1 MiB, 2,000 entries, 16 path
components and 128 explicitly named directories; media is probed only when played.
No playlist or media file is changed. The current queue lives in the browser session;
bookmarks and favorites are durable per-account server state.
