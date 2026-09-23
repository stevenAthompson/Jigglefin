# Jigglefin artwork

The original artwork is [`Jigglefin.svg`](../Jigglefin.svg). Keep that source unchanged; the PNGs
and Windows icon in this directory are derived exports. The source artwork includes its light
background, so the smaller images do too.

To regenerate the PNGs on Windows with Inkscape:

```powershell
foreach ($size in 32, 64, 128, 256, 512) {
    inkscape .\Jigglefin.svg --export-type=png --export-width=$size --export-filename="branding/jigglefin-$size.png"
}
```

Use FFmpeg to combine the icon sizes into a Windows icon:

```powershell
ffmpeg -y -i branding/jigglefin-32.png -i branding/jigglefin-64.png -i branding/jigglefin-128.png -i branding/jigglefin-256.png -map 0:v -map 1:v -map 2:v -map 3:v -c:v png -f ico branding/Jigglefin.ico
```

The 256-pixel PNG appears in the repository README and portable package. The ICO is compiled
into the Windows server executable. The bundled Jellyfin Web assets remain unmodified.
