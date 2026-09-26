# Jigglefin

![Jigglefin logo](branding/jigglefin-256.png)

Jigglefin is a Windows-focused, offline file-and-folder server derived from Jellyfin.
It keeps Jellyfin authentication, streaming and playback-state APIs so standard
Jellyfin clients can connect, without maintaining a scanned media catalog.

This branch (`codex/live-filesystem`) replaces the older, scan-backed Jigglefin.
It is a live-folder preview. Older CI artifacts from the `jigglefin` branch are
**not** this implementation. Use a separate test profile initially, and back up
your own profile before any later migration. No installer replaces your running server.

## How it works

- Adding a location saves its address. It does not scan, probe or even open the
  media location during setup or startup.
- Opening a folder lists its current immediate files and folders. New and removed
  entries appear on the next request—no rescan. A folder never collapses into a
  movie, album or series, and unsupported files remain visible.
- Selecting an entry reads its local NFO/XML/OPF and artwork where supported.
  Playing audio/video probes only that selected file. Small on-demand metadata
  caches are disposable and never determine what appears in a directory.
- Playback positions, favorites, accounts and folder permissions live in the
  private server profile. Clearing the selection cache does not erase bookmarks.
  IDs follow configured root and relative path; moving or renaming files changes
  their identity.
- Media is read-only to the application. Private profile, cache, log and temporary
  locations must be outside media trees. Links/reparse entries can be listed but
  cannot be traversed or played.
- There are no online metadata providers, remote artwork/subtitle searches,
  plugins, updates, remote URL media or opt-in internet features. Local/LAN media
  shares and incoming client connections remain supported.

The bundled web UI is a small folder browser, not Jellyfin Web. It includes local
accounts, a setup folder picker, folder access, Continue/Favorites, local playlist queues,
name/date/size sorting, playback and cache controls.
Native Jellyfin apps may still display their own catalog-oriented menus. Unsupported
APIs return compatible empty results or a navigable **Use Folder View** fallback;
they never trigger a recursive scan to imitate a catalog.

## Try a development Windows package

The portable ZIP includes the .NET runtime, the offline folder UI and prepared
Jellyfin FFmpeg/FFprobe. It does not require a separate runtime or encoder install.

1. Extract the ZIP into a new directory.
2. Run `Start-Jigglefin.ps1 -DataDir <new-test-profile-path>` in PowerShell.
3. Open `http://localhost:8096/web/`, create a local account, and select folder
   locations with Browse during setup (or later in Settings).
4. Connect standard Jellyfin clients to the same server address and use Folder View.

See [README-PORTABLE.md](README-PORTABLE.md) for launcher details. Without `-DataDir`,
the server uses `%LOCALAPPDATA%\jigglefin`; temporary files normally use
`%TEMP%\jigglefin`. Two servers cannot share port 8096.

## Verification and known limits

Automated tests cover fresh bounded listings, zero setup/startup media access,
local metadata edits, access restrictions, direct/range/transcoded playback,
subtitles, restart/resume, and unchanged synthetic media. Windows tests include
long paths, short-name aliases, owned drive remapping and real local SMB when
explicitly enabled. Packaged tests exercise the headless web UI and an actual
older-ZIP profile upgrade. Native-call observation checks attempted DNS, sockets,
UNC accesses and helper processes, not just successful HTTP requests.

Stock Android mobile 2.7.3 and Android TV 0.19.10 have been exercised in a headless
emulator. Mobile audiobook resume and TV video resume passed. **Android TV's folder
audio player restarts at zero on reopen even though the server retains the saved
position.** This is a stock-client limitation, not a claim of automatic audiobook
resume on every client. Physical-device/background playback is not fully verified.
Apple/Swiftfin interface testing remains deferred; API-contract coverage is not
Apple-device validation.

The latest completed checks, package hashes and verification limits are recorded in
[JIGGLEFIN-LIVE-DESIGN.md](JIGGLEFIN-LIVE-DESIGN.md). [JIGGLEFIN.md](JIGGLEFIN.md)
describes the superseded scan-backed implementation, not current behavior.

## Build from source on Windows

Use the .NET 10 SDK and Node.js 24 or later. Run these commands from a checkout of
this development branch (not the older default branch). Build dependencies require
network access; the installed product does not download them at runtime.

```powershell
dotnet restore Jellyfin.sln --locked-mode
dotnet build Jellyfin.sln --configuration Debug --no-restore -m:1
npm ci --prefix Jigglefin.Web --ignore-scripts --no-audit --no-fund
npm run check --prefix Jigglefin.Web
npm run build --prefix Jigglefin.Web
.\scripts\prepare-ffmpeg.ps1 -OutputDirectory publish/ffmpeg-prepared
.\scripts\package-win.ps1 -FfmpegDirectory publish/ffmpeg-prepared
```

Run `dotnet test Jellyfin.sln --configuration Debug --no-build` for source tests.
Set `JIGGLEFIN_TEST_FFMPEG` to the prepared `ffmpeg.exe` to enable native playback
tests. Local SMB tests require explicit `JIGGLEFIN_TEST_LOCAL_SMB=1` and an already
available local administrative share; they do not create shares or change host
services. Package/UI/native-audit scripts and their fixtures are in `scripts/` and
`tests/`. Tests must never write to an owner's real media tree.

The Windows CI workflow targets both `codex/live-filesystem` and `jigglefin`. It
builds and tests the solution, packages the offline UI/runtime/encoder, then runs
the headless packaged-UI and Android web-shell tests. Native outbound observation
and older-ZIP migration also have local scripts; those are separate checks, not
implied by a successful CI build.

## Upstream and licenses

Most server, authentication, streaming and transcoding code remains upstream
[Jellyfin Server](https://github.com/jellyfin/jellyfin). Jigglefin's live directory
layer and API adapters are kept separate where possible; upstream updates still
need regression testing against the offline/read-only contract.

Jigglefin is an independent fork, not an official Jellyfin release. The portable
package includes [Jellyfin FFmpeg](https://github.com/jellyfin/jellyfin-ffmpeg),
the Jigglefin folder UI and locally bundled hls.js, with their license notices.
It does not ship the old Jellyfin Web build.
