# Jigglefin artwork

The source artwork is [`Jigglefin.svg`](../Jigglefin.svg). The PNGs and Windows icon in this
directory are derived exports. The mark uses a simple fin and waves, with no text or character
details, so it stays legible at small icon sizes.

To regenerate the PNGs on Windows with Inkscape, use its console executable so each export
finishes before the icon is assembled:

```powershell
foreach ($size in 32, 64, 128, 256, 512) {
    & 'C:\Program Files\Inkscape\bin\inkscape.com' .\Jigglefin.svg --export-type=png --export-width=$size --export-filename="branding/jigglefin-$size.png"
}
```

Use FFmpeg to combine the icon sizes into a Windows icon:

```powershell
ffmpeg -y -i branding/jigglefin-32.png -i branding/jigglefin-64.png -i branding/jigglefin-128.png -i branding/jigglefin-256.png -map 0:v -map 1:v -map 2:v -map 3:v -c:v png -f ico branding/Jigglefin.ico
```

The 256-pixel PNG appears in the repository README and portable package. The ICO is compiled
into the Windows server executable. The bundled Jellyfin Web assets remain unmodified.
