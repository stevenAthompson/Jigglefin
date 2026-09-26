# Live-folder regression contract

The scan-backed test implementation is not the specification for Jigglefin. These
tests exercise the contract in `JIGGLEFIN-LIVE-DESIGN.md`: immediate filesystem
membership, selection-time metadata, playback-time probing and separate durable
user state. They use only isolated synthetic media/profile directories. Never point
a destructive test fixture at a user's media tree or installed profile.

## Coverage retained when replacing catalog tests

| Test family | Physical cases and assertions |
| --- | --- |
| `FolderFirstLibraryTests` | Every configured collection label navigates the same mixed corpus: loose files, same-name file/folder pairs, multiple films, season and bonus folders, album extras, booklets and chapters, nested photos, DVD `VIDEO_TS` files/disc stacks, unknown formats, Unicode and empty directories. Exact immediate path sets, parent IDs, physical types, filename order and paging; no folder-to-media collapse. |
| Multi-root/authorization cases in that family | Same-named files stay separate and actually stream through native helpers. Offline mounts remain visible without hiding usable roots; recovery needs no scan. Unauthorized known IDs cannot browse/select/read artwork/negotiate/play or access another group's files through search. |
| `FolderFirstRescanTests` | Despite the historical name, no scans are called. Removing/readding the final file leaves the empty folder and restores stable path identity. Actual file/folder renames and deletion immediately change navigation/stream availability; stale bookmarks persist privately without becoming stale resume results. |
| `LocalSidecarLibraryTests` | File/folder NFO, Emby XML and OPF; supported field projection; precedence; ambiguous shared metadata; editing/removal/cache eviction; no bookmark import from XML; malformed, external-entity, unknown-root and oversized sidecars; local artwork/photo routes. Folder decoration never changes identity or children. |
| `LiveFolderQueryTests` | Nested resume scopes, same-prefix sibling exclusion, missing/completed/non-playable suppression, name/type/media filtering, paging and user-data options; favorite/played/liked/resumable filters and saved-state sorting on immediate listings. Locked media and no resume enumeration. |
| `FolderPlaybackTests` / `LiveUserDataTests` | Per-account single/all Continue dismissal preserves bookmarks/favorites across store restart; only playback start clears dismissal. Global file/folder favorite shortcuts do not enumerate directories or read NFO/media and check access before stats. M3U/M3U8/PLS order/duplicates, named child references, ignored remote/absolute/parent paths, media read locks, bounded file/entry counts, authorization-before-access, logical-drive and immediate-directory picker responses. |
| `LiveFolderApiFilterTests` | Strict mocks prove authorization and empty catalog fallbacks do no filesystem work. Active-session resume exclusion is per user and occurs before path access. |
| Native-client source-identity regressions | A playable physical file has an unprobed source ID with empty streams before playback, including listings/resume queries and cache eviction. Locked-media and recording-reader assertions still prove no content access or recursive reads. Selected details may reuse an existing probe; `PlaybackInfo` alone does the real negotiation. Android TV receives authenticated static-byte URLs without making anonymous streams accessible. |
| Playback track-choice regressions | Explicit audio/subtitle choices apply to a live item's single source even when the client omits `MediaSourceId`. Subtitles Off stays Off and compatible video remains direct-playable. Ordinary and long-path audio/video cases use actual FFmpeg and authenticated HTTP playback. |
| `LivePathLeaseTests`, `LiveReadResultTests` and live-reader/service regressions | Actual Windows handles reject junction/device/ADS escapes, prevent ancestor/file replacement while reading and release on failure or stream disposal. Ordinary paths beyond 300 characters remain readable without accepting device aliases. Directory enumeration and the selected probe retain their protection. Sibling creation still works; locks never trigger a scan. |
| `LiveNativeReadLeaseTests` | Starts a real, slow finite FFmpeg conversion, checks the process is still alive after startup returns, verifies the input path remains pinned, stops the job and verifies release. Missing-input startup must clean up its job and handles. |
| `LiveDriveReadTests` and native input binding | An owned temporary drive is repointed after acquisition; managed bytes and actual FFmpeg PCM checksums must still match the original file. Nested reads use the same volume address; user/configuration entry points still reject extended aliases. SUBST cannot hide a junction ancestor; raw NT aliases with hidden ancestors fail closed. Native command rebinding changes exactly the selected generated input, preserving other arguments and rejecting ambiguous/missing inputs. |
| Existing live HTTP/playback/migration tests | Large unvisited trees, blocked legacy mutation/online endpoints, real direct/range/transcoded audio/video, subtitles, independent bookmarks across eviction/restart, native network traps and isolated legacy-profile import. |
| `WebClientSmoke/live-folders.cjs` | Actual simplified UI, headless desktop/mobile, picker-based setup without typed paths, Favorites shortcuts, Continue single/all dismissal, name/date/size order, local playlist queues/duplicates, Play from here, pause/stop/next/previous/skip/shuffle/repeat/automatic advance, direct/HLS playback, subtitles, saved places, account isolation, cache clearing, enable/disable/removal, disconnected-root recovery and process restart. Records external browser attempts before denying them and requires zero attempts; fixture hashes/mtimes remain unchanged. |
| `WebClientSmoke/live-upgrade.cjs` through `scripts/smoke-upgrade-package-win.ps1` | Extracts the actual older and replacement ZIPs into an owned fixture. The older binary creates real accounts, restricted/disabled/disconnected roots and separate audiobook bookmarks. The replacement upgrades a copied profile while all synthetic media content is locked. Checks unchanged legacy catalog/state, only saved-address import, surviving tokens/permissions, local metadata/range playback, disconnected bookmark recovery, cache eviction, fresh-browser resume and repeated restarts without overwriting newer progress. Original profile and media hashes/mtimes must stay identical. |
| `OfflineHostParsingTests` and `OfflineNetworkAudit` | Host parsing accepts numeric addresses and literal localhost without DNS. Real name-resolution events provide a positive control. The process-local native audit observes server/launcher/helper calls throughout the headless UI workflow and legacy online/refresh requests, old online-enabled settings, malicious playlist/NFO URLs and restart; managed and native loopback controls prove the observer sees actual attempts. |
| `ParseNetworkTests.ProxyTrust_UsesOnlyExplicitOfflineAddresses` | Actual forwarded-header middleware accepts explicitly configured numeric proxies/subnets and localhost; unknown hostnames disable forwarding, and configuring a non-loopback proxy does not implicitly trust loopback peers. |
| `LiveStorageBoundaryTests` | Actual NTFS hardlinks in private database/config/log/image/transcode locations are rejected before startup writes; nested private links are rejected without following targets. A temporary, unused DOS drive alias cannot hide private/media overlap. Repointing it between requests rejects saved IDs while other roots remain available. A dangling network-device mapping does not require media access at startup with local private storage. Logging configuration cannot redirect output into media or load requested sink assemblies. |
| SMB/alias cases in `LiveStorageBoundaryTests` | Real 8.3 spellings, local UNC aliases (including an unresolvable host), and LanmanRedirector/Mup device names cannot disguise private/media overlap. Real SMB reads show additions/removals immediately. An owned temporary SMB mapping disconnects/reconnects without losing saved IDs. No existing share, mapping, credentials, firewall or server service is changed. |
| Short-path configuration/read regressions | Pure lexical configuration and saved-address import preserve 8.3 spelling without media access; selected listing, lookup and re-add keep the same IDs. Mixed long/short private aliases and a private SUBST target using a short spelling still reject overlap. Two real-helper HTTP cases add audio/video playback and restart/resume through short-spelled roots. |
| No-access mount regressions | `LiveLibraryStoreTests` and the shared HTTP fixture require zero media stats as well as zero enumeration on AddLibrary/AddPath and home queries. Missing locations can be saved and show as unavailable only when opened. The packaged native audit retains an unvisited, unreachable 8.3-spelled UNC root through restart, requiring zero attempted UNC access. |
| `audit-path-normalization-win.ps1` | Before/after native positive control invokes each build's actual normalizer on an existing 8.3 fixture; the old build must make a GetLongPathNameW call and the new build must make none. Both kernel32/kernelbase implementations are observed. |
| SMB cases in `LivePlaybackTests` | The full selected-file HTTP/helper matrix also runs through the existing local SMB share: ordinary and >300-character audio/video paths, NFO/art/subtitles, direct/range/conversion, per-request TV auth, subtitles Off, cache eviction and server-restart bookmarks; synthetic media bytes/mtimes stay unchanged. |
| `NativeClientSmoke/android-tv.cjs` | Unmodified official Android TV release in an owned windowless Android guest: login, empty/nested folders, local NFO, audio progress/pause and app restart, actual video/subtitle rendering, stop and immediate Resume. Changed durable positions, not server-extrapolated counters, are playback proof. Stock folder-audio auto-resume remains a recorded limitation. |
| `NativeClientSmoke/android-mobile.cjs` and `WebClientSmoke/android-shell.test.cjs` | Official phone app's embedded WebView recognizes the local bootstrap bundle and does not collide with its globals. Tests connection/login, hardware Back, folder navigation, selected local metadata, audiobook save/reopen/resume and video with subtitles Off. This is not ExoPlayer/background media-session coverage. |
| `UserDataChangeNotifierTests` | Shutdown discards queued notification batches and awaits in-flight sends before service disposal; no async-void timer exception may kill the process. |

`LiveFolderFixture` shares setup and assertions instead of duplicating many large
scan fixtures. Every browse must enumerate exactly its requested directory and
may stat only that directory/ancestors, not children or sidecars. Mounting and
startup must not enumerate; startup must not even stat media. Browsing works with
all file content exclusively locked. Legacy catalog row counts cannot grow.
The test's explicit successive navigation through the corpus is not server recursion.

Case counts therefore differ from the original catalog suite. No remaining failing
family was blanket-skipped. Tests that need real FFmpeg explicitly skip if the helper
is not supplied; release validation must supply it and inspect the results.

## Running locally

Use the installed .NET 10 SDK. In PowerShell, set the helper to the prepared binary:

```powershell
$env:JIGGLEFIN_TEST_FFMPEG = 'D:\Jigglefin\publish\ffmpeg-prepared-test\ffmpeg.exe'
# Opt in only when the existing local administrative share is readable. The
# SMB fixtures use owned temporary data and never create/modify a share.
$env:JIGGLEFIN_TEST_LOCAL_SMB = '1'
dotnet test tests/Jellyfin.Server.Integration.Tests --no-restore -m:1
dotnet test tests/Jellyfin.Api.Tests --no-restore -m:1
dotnet test tests/Jellyfin.Server.Implementations.Tests --no-restore -m:1
dotnet test tests/Jellyfin.Server.Tests --no-restore -m:1
dotnet test tests/Jellyfin.Controller.Tests --no-restore -m:1
dotnet test tests/Jellyfin.MediaEncoding.Tests --no-restore -m:1
node Jigglefin.Web/build.mjs
node tests/WebClientSmoke/live-folders.cjs
```

Build these projects sequentially: implementations tests reference the integration
project, and concurrent builds or copying assemblies while the source-built server
runs can produce Windows file-lock failures. Independent `--no-build` test runs can
then be parallelized. Browser tests run headlessly, create random loopback ports and
own their child processes; they do not drive the user's browser or installed server.

Green source tests do **not** prove a release upgrade, actual native-client behavior
or a complete no-internet/path-race boundary. Those remain separate completion gates
in the design document; the Apple-device UI check was explicitly deferred by the user.

## Actual Windows ZIP checks

The black-box package tests use Windows PowerShell 5.1, Node (tested with 25.8.1,
including built-in `node:sqlite`) and the pinned Playwright dependency in
`tests/WebClientSmoke`. They launch the packaged self-contained executable, not
the source build. The upgrade harness verifies the older archive's SHA-256 before
extracting it; its default checksum is for the audited `7d5e6d75` release.

```powershell
./scripts/smoke-live-package-win.ps1 -PackageDirectory publish/<new-package>
./scripts/smoke-upgrade-package-win.ps1 `
  -OldArchive publish/release-7d5e6d75/Jigglefin-win-x64.zip `
  -NewArchive publish/<new-package>.zip
```

Each test retains its own `%TEMP%` fixture and screenshots. Successful runs write
`web-smoke-report.json` or `upgrade-report.json`; the latter includes both ZIP
checksums. Random loopback ports and verified child identities keep tests separate
from the installed server. Never substitute a real profile or real media directory.
The upgrade fixture's media lock is released before selection/playback; locked
startup/resume is evidence of no content reads, not a claim that playback needs no
file access. Synthetic profile upgrade is now covered by a real binary-to-binary
test, but is not proof of every historical Jellyfin version or the user's profile.

For reproducible server/native-helper/browser outbound checks, see
`OfflineNetworkAudit/README.md` and `scripts/audit-offline-win.ps1`. This extends the
browser's own request interception with native process-tree observation; it does
not silently turn off an existing system-wide network capture or inspect other apps.
