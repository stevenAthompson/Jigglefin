[CmdletBinding()]
param(
    [string]$WebDistPath = (Join-Path $PSScriptRoot '../Jigglefin.Web/dist'),

    [Parameter(Mandatory = $true)]
    [string]$FfmpegDirectory,

    [string]$OutputDirectory = 'publish/Jigglefin-win-x64'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$webDist = (Resolve-Path -LiteralPath $WebDistPath -ErrorAction Stop).Path
$ffmpegSource = (Resolve-Path -LiteralPath $FfmpegDirectory -ErrorAction Stop).Path

$manifestPath = Join-Path $webDist 'jigglefin-web.manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Build the offline folder UI first: cd Jigglefin.Web; npm ci --ignore-scripts --no-audit --no-fund; npm run build. The old Jellyfin Web build is not supported.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$webFiles = @('index.html', 'app.css', 'app.js', 'logo.png', 'hls.min.js', 'HLS-LICENSE')
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
    -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) {
    throw "Server publish failed with exit code $LASTEXITCODE"
}

$packagedWeb = New-Item -ItemType Directory -Path (Join-Path $outputPath 'jellyfin-web')
# Copy only manifest-listed files, never stale vendor files or a CDN configuration.
foreach ($webFile in $webFiles + @('jigglefin-web.manifest.json')) {
    Copy-Item -LiteralPath (Join-Path $webDist $webFile) -Destination (Join-Path $packagedWeb.FullName $webFile)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-win.ps1') -Destination (Join-Path $outputPath 'Start-Jigglefin.ps1')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README-PORTABLE.md') -Destination (Join-Path $outputPath 'README-PORTABLE.md')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'branding/jigglefin-256.png') -Destination (Join-Path $outputPath 'Jigglefin-logo.png')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $outputPath 'JIGGLEFIN-LICENSE')
Copy-Item -LiteralPath (Join-Path $webDist 'HLS-LICENSE') -Destination (Join-Path $outputPath 'HLS-LICENSE')
foreach ($ffmpegFile in @('ffmpeg.exe', 'ffprobe.exe', 'FFMPEG-LICENSE.md', 'FFMPEG-COPYING.GPLv3')) {
    Copy-Item -LiteralPath (Join-Path $ffmpegSource $ffmpegFile) -Destination (Join-Path $outputPath $ffmpegFile)
}

Compress-Archive -LiteralPath $outputPath -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host "Windows package: $outputPath"
Write-Host "Windows archive: $archivePath"
