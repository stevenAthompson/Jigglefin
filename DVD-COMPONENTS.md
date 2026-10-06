# DVD-Video ISO support (Windows portable ZIP)

Jigglefin lists `.iso` files as video and opens a selected image on demand. The DVD
path uses the bundled `dvd-ffprobe.exe` and `dvd-ffmpeg.exe`, built with
libdvdnav/libdvdread. It selects DVD-Video title 1 and transcodes it for Jellyfin
clients; it never scans every ISO or accesses an online metadata service.

The Jigglefin web UI also offers **DVD menu** mode. It starts a private, short-lived
libVLC/libdvdnav worker for the selected ISO, converts its decoded video/audio to a
local HLS stream, and accepts arrow/select/menu commands. Standard Jellyfin playback
requests have no way to send DVD menu navigation events, so standard clients retain
the title-1 fallback and cannot interact with menus. DVD menu mode is a live stream;
unlike title playback, it does not yet save/resume the current position. Disc images
without DVD-Video structure (including Blu-ray ISOs) are not supported. Some
discs place the main feature on a title other than title 1; title selection is
future work. The demuxer accepts local file input only, including the nested
MPEG program stream. No network protocol is permitted for the disc input.

The portable ZIP currently includes `dvdcss-2.dll` for discs that require CSS.
An eventual installer may download it separately instead. DVD decryption laws
vary by jurisdiction; users and distributors must check their local rules.

## Components and source

- DVD-capable FFmpeg: [Gyan Doshi's release-full Windows build](https://www.gyan.dev/ffmpeg/builds/),
  version 9.0.2, static x64 GPLv3. The complete downloaded `.7z` archive had
  SHA-256 `f0e46253c70dfe902bac915dfb4224f0cf1b7c6eeab9da2ccc9a5581f9a71b13`.
  The exact FFmpeg source is [commit 946fcce07b](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b).
  The build's library versions/configuration are in `DVD-FFMPEG-README.txt`, and
  the GPLv3 terms are in `DVD-FFMPEG-COPYING.GPLv3`.
- libdvdcss: [VideoLAN libdvdcss 1.6.0](https://images.videolan.org/developers/libdvdcss.html),
  built from its unmodified source with Meson and MSVC as a Windows DLL. The
  source archive is included as `LIBDVDCSS-SOURCE.tar.xz` (SHA-256
  `7ea556c846b7bfc32d47b41cae56d1863a6b6d5f706bb162778d6f298490977c`),
  with GPLv2 terms in `LIBDVDCSS-COPYING.GPLv2`.
- Interactive menu worker: Jigglefin's `Jigglefin.DvdMenuWorker.exe` uses a restricted
  subset of the official [VideoLAN VLC 3.0.24 Windows portable build](https://download.videolan.org/vlc/3.0.24/win64/).
  The complete official ZIP used to prepare it has SHA-256
  `fcf30850371ad10c9373cc4f0f4501e7dee49e3e9ae9f20c72fb2661a1ca6323`.
  The bundled `vlc` directory omits VLC's network-access, service-discovery,
  graphical-interface, Lua and updater components. The upstream COPYING and README
  are in `vlc`; matching unmodified source is included as
  `VLC-3.0.24-SOURCE.tar.xz` (SHA-256
  `e7cab503d1d7d5849b89d2cf0e1ee60d0ef6d012407791b644b9cfc0cc225fdf`).

The DVD helper is separate from the ordinary Jellyfin FFmpeg. Ordinary video
and audio retain the current encoder path and its input restrictions. The
server profile, temporary transcoding output, and logs must be outside media
folders; Jigglefin treats media locations as read-only.
