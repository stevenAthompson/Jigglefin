# DVD-Video ISO support (Windows portable ZIP)

Jigglefin lists `.iso` files as video and opens a selected image on demand. The DVD
path uses the bundled `dvd-ffprobe.exe` and `dvd-ffmpeg.exe`, built with
libdvdnav/libdvdread. It selects DVD-Video title 1 and transcodes it for Jellyfin
clients; it never scans every ISO or accesses an online metadata service.

This is **title playback, not interactive DVD menus**. Standard Jellyfin playback
requests have no way to send DVD menu navigation events to libdvdnav. Disc images
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

The DVD helper is separate from the ordinary Jellyfin FFmpeg. Ordinary video
and audio retain the current encoder path and its input restrictions. The
server profile, temporary transcoding output, and logs must be outside media
folders; Jigglefin treats media locations as read-only.
