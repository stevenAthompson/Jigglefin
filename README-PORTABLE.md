# Jigglefin portable Windows server

This is an early testing build of Jigglefin for Windows x64. It includes the .NET runtime and an
unmodified, matching Jellyfin Web client. FFmpeg is required separately.

1. Extract the ZIP to a folder you can keep between upgrades.
2. Install Jellyfin FFmpeg if needed: `winget install --id Jellyfin.FFmpeg --exact`.
3. Run `Start-Jigglefin.ps1` from PowerShell in the extracted folder. Pass `-FfmpegPath` if FFmpeg
   is installed somewhere the launcher cannot find it.
4. Open `http://localhost:8096/web/` and complete the normal Jellyfin setup wizard. Standard
   Jellyfin clients can then connect to the same server address.

Jigglefin stores its own data under `%LOCALAPPDATA%\jigglefin` and temporary files under
`%TEMP%\jigglefin`. It does not open an existing Jellyfin profile by default. You may pass
`-DataDir <path>` to the launcher for an explicit Jigglefin profile; do not point it at a Jellyfin
profile unless you intentionally want to migrate that database. Two servers cannot use port 8096
at the same time.

The bundled server is based on [Jellyfin Server](https://github.com/jellyfin/jellyfin). The web
client is built without changes from [Jellyfin Web](https://github.com/jellyfin/jellyfin-web) commit
`839563a2d633041d5854948d680bd393423b1abb`. License files are included in the extracted
package; source code is available in the linked repositories. See the
[Jigglefin repository](https://github.com/stevenAthompson/Jigglefin) for build instructions and
the current list of incomplete features.
