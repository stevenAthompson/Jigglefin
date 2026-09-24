# Jigglefin architecture

Jigglefin is a Windows-focused, folder-first fork of Jellyfin Server. Its compatibility boundary
is the Jellyfin HTTP/WebSocket API: unmodified Jellyfin applications should continue to discover,
authenticate with, browse, and stream from a Jigglefin server.

## Product rules

1. The filesystem is authoritative. A folder or playable file must not disappear because online
   metadata is absent, ambiguous, or unavailable.
2. Local metadata wins. Kodi NFO, Emby XML, local artwork, embedded tags, and filenames are used
   before any synthesized fallback.
3. API compatibility wins over internal purity. Jigglefin may synthesize Jellyfin entity types and
   fields from paths so existing clients receive the contracts they already understand.
4. Network metadata is opt-in. A new library must work offline without provider configuration.
5. Upstream changes stay mergeable. Jigglefin-specific changes should be small, covered by tests,
   and concentrated at library-resolution and default-policy seams.

## Initial compatibility strategy

Jellyfin clients select layouts and queries from `CollectionType` and item kinds such as `Movie`,
`Series`, `MusicAlbum`, and `AudioBook`. Replacing those public values with a new Jigglefin-only
type would require custom clients, so Jigglefin keeps them. Folder and filename information will be
translated into those existing DTO shapes where clients require it.

The first milestone establishes local-first policy without changing the wire protocol:

- the existing Jellyfin physical `Folders` view is enabled on new installations;
- remote metadata providers are disabled by default for new libraries;
- Kodi NFO readers remain enabled, and Jigglefin reads legacy Emby movie and series XML sidecars;
- local artwork, screen grabbing, and image extraction remain available;
- direct play, transcoding, authentication, and all existing client endpoints stay upstream code.

The same local-first provider defaults also apply when a client creates a library through the API
without `TypeOptions`. Explicit per-type provider choices are preserved.
Adding a library while an initial scan is still running queues a follow-up scan. Otherwise a
fresh installation can miss the new library if the active scan has already passed the root.
System-owned view queries also tolerate a missing user context during background artwork work.

The library entry views for Movies, TV Shows, and Books now list the immediate children of their
physical media folders. This keeps an `Action/Example Movie/Example Movie.mkv` tree navigable as
`Action` then `Example Movie` through the existing `UserViews` and `Items` endpoints. Music and
other folder-oriented library views already use the physical children path. Recursive queries still
serve searches and client features that request the full library.

Standard clients choose a metadata-first landing page when a `UserViews` entry advertises a movie,
TV, music, or book collection type. Jigglefin presents those media library *entries* as ordinary
folders in `UserViews` so clients use their existing folder-browser route by default. The stored
collection type, media item kinds, and deeper API contracts remain unchanged. An end-to-end check
with unmodified Jellyfin Web verified navigation from the library tile through `Action` to a loose
movie and through `Comedy` to a dedicated movie item.

Native folder browsers such as
[Swiftfin](https://github.com/jellyfin/Swiftfin/blob/main/Shared/Objects/Libraries/ItemLibrary.swift)
can send a broad `includeItemTypes` list containing
`Folder` and `sortBy=SortName` instead of Jellyfin Web's folder-field signature. Such requests
list the immediate physical children and present navigable series and music albums as folders.
Explicit recursive queries continue to list media throughout the library, and direct item
details keep their standard media kinds. An integration test exercises this native
request shape through the legacy user-scoped API as well as the current endpoints. This is API
contract coverage, not yet a full Swiftfin UI test.
The same broad type filter inside a series keeps physical season and bonus folders browseable
without inserting the pathless generated `Season Unknown` into the folder list.
[Jellyfin Android TV's folder grid](https://github.com/jellyfin/jellyfin-androidtv/blob/master/app/src/main/java/org/jellyfin/androidtv/ui/browsing/BrowsingUtils.kt)
uses a parent-only `Items` request with requested item fields. An integration test verifies that
this shape returns a navigable physical folder with its path and child count. The unmodified
Android TV 0.19.10 app was also exercised in a headless Android 34 emulator against an isolated
Windows server profile: it browsed `Movies/Action/Native Test Movie` and played the movie, then
browsed `Books/Fantasy/Native Test Audio` and played the 20-second M4B to completion.
The same client also browsed `Music/Rock/Native Artist/Native Album/Track 01` and
`TV/Drama/Native Show/Season 1/Native Pilot`, showed local NFO fields, and played the MP3 track
and episode to completion. All of these client checks used a headless emulator and an isolated
server profile; they were not desktop UI tests or CI automation.
Android TV's details screen has no playback actions for `AudioBook` items. Jigglefin therefore
presents playable audiobooks as `Audio` DTOs only to the Jellyfin Android TV client; stored item
kinds and responses to other clients remain `AudioBook`. Untagged audio's minimum-date sentinel
is omitted from DTOs, allowing the local XML year to display instead of `Jan 1, 1`.
An integration test also reports a partial audiobook playback session and verifies that the server
returns the saved bookmark in `UserData.PlaybackPositionTicks`. Automatic audiobook resume is still
a client-side limitation in Android TV 0.19.10: its [audio playback route](https://github.com/jellyfin/jellyfin-androidtv/blob/v0.19.10/app/src/main/java/org/jellyfin/androidtv/ui/playback/PlaybackLauncher.kt)
does not pass the requested start position to the audio player, and its
[audio queue implementation](https://github.com/jellyfin/jellyfin-androidtv/blob/v0.19.10/app/src/main/java/org/jellyfin/androidtv/ui/playback/rewrite/RewriteMediaManager.kt)
starts playback without seeking to the saved position. The standard API retains the bookmark for
clients that support resume; Android TV users can seek manually in its audio player.
The unmodified [Jellyfin Android mobile app](https://github.com/jellyfin/jellyfin-android/releases/tag/v2.7.3)
2.7.3 (libre release) was also tested in the headless emulator. It browsed
`Books/Fantasy/Native Test Audio` and `Movies/Action/Native Test Movie`, showed the local XML/NFO
metadata, and played each 20-second sample to completion. This was a CLI-only native-client
check, not an automated CI test.

An integration test renames and removes physical movie, audiobook, TV-series, and music-album directories,
rescanning after each change. The old entries disappear; renamed entries keep their standard
media kinds and stream from the new paths; the renamed series retains a physical season and
playable episode. Removed playable files no longer appear in recursive
queries. This protects the filesystem-as-source-of-truth rule beyond the initial library scan.
When the last file is removed from a previously typed movie, series, book, or music-album directory,
a rescan restores that directory as an empty physical `Folder` rather than dropping it from browse;
re-adding the file promotes it back to the compatible media kind.
Another integration test keeps an empty category and its empty child browseable alongside populated
media in Movies, TV Shows, Music, Books, Home Videos, and Music Videos libraries, including the
folder-list request used by Jellyfin Web.
An additional browse test verifies that two arbitrary grouping levels retain their physical paths
before a typed movie, series, book, or music album, including the broad sorted item query used by
native folder browsers.
A movie library with two physical roots also keeps both paths browseable when each root contains
an `Action/Same Movie` tree. The client receives two distinct Movie items, and both stream from
their own paths rather than losing one to a same-name category or movie collision.

The resolver chain now leaves an arbitrary TV grouping directory as a `Folder` unless it contains
`tvshow.nfo`, `series.xml`, or episode/season evidence. A music grouping directory is not inferred to be a
`MusicArtist` merely because it contains an album; an explicit `artist.nfo` or `artist.xml`
identifies an artist folder. The media beneath those groups still resolves to standard Jellyfin
item kinds. These rules are covered by unit tests and an end-to-end API browse test for movies,
TV shows, books, audiobooks, music, home videos/photos, and music videos.
When a TV directory contains a loose named episode from a different show, it remains a physical
folder and the file appears beneath it as an `Episode`; a directory matching the episode's show
name still resolves as a `Series`.
Inside a show, unnumbered video subdirectories remain physical `Folder` items instead of becoming
unnamed virtual seasons. The ordinary `Items?parentId=<series>` route exposes those folders and
their playable episodes; `Shows/<series>/Seasons` remains seasons-only for existing client flows.
Physical season folders also expose their nested directories through `Items`, and an unnumbered
episode beneath one inherits that season number rather than appearing in `Season Unknown`.
Jellyfin Web's folder-list responses present physical `Series` and `Season` entries as `Folder`
DTOs, so clicking through a show or season reaches those directories. Direct item details and
`Shows/<series>/Seasons` keep the real show and season types for standard client features. A
generated `Season Unknown` without a filesystem path stays out of ordinary `Items` browsing but
remains available through the Shows endpoint for episode grouping.

A separate integration test writes a valid local WAV file, browses it as a standard Jellyfin `Audio`
item, and verifies full and byte-range streaming through the unmodified audio endpoint.
The library browse test also uses an original, synthetic M4B audiobook to verify full and byte-range
audio streaming from the standard `AudioBook` item.
With the verified Jellyfin FFmpeg path, additional end-to-end tests transcode a folder-browsed movie
to an HLS transport-stream segment and a folder-browsed M4B audiobook to MP3. The movie test also
checks that a sibling `.eng.srt` is advertised in the standard media source and served through the
subtitle endpoint. Windows CI prepares
the same FFmpeg binaries before its test step and includes them in the portable package.
Another checks that a Kodi-style `movie.nfo` supplies the title, year, and plot in standard client
responses while its physical parent folder remains browseable. That test uses an original,
synthetically generated MP4 and also checks full and byte-range direct video streaming.
An artwork integration test confirms that a new physical movie uses its local `poster.png` and
`fanart.png`, advertises both image types in its item DTO, and serves the poster through the
standard image endpoint. A subsequent scan updates the poster tag after the local file changes;
removing the poster clears the primary image without discarding the still-present backdrop.
An additional integration test checks legacy Emby `movie.xml` in a dedicated movie directory and
`<movie-file>.xml` beside a loose movie. Both provide standard client title, year, and overview
fields. If XML and NFO coexist, NFO takes priority. Subsequent scans also pick up NFO/XML edits
and removals: deleting the preferred NFO falls through to XML, and removing the final sidecar
returns the item to its path-derived title and year instead of retaining stale metadata. The
internal sidecar provenance used for this is not included in client provider IDs.
Malformed `movie.xml` or `movie.nfo` files do not hide the physical movie or prevent direct
streaming; the movie retains its path-derived fallback title.
Another integration test checks `series.xml` in a show directory below a physical TV category.
The normal `Series` item receives its local title, year, and overview, while the category remains
visible as a folder. The XML also marks a show with only an unnumbered bonus subfolder as a
`Series`; that subfolder and its playable episode remain browseable. A coexisting `tvshow.nfo`
takes priority over the series XML. `<episode-file>.xml` supplies local episode title and overview
through the standard show and item endpoints; an adjacent episode NFO takes priority.
Physical TV seasons also read `season.xml` for local title and overview. A season's own
`season.nfo` takes priority, while a named season in the parent `tvshow.nfo` remains a fallback
when neither season sidecar is present. Rescans pick up XML edits and fall through from removed
NFO to XML, then to the parent show's named-season entry when both season sidecars are removed.
The same rescan test checks edited show and episode XML, fallback from removed show and episode
NFO to XML, and filename-derived names after the last XML sidecars are removed.
Jigglefin also reads legacy-style `artist.xml` and `album.xml` in physical music directories.
The upstream OPF reader supplies local book metadata. A basename-matched `.opf` can describe one
book in a mixed directory; generic `content.opf` and Calibre `metadata.opf` apply when the
directory contains exactly one supported book or audiobook file, even if its folder name differs. They cannot
overwrite the names of unrelated sibling books. Removing a specific OPF on a later scan returns
that book to its filename without applying an ambiguous shared OPF. A basename-matched `.xml` with legacy Emby-style
fields likewise describes only its book; `book.xml` applies only when the directory contains one
supported book or audiobook file. OPF takes precedence if both formats are present. Audiobooks
also accept basename-matched `.xml` sidecars; generic
`audiobook.xml` or `book.xml` applies only when the directory contains one supported book or
audiobook media file, so it cannot rename sibling chapters or an adjacent ebook.
Home-video and music-video files also accept basename-matched legacy `.xml` sidecars. They do not
inherit a folder-wide XML file, and a coexisting basename-matched NFO takes precedence.

A movie category with a single loose file, such as `Action/Loose Movie.mp4`, remains a physical
`Folder` with a `Movie` child. A dedicated movie directory still resolves to a `Movie` when its
name matches the video filename or it contains `movie.nfo` or `movie.xml`; this distinction is
covered by an API browse test.
A directory containing two distinct movie files remains a physical `Folder`, with both `Movie`
children browseable and directly streamable rather than collapsing the directory into one movie.
If a named movie directory also contains an `Extras` or `Trailers` directory, it remains a
physical `Folder`: the main movie and bonus directory are both reachable through ordinary
`Items` browsing instead of hiding the bonus path behind movie-detail extras handling.
In a Home Videos and Photos library, a directory with both photos and videos, or photos and child
directories, remains a physical `Folder`. A DVD/Blu-ray rip beside a standalone photo also
remains a folder instead of replacing it with a single video. Photo-only directories may retain
Jellyfin's `PhotoAlbum` type for detail endpoints, but folder-list responses present them as
`Folder` items so the unmodified Web client can continue down the physical path.

Book and audiobook directories also stay physical `Folder` items when they contain child
directories, when their only media file has a different title from the directory, or when an
EPUB and audiobook share the same directory. This keeps loose books, mixed formats, and nested
bonus folders browseable in standard clients. A directory named for its single book or
audiobook still resolves to the usual `Book` or `AudioBook` item.
When a loose movie, book, or audiobook file has the same basename as a sibling directory, that
directory remains a physical `Folder` and its own media file remains browseable underneath. Loose
audiobooks take their display name and year from the filename, not from a category folder.
Music directories with tracks alongside an unrelated child directory likewise stay `Folder`
items, so the tracks and child directory remain visible. A normal album without such children,
including a recognized `Disc 1`-style multi-disc layout, still resolves as `MusicAlbum`.
For a plain unsorted `Items?parentId=<album>` browse, a multi-disc album exposes its physical
disc folders and their tracks. Jellyfin Web's folder-list requests, including a user-selected
sort, also receive physical disc folders and present artist and album entries as `Folder` DTOs,
so clicks stay in the folder browser. Direct item details retain the real `MusicArtist` and
`MusicAlbum` kinds. Music-specific sorted, filtered, and recursive track queries remain flattened
for playback and the unmodified Jellyfin Web detail pages.
Within a physical genre/artist/album tree, Kodi-style `artist.nfo` and `album.nfo` supply the
standard `MusicArtist` and `MusicAlbum` metadata. Local `artist.xml` and `album.xml` supply the same
item kinds and common title, year, and overview fields when NFO is absent. NFO takes priority when
both sidecars are present. An album's local sidecar title takes precedence over names inferred from
its tracks, including on subsequent library scans. Removing an artist or album NFO on a later scan
falls through to its XML sidecar; edits to that XML are picked up on rescan, and removing both
sidecars restores the physical names. An artist
without either sidecar becomes a navigable physical `Folder`, while its album remains browseable.

Further resolver work will preserve more physical directory arrangements while assigning compatible
Jellyfin item kinds from deterministic path rules and local sidecars. Other ambiguous and
mixed-content directories still need more coverage.

## Upstream workflow

The Git remotes are intentionally split:

- `origin`: `https://github.com/stevenAthompson/Jigglefin.git`
- `upstream`: `https://github.com/jellyfin/jellyfin.git`

`master` is reserved as a clean mirror of `upstream/master`. Jigglefin changes live on the
`jigglefin` branch. To update:

```powershell
git fetch upstream
git switch master
git merge --ff-only upstream/master
git push origin master
git switch jigglefin
git merge master
dotnet test Jellyfin.sln
git push origin jigglefin
```

Keeping product changes in focused commits makes conflicts reviewable and allows individual
Jigglefin patches to be rebased, replaced by an upstream implementation, or temporarily reverted.

## Windows development

The current upstream baseline requires the .NET 10 SDK and FFmpeg. Build and test with:

```powershell
dotnet restore Jellyfin.sln --locked-mode
dotnet build Jellyfin.sln --configuration Debug --no-restore
dotnet test Jellyfin.sln --configuration Debug --no-build
```

Set `JIGGLEFIN_TEST_FFMPEG` to a Jellyfin FFmpeg executable before running tests to exercise the
movie and audiobook transcoding checks locally. Without it, those two checks are skipped; Windows
CI sets it to the verified binary that goes into the portable package.

Run without an embedded web client during server work:

```powershell
.\scripts\dev-run.ps1
```

For a portable Windows x64 build, the `Jigglefin CI` workflow publishes a `Jigglefin-win-x64`
artifact containing a self-contained server and Jellyfin Web. It builds the unmodified web client
from pinned upstream commit `839563a2d633041d5854948d680bd393423b1abb` using Node 24. The
package includes the official Jellyfin FFmpeg 8.1.2-5 Windows binaries, verified against a pinned
SHA256 digest. It can also be reproduced locally by building that web commit with `npm ci` and
`npm run build:production`, running `scripts/prepare-ffmpeg.ps1 -OutputDirectory <ffmpeg-path>`,
then running `scripts/package-win.ps1 -WebDistPath <web-dist-path> -FfmpegDirectory <ffmpeg-path>`.
Before artifact upload, CI runs `scripts/smoke-package-win.ps1` against the assembled package.
It uses a fresh temporary profile and random one-time admin password to check setup, login,
creation of a movie library through Jellyfin Web's `PathInfos` payload with a comma in its Windows path,
physical movie, audiobook, music, TV, home-video/photo, and music-video folders, local NFO fields,
standard client playback negotiation, external subtitles, direct media bytes, and headless playback
of all six playable media types plus a photo-only album browse in unmodified Jellyfin Web.
Successful profiles are cleaned up after the server stops when Windows releases their files.
To repeat the full client test locally after packaging, run `npm ci --prefix tests/WebClientSmoke`,
`tests/WebClientSmoke/node_modules/.bin/playwright.cmd install chromium`, then
`scripts/smoke-package-win.ps1 -PackageDirectory <package-path> -HeadlessWebClient`.
The extracted package includes `Start-Jigglefin.ps1`; see its `README-PORTABLE.md` for first-run
instructions. Its bundled FFmpeg is used by default.

Jigglefin defaults to `%LOCALAPPDATA%\jigglefin` for its data, configuration, cache, and logs,
and `%TEMP%\jigglefin` for temporary files.
It does not read Jellyfin's default profile or `JELLYFIN_*_DIR` path overrides. Use
`JIGGLEFIN_DATA_DIR`, `JIGGLEFIN_CONFIG_DIR`, `JIGGLEFIN_CACHE_DIR`, `JIGGLEFIN_LOG_DIR`, or
`JIGGLEFIN_WEB_DIR` to override Jigglefin paths; command-line directory flags take precedence.
Do not point `--datadir` at an existing Jellyfin profile unless you intentionally want Jigglefin to
open and potentially migrate that database. The server still uses the upstream Jellyfin logging
template internally, but its log directory is resolved inside Jigglefin's profile.

The launcher prefers the per-user .NET 10 SDK and the official `Jellyfin.FFmpeg` WinGet package,
even if another `ffmpeg.exe` appears earlier in the system path. Pass server arguments through it,
for example `.\scripts\dev-run.ps1 --datadir D:\JigglefinData`.

A separately installed Jellyfin client can then connect to `http://localhost:8096`.
