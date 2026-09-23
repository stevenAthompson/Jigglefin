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

The resolver chain now leaves an arbitrary TV grouping directory as a `Folder` unless it contains
`tvshow.nfo` or episode/season evidence. A music grouping directory is not inferred to be a
`MusicArtist` merely because it contains an album; an explicit `artist.nfo` identifies an artist
folder. The media beneath those groups still resolves to standard Jellyfin item kinds. These rules
are covered by unit tests and an end-to-end API browse test for movies, TV shows, books,
audiobooks, music, home videos/photos, and music videos.
When a TV directory contains a loose named episode from a different show, it remains a physical
folder and the file appears beneath it as an `Episode`; a directory matching the episode's show
name still resolves as a `Series`.
Inside a show, unnumbered video subdirectories remain physical `Folder` items instead of becoming
unnamed virtual seasons. The ordinary `Items?parentId=<series>` route exposes those folders and
their playable episodes; `Shows/<series>/Seasons` remains seasons-only for existing client flows.
Physical season folders also expose their nested directories through `Items`, and an unnumbered
episode beneath one inherits that season number rather than appearing in `Season Unknown`.

A separate integration test writes a valid local WAV file, browses it as a standard Jellyfin `Audio`
item, and verifies full and byte-range streaming through the unmodified audio endpoint.
The library browse test also uses an original, synthetic M4B audiobook to verify full and byte-range
audio streaming from the standard `AudioBook` item.
With the verified Jellyfin FFmpeg path, additional end-to-end tests transcode a folder-browsed movie
to an HLS transport-stream segment and a folder-browsed M4B audiobook to MP3. Windows CI prepares
the same FFmpeg binaries before its test step and includes them in the portable package.
Another checks that a Kodi-style `movie.nfo` supplies the title, year, and plot in standard client
responses while its physical parent folder remains browseable. That test uses an original,
synthetically generated MP4 and also checks full and byte-range direct video streaming.
An additional integration test checks legacy Emby `movie.xml` in a dedicated movie directory and
`<movie-file>.xml` beside a loose movie. Both provide standard client title, year, and overview
fields. If XML and NFO coexist, NFO takes priority.
Another integration test checks `series.xml` in a show directory below a physical TV category.
The normal `Series` item receives its local title, year, and overview, while the category remains
visible as a folder. A coexisting `tvshow.nfo` takes priority over the series XML.
Jigglefin also reads legacy-style `artist.xml` and `album.xml` in physical music directories.
XML readers for books and other media kinds remain work in progress.

A movie category with a single loose file, such as `Action/Loose Movie.mp4`, remains a physical
`Folder` with a `Movie` child. A dedicated movie directory still resolves to a `Movie` when its
name matches the video filename or it contains `movie.nfo` or `movie.xml`; this distinction is
covered by an API browse test.

Book and audiobook directories also stay physical `Folder` items when they contain child
directories, when their only media file has a different title from the directory, or when an
EPUB and audiobook share the same directory. This keeps loose books, mixed formats, and nested
bonus folders browseable in standard clients. A directory named for its single book or
audiobook still resolves to the usual `Book` or `AudioBook` item.
Music directories with tracks alongside an unrelated child directory likewise stay `Folder`
items, so the tracks and child directory remain visible. A normal album without such children,
including a recognized `Disc 1`-style multi-disc layout, still resolves as `MusicAlbum`.
Within a physical genre/artist/album tree, Kodi-style `artist.nfo` and `album.nfo` supply the
standard `MusicArtist` and `MusicAlbum` metadata. Local `artist.xml` and `album.xml` supply the same
item kinds and common title, year, and overview fields when NFO is absent. NFO takes priority when
both sidecars are present. An album's local sidecar title takes precedence over names inferred from
its tracks, including on subsequent library scans.

Further resolver work will preserve more physical directory arrangements while assigning compatible
Jellyfin item kinds from deterministic path rules and local sidecars. In particular, ambiguous
file/folder name collisions and mixed-content directories need more coverage.

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
