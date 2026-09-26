# Jigglefin portable Windows server

![Jigglefin logo](Jigglefin-logo.png)

This is a development build of the live-folder replacement, not a finished release.
It includes the .NET runtime, a simplified local folder-browser UI, and Jellyfin FFmpeg
and FFprobe. An actual older-release ZIP upgrade has passed with a copied synthetic
profile, including accounts, folder permissions and saved audiobook positions. Complete
offline/security and native-client release checks are still in progress. Use a separate
test profile for now; this is not approval to upgrade your production installation.

1. Extract the ZIP into a new folder.
2. Run `Start-Jigglefin.ps1 -DataDir <new-test-profile-path>` from PowerShell.
3. Open `http://localhost:8096/web/`, create a local account, then add folder locations
   under Settings. Standard Jellyfin apps connect to the same server address.

Folders are read when opened; there is no scan or catalog rebuild. Selecting a file reads
its local NFO/artwork, and playing it probes only that file. New files appear when you reopen
or reload the folder. Only filename sorting/filtering is offered. Unsupported file formats
are still visible. There are no online metadata, plugin-download or update controls.

Saved positions and favorites are per account and separate from the selection cache.
Clearing that cache does not clear bookmarks. Actual file moves/renames currently change
item identity. Standard clients may expose unsupported catalog menus; use their Folder View.
Native client behavior is not fully verified yet, including audiobook automatic resume on
Android TV. Apple-device UI testing remains deferred.

Media locations are read-only to the application. Its profile and caches must be outside
your media trees. Do not point this development build at an existing Jellyfin/Jigglefin
profile yet. Without `-DataDir`, Jigglefin uses `%LOCALAPPDATA%\jigglefin`; temporary files
normally use `%TEMP%\jigglefin`. Two servers cannot share port 8096.

`Start-Jigglefin.ps1 --help` and `Start-Jigglefin.ps1 --version` do not start a server or
create a profile. The bundled FFmpeg is selected by default.

The server is derived from [Jellyfin Server](https://github.com/jellyfin/jellyfin).
The folder UI is part of Jigglefin and bundles hls.js 1.6.16 locally (Apache-2.0).
The bundled [Jellyfin FFmpeg 8.1.2-5](https://github.com/jellyfin/jellyfin-ffmpeg/releases/tag/v8.1.2-5)
source is available at that tag. GPL and hls.js license files are included.
See the [Jigglefin repository](https://github.com/stevenAthompson/Jigglefin) and
`JIGGLEFIN-LIVE-DESIGN.md` for the current implementation and uncompleted release gates.
