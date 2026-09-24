# Jigglefin

![Jigglefin logo](branding/jigglefin-256.png)

Jigglefin is a Windows-focused, folder-first fork of Jellyfin Server. It keeps the standard
Jellyfin API and media item types so unmodified Jellyfin clients can connect, browse, and stream.
It is an early testing build, not yet a finished replacement for Jellyfin.

The filesystem is the source of truth. A movie library such as:

    Movies/
      Action/
        Loose Movie.mp4
      Comedy/
        Matched Movie (2020)/
          Matched Movie (2020).mp4
          movie.nfo

opens as Action and Comedy folders in the normal client library tile. The loose file is a Movie
item; the dedicated movie directory is one Movie item with local NFO metadata. TV, music, books,
audiobooks, home videos/photos, and music videos use the same folder-first approach while
retaining their usual Jellyfin media item kinds.

## Try the Windows build

Download the Jigglefin-win-x64 ZIP from a successful run of
[Jigglefin CI](https://github.com/stevenAthompson/Jigglefin/actions/workflows/jigglefin-ci.yml).
This portable package includes the .NET runtime, unmodified Jellyfin Web, and verified official
Jellyfin FFmpeg and FFprobe binaries. It does not require a separate FFmpeg installation.

1. Extract the ZIP somewhere you can keep between upgrades.
2. Run Start-Jigglefin.ps1 in PowerShell from the extracted folder.
3. Open http://localhost:8096/web/, create an admin account, and add folders as media libraries.
4. Connect other Jellyfin clients to the server address, such as http://WINDOWS-HOST:8096.

Read the included README-PORTABLE.md for profile locations, command-line options, and cautions.
Jigglefin stores its data under %LOCALAPPDATA%\jigglefin and does not open an existing Jellyfin
profile by default. Do not point it at a Jellyfin profile unless you intentionally want it to
open and potentially migrate that database. Jellyfin and Jigglefin cannot both bind port 8096.

## Current behavior and limits

- Physical category folders stay navigable. A single loose movie or named TV episode does not
  automatically replace its parent category with a Movie or Series. Book and audiobook folders
  with additional child folders or both book and audiobook files remain browseable instead of
  hiding those entries, as do movie folders with `Extras` and music folders that mix tracks with
  non-disc subfolders. A multi-disc `MusicAlbum` exposes its physical disc folders in the folder
  browser. Home-video folders with both photos and videos, or photos and child folders, remain
  physical folders; photo-only albums open through the Web folder browser. Music-specific track
  lists remain compatible with Jellyfin Web. Same-named movie, book, and audiobook files beside
  directories keep both paths browseable; loose audiobook names come from filenames.
- Kodi-style NFO and legacy-style `movie.xml`, `<movie-file>.xml`, `series.xml`, `season.xml`,
  `<episode-file>.xml`, `artist.xml`, `album.xml`, `<book-file>.xml`, and `<audiobook-file>.xml`
  sidecars, local artwork, embedded tags, and
  filenames are used ahead of remote metadata. Network metadata providers are disabled by
  default for new libraries; explicit provider choices remain available. Book OPF sidecars are
  supported without applying a shared `metadata.opf` to unrelated books in a mixed folder.
  A shared `book.xml` or `audiobook.xml` is read only when its folder has exactly one book or
  audiobook media file; OPF takes precedence over book XML. Other legacy XML media types remain
  work in progress.
- Media library entries are presented as folders in UserViews so standard clients use their
  built-in folder browser. Web folder-list responses also keep shows, seasons, artists, and albums
  navigable; direct details retain their standard media types.
- The unmodified Jellyfin Web client is tested headlessly against the packaged server for physical
  movie, audiobook, music, TV, home-video, and music-video folder navigation and sustained playback
  of synthetic media, plus browsing a photo-only album. Automated API
  tests cover browse paths, rename/removal rescans, and direct or byte-range
  streaming for movie, music, and audiobook samples, including same-named movie, TV, and music
  trees in separate library roots. Windows CI also uses its bundled FFmpeg to
  verify movie-to-HLS and audiobook-to-MP3 transcoding, plus local external subtitle delivery.
  Before uploading the ZIP, CI starts the packaged server with an isolated profile, completes
  setup with a throwaway password, and checks authenticated movie, audiobook, music, TV,
  home-video/photo, and music-video folder browsing,
  local movie NFO metadata, client playback negotiation, external subtitles, direct streaming,
  headless Web playback, discovery and removal of a movie in a running monitored library, and library,
  metadata, and streaming persistence after a clean restart. It also discovers a movie added
  while the server was stopped.
  A Swiftfin-style API test checks folder browsing with broad item-type
  filters, including physical series and music albums; an Android TV-style items query checks
  folder paths and child counts. A restricted-user API test verifies that known folder and media
  IDs cannot bypass a blocked library's browse or stream permissions. Unmodified Android TV and
  Android mobile apps were also tested in a CLI-only emulator for native browsing and playback.
  Swiftfin UI coverage and more ambiguous mixed-content layouts remain work in progress.
  Android TV 0.19.10 plays audiobooks
  but does not automatically resume saved audio bookmarks; the server retains those positions.

The detailed design and upstream merge strategy are in [JIGGLEFIN.md](JIGGLEFIN.md).

## Build from source on Windows

Install the .NET 10 SDK and FFmpeg, then run these commands in PowerShell:

    git clone https://github.com/stevenAthompson/Jigglefin.git
    cd Jigglefin
    git switch jigglefin
    dotnet restore Jellyfin.sln --locked-mode
    dotnet build Jellyfin.sln --configuration Debug --no-restore
    dotnet test Jellyfin.sln --configuration Debug --no-build
    .\scripts\dev-run.ps1

The development launcher runs the server without an embedded web client. The CI workflow builds
the web client from a pinned upstream commit and packages the self-contained Windows server. See
[JIGGLEFIN.md](JIGGLEFIN.md) for the equivalent local packaging commands.

The master branch is kept as a clean mirror of jellyfin/jellyfin for upstream updates. Jigglefin
changes live on the jigglefin branch in focused, tested commits.

## Upstream and licenses

Jigglefin is based on [Jellyfin Server](https://github.com/jellyfin/jellyfin), which remains the
majority of this codebase. The portable package also includes unmodified builds of
[Jellyfin Web](https://github.com/jellyfin/jellyfin-web) and
[Jellyfin FFmpeg](https://github.com/jellyfin/jellyfin-ffmpeg). Jigglefin is an independent fork,
not an official Jellyfin release. The source and packaged license files identify the applicable
licenses for each component.
