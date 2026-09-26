# Jigglefin portable Windows server

![Jigglefin logo](Jigglefin-logo.png)

This is an early testing build of Jigglefin for Windows x64. It includes the .NET runtime and an
unmodified, matching Jellyfin Web client. It also includes the official Jellyfin FFmpeg and
FFprobe binaries for media probing and transcoding.

1. Extract the ZIP to a folder you can keep between upgrades.
2. Run `Start-Jigglefin.ps1` from PowerShell in the extracted folder. It uses the bundled FFmpeg
   by default; pass `-FfmpegPath` only to use a different copy.
3. Open `http://localhost:8096/web/` and complete the normal Jellyfin setup wizard. Standard
   Jellyfin clients can then connect to the same server address.

Jigglefin stores its own data under `%LOCALAPPDATA%\jigglefin` and temporary files under
`%TEMP%\jigglefin`. It does not open an existing Jellyfin profile by default. You may pass
`-DataDir <path>` to the launcher for an explicit Jigglefin profile; do not point it at a Jellyfin
profile unless you intentionally want to migrate that database. Two servers cannot use port 8096
at the same time.

Run `.\Start-Jigglefin.ps1 --help` to list server options or
`.\Start-Jigglefin.ps1 --version` to print the server version. These commands do not start the
server, create a profile, or require FFmpeg.

New profiles keep audiobook positions throughout each chapter, with no five-minute beginning or
ending cutoff. Existing profiles keep their saved settings: in Dashboard > Playback > Resume,
set both audiobook resume thresholds to `0` to use the new defaults. Previously discarded
positions cannot be recovered. Automatic resume also depends on the client; Android TV 0.19.10
does not automatically seek to saved audiobook positions.

The bundled server is based on [Jellyfin Server](https://github.com/jellyfin/jellyfin). The web
client is built without changes from [Jellyfin Web](https://github.com/jellyfin/jellyfin-web) commit
`839563a2d633041d5854948d680bd393423b1abb`. The bundled FFmpeg binaries come from
[Jellyfin FFmpeg 8.1.2-5](https://github.com/jellyfin/jellyfin-ffmpeg/releases/tag/v8.1.2-5);
its corresponding source and license are available at that tag. License files are included in
the extracted package. See the
[Jigglefin repository](https://github.com/stevenAthompson/Jigglefin) for build instructions and
the current list of incomplete features.
