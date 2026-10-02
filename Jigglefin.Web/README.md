# Local folder client

This is Jigglefin's bundled, offline web UI, not a fork of the full Jellyfin Web dashboard.
It uses standard Jellyfin authentication, live folder/item DTOs, playback negotiation,
stream/subtitle endpoints and playback reports. Cache clearing and enabling/disabling a
folder group, dismissing Continue, resolving local playlist files and explicitly building
bounded folder queues are small Jigglefin-specific operations. Native Jellyfin
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
- Settings offers three complete, bundled themes: Night, Day and High contrast. The
  selection is saved only in this browser and also covers sign-in, browsing, settings
  and playback. No remote stylesheet, server preference or metadata is involved.
- Folders, Continue and Favorites navigation. Star any file/folder to keep a shortcut;
  a file shortcut opens its parent and selects it. Continue supports individual removal
  and Clear; dismissal keeps the bookmark/favorite and playback adds it again.
- Filename filtering; playlist/name, reverse name, modified date (newest/oldest),
  size (largest/smallest) and type sorting. Folder sizes are not recursively calculated.
- Folder Play/Shuffle; selected-file Play plays that file, while Play from here queues
  the displayed order starting at the selection. Next/Previous, pause, Stop, ±30 seconds,
  shuffle, repeat-track/repeat-queue and a clickable queue are available. Transport
  controls use bundled SVG icons with tooltips and accessible names.
- Each folder row has Play Folder, including folder favorites and configured groups.
  This explicit action includes subfolders without navigating away. It plays direct
  files first, then visits child folders in natural name order. The selected sort
  applies to files within each folder; Playlist uses the first alphabetical local
  playlist, preserves duplicates, and appends unlisted files without adding referenced
  child tracks twice. Cancel abandons a pending queue without stopping current playback.
- All queues are capped at 500 tracks. Recursive Play Folder also caps directory reads
  at 100, raw entries at 10,000 (including non-media), and depth at 16 below the selected
  folder. Enumeration actually stops at the entry budget. A notice explains partial
  queues; open a smaller subfolder for the rest. A truncated directory is sorted only
  within the entries read. Cancellation is checked between filesystem operations;
  it cannot interrupt an operating-system call stalled on an unavailable share.
- Direct browser playback and local HLS conversion, local text subtitles, audio-track choice,
  playback speed, seek controls, and durable resume after cache clear and restart.
- Add a playable file or local playlist to the end of the current queue from its row;
  selected items also offer Play next and Add to queue. If nothing is playing, Add to
  queue starts playback. The existing 500-track cap still applies, and duplicate
  playlist references remain intentional. This queue stays in the browser session.
- Sleep after the current track or after 15, 30 or 60 minutes. Expiry stops playback
  through the same bookmark-saving path as Stop, even when repeat is enabled. The
  timer is session-local; closing the browser cancels it, and manually stopping
  playback clears it. Browser background throttling can delay a wall-clock timer.
- Keyboard shortcuts while the player is active: Space/K pause or play, left/right
  arrows skip 30 seconds, and N/P move to the next or previous track. Shortcuts
  ignore text inputs, menus, dialogs, modified key combinations and held-key repeats.
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
explicitly referenced child-directory tracks (shown in Queue). Normal browsing and the
in-folder Play/Shuffle controls do not recursively discover media; the separate Play
Folder row action is the bounded, explicit exception described above. Choosing another
sort uses only displayed playable files in that order for the in-folder controls.
Selecting a playlist and pressing Play plays just its entries, not unlisted files.

Only UTF-8 relative local paths are accepted. URLs, absolute paths, parent (`..`)
references, links, missing files and non-audio/video entries are ignored. Playlists
cannot contact an online service. Reads are bounded to 1 MiB, 2,000 entries, 16 path
components and 128 explicitly named directories; media is probed only when played.
No playlist or media file is changed. The current queue lives in the browser session;
bookmarks and favorites are durable per-account server state.
