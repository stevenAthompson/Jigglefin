[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OldArchive,
    [Parameter(Mandatory = $true)][string]$NewArchive,
    [string]$ExpectedOldSha256 = 'B8380C4BF071690B7F87113D54D966DFE13EC87E2FD97E6A55791A78DCE7F630'
)
$ErrorActionPreference = 'Stop'
$oldZip = (Resolve-Path -LiteralPath $OldArchive).Path
$newZip = (Resolve-Path -LiteralPath $NewArchive).Path
if ((Get-FileHash -LiteralPath $oldZip -Algorithm SHA256).Hash -ne $ExpectedOldSha256) { throw 'The older release archive did not match its expected checksum.' }
$fixture = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('jigglefin-zip-upgrade-' + [Guid]::NewGuid().ToString('N')))
function Expand-TestPackage([string]$archive, [string]$destination) {
    Expand-Archive -LiteralPath $archive -DestinationPath $destination
    $servers = @(Get-ChildItem -LiteralPath $destination -Filter jellyfin.exe -File -Recurse)
    if ($servers.Count -ne 1) { throw 'Expected exactly one packaged server.' }
    $packagePath = $servers[0].Directory.FullName
    foreach ($name in @('Start-Jigglefin.ps1', 'coreclr.dll', 'ffmpeg.exe', 'ffprobe.exe', 'jellyfin-web/index.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $packagePath $name) -PathType Leaf)) { throw "Missing packaged file: $name" }
    }
    return $packagePath
}
$oldPackage = Expand-TestPackage $oldZip (Join-Path $fixture.FullName 'Old package')
$newPackage = Expand-TestPackage $newZip (Join-Path $fixture.FullName 'New package')
$previous = @{}
foreach ($key in @('JIGGLEFIN_UPGRADE_FIXTURE', 'JIGGLEFIN_OLD_PACKAGE', 'JIGGLEFIN_NEW_PACKAGE', 'JIGGLEFIN_OLD_ARCHIVE_SHA256', 'JIGGLEFIN_NEW_ARCHIVE_SHA256')) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
try {
    $env:JIGGLEFIN_UPGRADE_FIXTURE = $fixture.FullName
    $env:JIGGLEFIN_OLD_PACKAGE = $oldPackage
    $env:JIGGLEFIN_NEW_PACKAGE = $newPackage
    $env:JIGGLEFIN_OLD_ARCHIVE_SHA256 = (Get-FileHash -LiteralPath $oldZip -Algorithm SHA256).Hash
    $env:JIGGLEFIN_NEW_ARCHIVE_SHA256 = (Get-FileHash -LiteralPath $newZip -Algorithm SHA256).Hash
    & node (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/WebClientSmoke/live-upgrade.cjs')
    if ($LASTEXITCODE -ne 0) { throw "Packaged upgrade test failed ($LASTEXITCODE). Isolated fixture: $($fixture.FullName)" }
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
}
