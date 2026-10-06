[CmdletBinding()]
param(
    [string]$WebDistPath,

    [Parameter(Mandatory = $true)]
    [string]$FfmpegDirectory,

    [Parameter(Mandatory = $true)]
    [string]$DvdToolsDirectory,

    [Parameter(Mandatory = $true)]
    [string]$VlcDirectory,

    [Parameter(Mandatory = $true)]
    [string]$VlcSourceArchive,

    [string]$OutputDirectory = 'publish/Jigglefin-win-x64'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $WebDistPath) { $WebDistPath = Join-Path $repositoryRoot 'Jigglefin.Web/dist' }
$webDist = (Resolve-Path -LiteralPath $WebDistPath -ErrorAction Stop).Path
$ffmpegSource = (Resolve-Path -LiteralPath $FfmpegDirectory -ErrorAction Stop).Path
$dvdToolsSource = (Resolve-Path -LiteralPath $DvdToolsDirectory -ErrorAction Stop).Path
$vlcSource = (Resolve-Path -LiteralPath $VlcDirectory -ErrorAction Stop).Path
$vlcSourceArchivePath = (Resolve-Path -LiteralPath $VlcSourceArchive -ErrorAction Stop).Path

$manifestPath = Join-Path $webDist 'jigglefin-web.manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Build the offline folder UI first: cd Jigglefin.Web; npm ci --ignore-scripts --no-audit --no-fund; npm run build. The old Jellyfin Web build is not supported.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$webFiles = @('index.html', 'app.css', 'main.jigglefin.bundle.js', 'logo.png', 'hls.min.js', 'HLS-LICENSE', 'wallpapers/blue-current.svg', 'wallpapers/bloom-blueprint.svg', 'wallpapers/garden-lines.svg')
if ($manifest.name -ne 'Jigglefin folder browser' -or $manifest.offline -ne $true -or
    @($manifest.files.PSObject.Properties).Count -ne $webFiles.Count) {
    throw 'The web manifest is not a supported offline Jigglefin folder build.'
}
foreach ($requiredFile in $webFiles) {
    $asset = Join-Path $webDist $requiredFile
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf) -or
        (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash -ne $manifest.files.$requiredFile) {
        throw "The offline web asset is missing or changed: $requiredFile. Rebuild Jigglefin.Web."
    }
}

foreach ($requiredFile in @('ffmpeg.exe', 'ffprobe.exe', 'FFMPEG-LICENSE.md', 'FFMPEG-COPYING.GPLv3')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ffmpegSource $requiredFile))) {
        throw "The prepared Jellyfin FFmpeg directory is missing $requiredFile in $ffmpegSource"
    }
}
foreach ($requiredFile in @('dvd-ffmpeg.exe', 'dvd-ffprobe.exe', 'dvdcss-2.dll', 'DVD-FFMPEG-COPYING.GPLv3', 'DVD-FFMPEG-README.txt', 'LIBDVDCSS-COPYING.GPLv2', 'LIBDVDCSS-SOURCE.tar.xz')) {
    if (-not (Test-Path -LiteralPath (Join-Path $dvdToolsSource $requiredFile) -PathType Leaf)) {
        throw "The prepared DVD tools directory is missing $requiredFile in $dvdToolsSource"
    }
}
$vlcDirectories = @('audio_filter','audio_mixer','audio_output','codec','demux','packetizer','spu','stream_filter','text_renderer','video_chroma','video_filter','video_output')
$vlcAccessFiles = @('libdvdnav_plugin.dll','libdvdread_plugin.dll','libfilesystem_plugin.dll')
foreach ($requiredFile in @('libvlc.dll','libvlccore.dll','COPYING.txt','README.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $vlcSource $requiredFile) -PathType Leaf)) { throw "The VLC runtime is missing $requiredFile" }
}
foreach ($requiredFile in $vlcAccessFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $vlcSource "plugins/access/$requiredFile") -PathType Leaf)) { throw "The VLC runtime is missing $requiredFile" }
}
foreach ($vlcDirectory in $vlcDirectories) {
    if (-not (Test-Path -LiteralPath (Join-Path $vlcSource "plugins/$vlcDirectory") -PathType Container)) { throw "The VLC runtime is missing $vlcDirectory" }
}
if ((Get-FileHash -LiteralPath $vlcSourceArchivePath -Algorithm SHA256).Hash -ne 'E7CAB503D1D7D5849B89D2CF0E1EE60D0EF6D012407791B644B9CFC0CC225FDF') {
    throw 'The VLC 3.0.24 source archive does not match VideoLAN SHA-256.'
}

$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$archivePath = "$outputPath.zip"

if ((Test-Path -LiteralPath $outputPath) -or (Test-Path -LiteralPath $archivePath)) {
    throw "Output already exists: $outputPath or $archivePath. Choose a new -OutputDirectory."
}

$localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

New-Item -ItemType Directory -Path $outputPath | Out-Null

& $dotnet publish (Join-Path $repositoryRoot 'Jellyfin.Server/Jellyfin.Server.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $outputPath `
    -m:1 `
    -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) {
    throw "Server publish failed with exit code $LASTEXITCODE"
}
& $dotnet publish (Join-Path $repositoryRoot 'Jigglefin.DvdMenuWorker/Jigglefin.DvdMenuWorker.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $outputPath `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) {
    throw "DVD menu worker publish failed with exit code $LASTEXITCODE"
}

$packagedWeb = New-Item -ItemType Directory -Path (Join-Path $outputPath 'jellyfin-web')
# Copy only manifest-listed files, never stale vendor files or a CDN configuration.
foreach ($webFile in $webFiles + @('jigglefin-web.manifest.json')) {
    $destination = Join-Path $packagedWeb.FullName $webFile
    $parent = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
    Copy-Item -LiteralPath (Join-Path $webDist $webFile) -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-win.ps1') -Destination (Join-Path $outputPath 'Start-Jigglefin.ps1')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README-PORTABLE.md') -Destination (Join-Path $outputPath 'README-PORTABLE.md')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'JIGGLEFIN-LIVE-DESIGN.md') -Destination (Join-Path $outputPath 'JIGGLEFIN-LIVE-DESIGN.md')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'branding/jigglefin-256.png') -Destination (Join-Path $outputPath 'Jigglefin-logo.png')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $outputPath 'JIGGLEFIN-LICENSE')
Copy-Item -LiteralPath (Join-Path $webDist 'HLS-LICENSE') -Destination (Join-Path $outputPath 'HLS-LICENSE')
foreach ($ffmpegFile in @('ffmpeg.exe', 'ffprobe.exe', 'FFMPEG-LICENSE.md', 'FFMPEG-COPYING.GPLv3')) {
    Copy-Item -LiteralPath (Join-Path $ffmpegSource $ffmpegFile) -Destination (Join-Path $outputPath $ffmpegFile)
}
foreach ($dvdFile in @('dvd-ffmpeg.exe', 'dvd-ffprobe.exe', 'dvdcss-2.dll', 'DVD-FFMPEG-COPYING.GPLv3', 'DVD-FFMPEG-README.txt', 'LIBDVDCSS-COPYING.GPLv2', 'LIBDVDCSS-SOURCE.tar.xz')) {
    Copy-Item -LiteralPath (Join-Path $dvdToolsSource $dvdFile) -Destination (Join-Path $outputPath $dvdFile)
}
$packagedVlc = New-Item -ItemType Directory -Path (Join-Path $outputPath 'vlc')
$packagedVlcPlugins = New-Item -ItemType Directory -Path (Join-Path $packagedVlc.FullName 'plugins')
foreach ($vlcFile in @('libvlc.dll','libvlccore.dll','COPYING.txt','README.txt')) {
    Copy-Item -LiteralPath (Join-Path $vlcSource $vlcFile) -Destination (Join-Path $packagedVlc.FullName $vlcFile)
}
foreach ($vlcDirectory in $vlcDirectories) {
    Copy-Item -LiteralPath (Join-Path $vlcSource "plugins/$vlcDirectory") -Destination $packagedVlcPlugins.FullName -Recurse
}
$packagedVlcAccess = New-Item -ItemType Directory -Path (Join-Path $packagedVlcPlugins.FullName 'access')
foreach ($vlcFile in $vlcAccessFiles) {
    Copy-Item -LiteralPath (Join-Path $vlcSource "plugins/access/$vlcFile") -Destination (Join-Path $packagedVlcAccess.FullName $vlcFile)
}
Copy-Item -LiteralPath (Join-Path $dvdToolsSource 'dvdcss-2.dll') -Destination (Join-Path $packagedVlc.FullName 'dvdcss-2.dll')
Copy-Item -LiteralPath $vlcSourceArchivePath -Destination (Join-Path $outputPath 'VLC-3.0.24-SOURCE.tar.xz')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'DVD-COMPONENTS.md') -Destination (Join-Path $outputPath 'DVD-COMPONENTS.md')

Compress-Archive -LiteralPath $outputPath -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host "Windows package: $outputPath"
Write-Host "Windows archive: $archivePath"
