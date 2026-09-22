[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$FfmpegPath,

    [string]$DataDir,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ServerArguments
)

$ErrorActionPreference = 'Stop'

$server = Join-Path $PSScriptRoot 'jellyfin.exe'
if (-not (Test-Path -LiteralPath $server)) {
    throw "Jigglefin server executable not found at $server"
}

if (-not $FfmpegPath) {
    $bundledFfmpeg = Join-Path $PSScriptRoot 'ffmpeg.exe'
    if (Test-Path -LiteralPath $bundledFfmpeg) {
        $FfmpegPath = $bundledFfmpeg
    }
}

if (-not $FfmpegPath) {
    $wingetPackages = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (Test-Path -LiteralPath $wingetPackages) {
        $FfmpegPath = Get-ChildItem -LiteralPath $wingetPackages -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object FullName -Like '*Jellyfin.FFmpeg*' |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
}

if (-not $FfmpegPath) {
    $ffmpegCommand = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($null -ne $ffmpegCommand) {
        $FfmpegPath = $ffmpegCommand.Source
    }
}

if (-not $FfmpegPath -or -not (Test-Path -LiteralPath $FfmpegPath)) {
    throw 'FFmpeg was not found in the package or on this computer. Pass -FfmpegPath to select one.'
}

$arguments = @('--ffmpeg', $FfmpegPath)
if ($DataDir) {
    $arguments += @('--datadir', $DataDir)
}
$arguments += $ServerArguments

Write-Host "Starting Jigglefin from $server"
Write-Host "Using FFmpeg: $FfmpegPath"
& $server @arguments
exit $LASTEXITCODE
