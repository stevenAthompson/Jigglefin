[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'

$version = '8.1.2-5'
$archiveName = "jellyfin-ffmpeg_$($version)_portable_win64-clang-gpl.zip"
$expectedHash = '4bb79f3d1e4092404eafe3fb3b0101e196f8eb00355201f60314e5ba21447b82'
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    throw "Output already exists: $outputPath. Choose a new -OutputDirectory."
}

New-Item -ItemType Directory -Path $outputPath | Out-Null
$downloadedArchive = $null
if ($ArchivePath) {
    $archive = (Resolve-Path -LiteralPath $ArchivePath -ErrorAction Stop).Path
} else {
    $downloadedArchive = Join-Path $outputPath $archiveName
    Invoke-WebRequest -Uri "https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v$version/$archiveName" -OutFile $downloadedArchive
    $archive = $downloadedArchive
}

$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) {
    throw "FFmpeg archive SHA256 mismatch: $actualHash"
}

Expand-Archive -LiteralPath $archive -DestinationPath $outputPath
foreach ($executable in @('ffmpeg.exe', 'ffprobe.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath $executable))) {
        throw "The verified FFmpeg archive is missing $executable"
    }
}

$sourceRoot = "https://raw.githubusercontent.com/jellyfin/jellyfin-ffmpeg/v$version"
Invoke-WebRequest -Uri "$sourceRoot/LICENSE.md" -OutFile (Join-Path $outputPath 'FFMPEG-LICENSE.md')
Invoke-WebRequest -Uri "$sourceRoot/COPYING.GPLv3" -OutFile (Join-Path $outputPath 'FFMPEG-COPYING.GPLv3')

if ($downloadedArchive) {
    Remove-Item -LiteralPath $downloadedArchive
}

Write-Host "Prepared Jellyfin FFmpeg $version in $outputPath"
