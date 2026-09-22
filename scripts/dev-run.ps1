[CmdletBinding()]
param(
    [switch]$NoBuild,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ServerArguments
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

$wingetPackages = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
$jellyfinFfmpeg = Get-ChildItem -LiteralPath $wingetPackages -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object FullName -Like '*Jellyfin.FFmpeg*' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

$ffmpeg = if ($null -ne $jellyfinFfmpeg) {
    $jellyfinFfmpeg.FullName
} else {
    (Get-Command ffmpeg -ErrorAction Stop).Source
}

$arguments = @(
    'run'
    '--project'
    (Join-Path $repositoryRoot 'Jellyfin.Server')
    '--configuration'
    'Debug'
)

if ($NoBuild) {
    $arguments += '--no-build'
}

$arguments += @(
    '--'
    '--nowebclient'
    '--ffmpeg'
    $ffmpeg
)
$arguments += $ServerArguments

Write-Host "Using .NET: $dotnet"
Write-Host "Using FFmpeg: $ffmpeg"

Push-Location $repositoryRoot
try {
    & $dotnet @arguments
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
