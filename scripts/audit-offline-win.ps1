[CmdletBinding()]
param(
    [string]$AuditPython = (Join-Path $PSScriptRoot '../publish/offline-audit-venv/Scripts/python.exe'),
    [string]$PackageDirectory
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$python = (Resolve-Path -LiteralPath $AuditPython).Path
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'
$control = Join-Path $repository 'tests/OfflineNetworkAudit/Control'
$helper = if ($PackageDirectory) { Join-Path (Resolve-Path -LiteralPath $PackageDirectory).Path 'ffprobe.exe' } else { Join-Path $repository 'publish/ffmpeg-prepared-test/ffprobe.exe' }
if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) { throw 'A prepared local FFprobe is required.' }
& $python -c "import frida; assert frida.__version__ == '17.19.0'"
if ($LASTEXITCODE -ne 0) { throw 'Install tests/OfflineNetworkAudit/requirements.txt in an isolated Python environment first.' }
& $dotnet build $control -m:1
if ($LASTEXITCODE -ne 0) { throw 'Network-observer positive control did not build.' }
$fixture = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('jigglefin-network-audit-' + [Guid]::NewGuid().ToString('N')))
$reportPath = Join-Path $fixture.FullName 'positive-control.json'
$previousRoot = $env:DOTNET_ROOT
$previousAudit = $env:JIGGLEFIN_AUDIT_PYTHON
$previousPackage = $env:JIGGLEFIN_TEST_PACKAGE
try {
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    & $python (Join-Path $repository 'tests/OfflineNetworkAudit/trace.py') --report $reportPath -- (Join-Path $control 'bin/Debug/net10.0/Control.exe') $helper
    if ($LASTEXITCODE -ne 0) { throw 'Native positive control failed.' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.errors.Count -or $report.exitCode -ne 0 -or $report.processes.Count -ne 2) { throw 'Native positive-control report was incomplete.' }
    foreach ($kind in @('resolve', 'connect', 'datagram', 'incoming-accept')) {
        if (-not @($report.events | Where-Object kind -EQ $kind).Count) { throw "Observer missed positive control: $kind" }
    }
    $native = @($report.processes | Where-Object executable -EQ $helper)
    if ($native.Count -ne 1 -or -not @($report.events | Where-Object { $_.pid -eq $native[0].pid -and $_.kind -eq 'connect' }).Count) { throw 'Native child connection was not observed.' }
    Write-Host "Managed/native loopback positive controls passed: $reportPath"
    $env:JIGGLEFIN_AUDIT_PYTHON = $python
    if ($PackageDirectory) {
        & (Join-Path $PSScriptRoot 'smoke-live-package-win.ps1') -PackageDirectory $PackageDirectory
    } else {
        $env:JIGGLEFIN_TEST_PACKAGE = $null
        & $dotnet build (Join-Path $repository 'Jellyfin.Server') --no-restore -m:1
        if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
        & node (Join-Path $repository 'Jigglefin.Web/build.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Offline web build failed.' }
        & node (Join-Path $repository 'tests/WebClientSmoke/live-folders.cjs')
        if ($LASTEXITCODE -ne 0) { throw 'Native/server/browser offline audit failed. Inspect the retained fixture reports.' }
    }
} finally {
    $env:DOTNET_ROOT = $previousRoot
    $env:JIGGLEFIN_AUDIT_PYTHON = $previousAudit
    $env:JIGGLEFIN_TEST_PACKAGE = $previousPackage
}
