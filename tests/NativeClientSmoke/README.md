# Headless stock Android client checks

Development-only Windows fixture. It creates synthetic media, a private server
profile, a new Android virtual device, a separate loopback ADB server and random
server/control ports under `%TEMP%`. It does not reuse the user's AVD, default ADB
server, installed Jigglefin profile, desktop input, or `Z:\Media`.

Requirements: Node 25+, the Windows Android SDK (`platform-tools`, `emulator`, and
the rootable `system-images;android-34;google_apis;x86_64` image), and a built Windows
Jigglefin package. `JIGGLEFIN_TEST_PACKAGE` is required so a stale package cannot
silently become the test target. Set `JIGGLEFIN_ANDROID_SDK` to override the SDK
location. Android CLI references: [emulator](https://developer.android.com/studio/run/emulator-commandline),
[ADB](https://developer.android.com/tools/adb).

Download the **unmodified official release** APKs into the paths pinned in
`fixture.cjs`. The harness checks their SHA-256 against the release asset digests:

- [Android TV 0.19.10](https://github.com/jellyfin/jellyfin-androidtv/releases/tag/v0.19.10), `jellyfin-androidtv-v0.19.10-release.apk`.
- [Android 2.7.3](https://github.com/jellyfin/jellyfin-android/releases/tag/v2.7.3), `jellyfin-android-v2.7.3-libre-release.apk`.

```powershell
$env:JIGGLEFIN_TEST_PACKAGE = 'D:\Jigglefin\publish\<package>'
node tests/NativeClientSmoke/fixture.cjs
# In another CLI, use only the freshly printed owned fixture path:
node tests/NativeClientSmoke/android-tv.cjs '<fixture>'
node tests/NativeClientSmoke/android-mobile.cjs '<fixture>'
node tests/NativeClientSmoke/control.cjs '<fixture>' stop
```

`android-tv.cjs` tests normal login, empty/nested physical folders, real audio
progress and pause, app restart, local NFO details, video playback, stopped
bookmarks and immediate Resume. It records screenshots, guest crash output and a
JSON report. `--signed-in` allows retrying
the browse/play section on the same owned guest. A recorded client limitation is
not a successful assertion of that feature: TV folder audio currently starts at
zero on reopening despite a saved bookmark.

`android-mobile.cjs` checks the official phone app's embedded WebView using the
bundled folder UI: connection/login, empty/nested folders, hardware Back, local NFO
details, real audiobook progress and resume after app restart, and direct video
with subtitles Off. It does not establish ExoPlayer integration, background audio,
or a native Android media session. The bootstrap bundle name matches the stock
shell's ready signal; scripts are scoped to avoid its injected globals.

`ui.cjs`/`control.cjs` support targeted guest UI inspection and diagnosis. Run only
one UI driver at a time. TV card touches can either focus or activate, so the driver
observes navigation before pressing Enter. Avoid UiAutomator's idle wait during
moving video; observe screenshots and **changed durable user-data positions**
instead. Session `PlayState.PositionTicks` alone is not playback proof: the server
can extrapolate it while a client buffers. The paused/saved assertions require
actual client reports. Passwords
are randomly generated and typed through the authenticated local controller, not
printed on the command line. Treat the retained fixture/profile as private test
data; do not commit its credentials, databases or APKs.

The emulator is windowless and audio output is disabled. Client internet access is
denied inside the **owned Android guest** after boot; ADB reverse connects to the
local test server. No host firewall, existing process or other device is changed.
This is a native-client compatibility test, not an assertion that the stock client
itself has no internet features. Server/native-helper outbound auditing is separate
in `../OfflineNetworkAudit`.

Always request `stop` and inspect `cleanup-report.json`: synthetic media hashes and
mtimes must be unchanged, with no cleanup errors. The fixture has bounded startup
and idle lifetimes and reports forced cleanup as a failure. CLI/controller crashes
and host failure are not a substitute for a verified clean shutdown. Check owned
process identities before any manual cleanup; never kill every ADB/emulator/server
process by name. Physical Android devices and Apple clients remain separate gates.

The current fixture generates ordinary 320x180 H.264/AAC video. The old 32x32
integration clip loop buffered in the guest decoder and was not accepted as a
native playback pass. For diagnosis of a pre-existing owned fixture only,
`prepare-video-control.cjs '<fixture>'` creates a separate synthetic group;
`android-tv.cjs '<fixture>' --signed-in --video-control` selects it. Always run
`prepare-video-control.cjs '<fixture>' --verify` before stopping: this additional
group has its own hash/mtime snapshot. It never replaces existing fixture media.

`proxy.cjs` is an optional ten-minute loopback diagnostic relay. It changes only
the owned guest's ADB reverse route, prints header names/auth-presence booleans,
redacts credential query parameters, and restores that route on normal expiry.
It is not used for acceptance runs and does not implement WebSockets. Stop the
owned fixture after interrupted diagnostics; do not interpret relay behavior as
unmodified direct-client behavior.
