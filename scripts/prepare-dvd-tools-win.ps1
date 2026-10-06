[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$FfmpegArchivePath,

    [string]$LibdvdcssArchivePath
)

$ErrorActionPreference = 'Stop'
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    throw "Output already exists: $outputPath. Choose a new -OutputDirectory."
}

$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
if (-not (Test-Path -LiteralPath $sevenZip -PathType Leaf)) {
    throw '7-Zip is required to prepare the verified DVD-capable FFmpeg build.'
}

New-Item -ItemType Directory -Path $outputPath | Out-Null
$ffmpegArchive = if ($FfmpegArchivePath) {
    (Resolve-Path -LiteralPath $FfmpegArchivePath -ErrorAction Stop).Path
} else {
    $download = Join-Path $outputPath 'ffmpeg-release-full.7z'
    & curl.exe -L --fail --retry 3 --retry-all-errors --connect-timeout 20 --max-time 1800 --silent --show-error `
        -o $download 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.7z'
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the DVD-capable FFmpeg archive.' }
    $download
}

$ffmpegHash = (Get-FileHash -LiteralPath $ffmpegArchive -Algorithm SHA256).Hash
if ($ffmpegHash -ne 'f0e46253c70dfe902bac915dfb4224f0cf1b7c6eeab9da2ccc9a5581f9a71b13') {
    throw "DVD-capable FFmpeg archive SHA256 mismatch: $ffmpegHash"
}

$extractPath = Join-Path $outputPath 'extract'
New-Item -ItemType Directory -Path $extractPath | Out-Null
& $sevenZip e $ffmpegArchive `
    'ffmpeg-9.0.2-full_build\bin\ffmpeg.exe' `
    'ffmpeg-9.0.2-full_build\bin\ffprobe.exe' `
    'ffmpeg-9.0.2-full_build\README.txt' `
    'ffmpeg-9.0.2-full_build\LICENSE' `
    "-o$extractPath" -y
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the verified DVD-capable FFmpeg build.' }

foreach ($file in @('ffmpeg.exe', 'ffprobe.exe', 'README.txt', 'LICENSE')) {
    if (-not (Test-Path -LiteralPath (Join-Path $extractPath $file) -PathType Leaf)) {
        throw "The verified DVD-capable FFmpeg archive is missing $file"
    }
}

Copy-Item -LiteralPath (Join-Path $extractPath 'ffmpeg.exe') -Destination (Join-Path $outputPath 'dvd-ffmpeg.exe')
Copy-Item -LiteralPath (Join-Path $extractPath 'ffprobe.exe') -Destination (Join-Path $outputPath 'dvd-ffprobe.exe')
Copy-Item -LiteralPath (Join-Path $extractPath 'README.txt') -Destination (Join-Path $outputPath 'DVD-FFMPEG-README.txt')
Copy-Item -LiteralPath (Join-Path $extractPath 'LICENSE') -Destination (Join-Path $outputPath 'DVD-FFMPEG-COPYING.GPLv3')

$cssArchive = if ($LibdvdcssArchivePath) {
    (Resolve-Path -LiteralPath $LibdvdcssArchivePath -ErrorAction Stop).Path
} else {
    $download = Join-Path $outputPath 'libdvdcss-1.6.0.tar.xz'
    & curl.exe -L --fail --retry 3 --retry-all-errors --connect-timeout 20 --max-time 300 --silent --show-error `
        -o $download 'https://download.videolan.org/libdvdcss/1.6.0/libdvdcss-1.6.0.tar.xz'
    if ($LASTEXITCODE -ne 0) {
        & curl.exe -L --fail --retry 3 --retry-all-errors --connect-timeout 20 --max-time 300 --silent --show-error `
            -o $download 'https://ftp.fau.de/videolan/libdvdcss/1.6.0/libdvdcss-1.6.0.tar.xz'
        if ($LASTEXITCODE -ne 0) { throw 'Could not download the libdvdcss source archive.' }
    }

    $download
}

$cssHash = (Get-FileHash -LiteralPath $cssArchive -Algorithm SHA256).Hash
if ($cssHash -ne '7ea556c846b7bfc32d47b41cae56d1863a6b6d5f706bb162778d6f298490977c') {
    throw "libdvdcss source archive SHA256 mismatch: $cssHash"
}

Copy-Item -LiteralPath $cssArchive -Destination (Join-Path $outputPath 'LIBDVDCSS-SOURCE.tar.xz')
& tar.exe -xf $cssArchive -C $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the verified libdvdcss source.' }
$cssSource = Join-Path $outputPath 'libdvdcss-1.6.0'
Copy-Item -LiteralPath (Join-Path $cssSource 'COPYING') -Destination (Join-Path $outputPath 'LIBDVDCSS-COPYING.GPLv2')

& py.exe -3 -m venv (Join-Path $outputPath 'venv')
if ($LASTEXITCODE -ne 0) { throw 'Python 3 is required to prepare the libdvdcss build.' }
$venvScripts = Join-Path $outputPath 'venv\Scripts'
& (Join-Path $venvScripts 'python.exe') -m pip install --disable-pip-version-check 'meson==1.9.2' 'ninja==1.13.2'
if ($LASTEXITCODE -ne 0) { throw 'Could not install the isolated libdvdcss build tools.' }

$priorPath = $env:PATH
try {
    $env:PATH = "$venvScripts;$priorPath"
    $cssBuild = Join-Path $outputPath 'libdvdcss-build'
    & (Join-Path $venvScripts 'meson.exe') setup $cssBuild $cssSource --vsenv --backend=ninja -Ddefault_library=shared -Dbuildtype=release
    if ($LASTEXITCODE -ne 0) { throw 'libdvdcss configuration failed.' }
    & (Join-Path $venvScripts 'meson.exe') compile -C $cssBuild
    if ($LASTEXITCODE -ne 0) { throw 'libdvdcss compilation failed.' }
} finally {
    $env:PATH = $priorPath
}

Copy-Item -LiteralPath (Join-Path $cssBuild 'src\dvdcss-2.dll') -Destination (Join-Path $outputPath 'dvdcss-2.dll')
$demuxers = & (Join-Path $outputPath 'dvd-ffmpeg.exe') -hide_banner -demuxers 2>&1
if ($LASTEXITCODE -ne 0 -or -not ($demuxers -match 'dvdvideo')) {
    throw 'The verified FFmpeg build lacks the dvdvideo demuxer.'
}

Write-Host "Prepared DVD helpers in $outputPath"
