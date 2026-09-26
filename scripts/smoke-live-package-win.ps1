[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$packagePath = (Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop).Path
$repositoryRoot = Split-Path -Parent $PSScriptRoot
foreach ($name in @('jellyfin.exe', 'Start-Jigglefin.ps1', 'ffmpeg.exe', 'ffprobe.exe', 'coreclr.dll', 'JIGGLEFIN-LICENSE', 'HLS-LICENSE', 'jellyfin-web/jigglefin-web.manifest.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packagePath $name) -PathType Leaf)) {
        throw "The self-contained offline package is missing $name"
    }
}
$webPath = Join-Path $packagePath 'jellyfin-web'
$manifest = Get-Content -LiteralPath (Join-Path $webPath 'jigglefin-web.manifest.json') -Raw | ConvertFrom-Json
if ($manifest.name -ne 'Jigglefin folder browser' -or $manifest.offline -ne $true -or
    @($manifest.files.PSObject.Properties).Count -ne 6 -or
    @(Get-ChildItem -LiteralPath $webPath -File -Recurse).Count -ne 7) {
    throw 'The package must contain only the six offline web assets and their manifest.'
}
foreach ($property in $manifest.files.PSObject.Properties) {
    if ($property.Name -notin @('index.html', 'app.css', 'main.jigglefin.bundle.js', 'logo.png', 'hls.min.js', 'HLS-LICENSE') -or
        (Get-FileHash -LiteralPath (Join-Path $webPath $property.Name) -Algorithm SHA256).Hash -ne $property.Value) {
        throw "Packaged web asset verification failed: $($property.Name)"
    }
}

$previousPackage = $env:JIGGLEFIN_TEST_PACKAGE
try {
    $env:JIGGLEFIN_TEST_PACKAGE = $packagePath
    & node (Join-Path $repositoryRoot 'tests/WebClientSmoke/live-folders.cjs')
    if ($LASTEXITCODE -ne 0) { throw "Live folder package test failed with exit code $LASTEXITCODE" }
} finally {
    $env:JIGGLEFIN_TEST_PACKAGE = $previousPackage
}
