[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WebDistPath,

    [Parameter(Mandatory = $true)]
    [string]$FfmpegDirectory,

    [string]$OutputDirectory = 'publish/Jigglefin-win-x64'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$webDist = (Resolve-Path -LiteralPath $WebDistPath -ErrorAction Stop).Path
$webSource = Split-Path -Parent $webDist
$webLicense = Join-Path $webSource 'LICENSE'
$ffmpegSource = (Resolve-Path -LiteralPath $FfmpegDirectory -ErrorAction Stop).Path

foreach ($requiredFile in @('index.html', 'config.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $webDist $requiredFile))) {
        throw "The Jellyfin Web build is missing $requiredFile in $webDist"
    }
}

if (-not (Test-Path -LiteralPath $webLicense)) {
    throw "The Jellyfin Web source license was not found at $webLicense"
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

Copy-Item -LiteralPath $webDist -Destination (Join-Path $outputPath 'jellyfin-web') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-win.ps1') -Destination (Join-Path $outputPath 'Start-Jigglefin.ps1')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README-PORTABLE.md') -Destination (Join-Path $outputPath 'README-PORTABLE.md')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $outputPath 'JIGGLEFIN-LICENSE')
Copy-Item -LiteralPath $webLicense -Destination (Join-Path $outputPath 'JELLYFIN-WEB-LICENSE')
foreach ($ffmpegFile in @('ffmpeg.exe', 'ffprobe.exe', 'FFMPEG-LICENSE.md', 'FFMPEG-COPYING.GPLv3')) {
    Copy-Item -LiteralPath (Join-Path $ffmpegSource $ffmpegFile) -Destination (Join-Path $outputPath $ffmpegFile)
}

Compress-Archive -LiteralPath $outputPath -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host "Windows package: $outputPath"
Write-Host "Windows archive: $archivePath"
