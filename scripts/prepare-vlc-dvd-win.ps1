[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw "Output already exists: $root" }
New-Item -ItemType Directory -Path $root | Out-Null
$zip = Join-Path $root 'vlc-3.0.24-win64.zip'
$source = Join-Path $root 'vlc-3.0.24.tar.xz'
Invoke-WebRequest -Uri 'https://download.videolan.org/vlc/3.0.24/win64/vlc-3.0.24-win64.zip' -OutFile $zip
Invoke-WebRequest -Uri 'https://download.videolan.org/vlc/3.0.24/vlc-3.0.24.tar.xz' -OutFile $source
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne 'FCF30850371AD10C9373CC4F0F4501E7DEE49E3E9AE9F20C72FB2661A1CA6323') {
    throw 'VLC portable ZIP did not match the official SHA-256.'
}
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne 'E7CAB503D1D7D5849B89D2CF0E1EE60D0EF6D012407791B644B9CFC0CC225FDF') {
    throw 'VLC source archive did not match the official SHA-256.'
}
Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $root 'extract')
Write-Host "Verified VLC runtime: $(Join-Path $root 'extract/vlc-3.0.24')"
Write-Host "Verified VLC source: $source"
