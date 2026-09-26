[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineAssembly,
    [Parameter(Mandatory)][string]$CurrentAssembly,
    [string]$AuditPython = (Join-Path $PSScriptRoot '../publish/offline-audit-venv/Scripts/python.exe')
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$baseline = (Resolve-Path -LiteralPath $BaselineAssembly).Path
$current = (Resolve-Path -LiteralPath $CurrentAssembly).Path
$python = (Resolve-Path -LiteralPath $AuditPython).Path
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'
$control = Join-Path $repository 'tests/OfflineNetworkAudit/Control'
& $dotnet build $control -m:1
if ($LASTEXITCODE -ne 0) { throw 'Normalization control build failed.' }
$fixture = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('jigglefin-network-audit-normalize-' + [Guid]::NewGuid().ToString('N')))
$previousRuntime = $env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    foreach ($phase in @('before', 'after')) {
        $assembly = if ($phase -eq 'before') { $baseline } else { $current }
        $mode = if ($phase -eq 'before') { '--normalize-expanding' } else { '--normalize-lexical' }
        $reportFile = Join-Path $fixture.FullName ($phase + '.json')
        & $python (Join-Path $repository 'tests/OfflineNetworkAudit/trace.py') --report $reportFile -- (Join-Path $control 'bin/Debug/net10.0/Control.exe') $mode $assembly
        if ($LASTEXITCODE -ne 0) { throw "$phase normalization control failed." }
        $report = Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json
        if ($report.exitCode -ne 0 -or $report.errors.Count -or $report.forcedTerminations.Count -or $report.processes.Count -ne 1 -or -not @($report.events | Where-Object kind -EQ 'ready').Count) { throw "$phase normalization observation was incomplete." }
        $expansions = @($report.events | Where-Object kind -EQ 'path-expansion')
        if ($phase -eq 'before' -and $expansions.Count -lt 1) { throw 'The old implementation did not exercise the native filesystem expansion; this is not a positive control.' }
        if ($phase -eq 'after' -and $expansions.Count -ne 0) { throw 'Configuration normalization still expands filesystem short names.' }
        if (@($report.events | Where-Object { $_.kind -in @('connect','connect-name','datagram','resolve','http','unc-file') }).Count) { throw 'Unexpected network access in the local normalization control.' }
        Write-Host "$phase normalization: $($expansions.Count) native expansion calls."
    }
    Write-Host "PASS. Native normalization evidence: $($fixture.FullName)"
} finally {
    $env:DOTNET_ROOT = $previousRuntime
}
