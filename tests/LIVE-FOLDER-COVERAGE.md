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
| `LiveFolderApiFilterTests` | Strict mocks prove authorization and empty catalog fallbacks do no filesystem work. Active-session resume exclusion is per user and occurs before path access. |
| Existing live HTTP/playback/migration tests | Large unvisited trees, blocked legacy mutation/online endpoints, real direct/range/transcoded audio/video, subtitles, independent bookmarks across eviction/restart, native network traps and isolated legacy-profile import. |
| `WebClientSmoke/live-folders.cjs` | Actual simplified UI, headless desktop/mobile, direct/HLS playback and seeking, subtitles, saved places, account isolation, cache clearing, enable/disable/removal, disconnected-root recovery and process restart. Records external browser attempts before denying them and requires zero attempts; fixture hashes/mtimes remain unchanged. |

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
