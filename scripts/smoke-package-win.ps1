[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    [ValidateRange(1, 600)]
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

$package = (Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop).Path
$server = Join-Path $package 'jellyfin.exe'
$ffmpeg = Join-Path $package 'ffmpeg.exe'
$web = Join-Path $package 'jellyfin-web'
foreach ($requiredFile in @($server, $ffmpeg, (Join-Path $web 'index.html'), (Join-Path $web 'config.json'))) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "The Windows package is missing $requiredFile"
    }
}

# The package uses Jellyfin's default port. Never mistake an existing local server for this test instance.
if (Get-NetTCPConnection -LocalPort 8096 -State Listen -ErrorAction SilentlyContinue) {
    throw 'Port 8096 is already in use; cannot safely smoke-test the Windows package.'
}

$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$smokeProfile = Join-Path $tempRoot ("jigglefin-package-smoke-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $smokeProfile | Out-Null
$stdout = Join-Path $smokeProfile 'stdout.log'
$stderr = Join-Path $smokeProfile 'stderr.log'
$arguments = @(
    '--datadir', ('"{0}"' -f $smokeProfile),
    '--ffmpeg', ('"{0}"' -f $ffmpeg),
    '--webdir', ('"{0}"' -f $web)
)

$serverProcess = $null
try {
    $serverProcess = Start-Process -FilePath $server -ArgumentList $arguments -WorkingDirectory $package `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru

    $baseUrl = 'http://127.0.0.1:8096'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $publicInfo = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) {
            throw "Packaged server exited during startup with code $($serverProcess.ExitCode)."
        }

        try {
            $candidate = Invoke-RestMethod -Uri "$baseUrl/System/Info/Public" -TimeoutSec 3
            if ($candidate -isnot [string] -and $candidate.Version -and $candidate.Id) {
                $publicInfo = $candidate
                break
            }
        } catch {
            # A fresh profile may briefly serve a setup/status page before the API is ready.
        }
        Start-Sleep -Milliseconds 1000
    }

    if ($null -eq $publicInfo) {
        throw "Packaged server did not answer its public API with a version and ID within $TimeoutSeconds seconds."
    }

    $listeners = @(Get-NetTCPConnection -LocalPort 8096 -State Listen -ErrorAction Stop)
    if (-not ($listeners | Where-Object OwningProcess -EQ $serverProcess.Id)) {
        throw "Port 8096 is not owned by the packaged server process $($serverProcess.Id)."
    }
    $webResponse = Invoke-WebRequest -Uri "$baseUrl/web/index.html" -TimeoutSec 15
    if ($webResponse.StatusCode -ne 200 -or $webResponse.Content -notmatch '<html') {
        throw 'The packaged server did not serve the bundled Jellyfin Web index page.'
    }

    Write-Host "Packaged server smoke test passed: API version $($publicInfo.Version), bundled Web HTTP $($webResponse.StatusCode)."
} catch {
    Write-Warning "Package smoke test failed. Isolated profile and logs: $smokeProfile"
    foreach ($logPath in @($stdout, $stderr)) {
        if (Test-Path -LiteralPath $logPath) {
            Write-Warning "Last lines of $logPath"
            Get-Content -LiteralPath $logPath -Tail 20 | ForEach-Object {
                Write-Warning $_.Substring(0, [Math]::Min(250, $_.Length))
            }
        }
    }
    throw
} finally {
    if ($null -ne $serverProcess) {
        $serverProcess.Refresh()
        if (-not $serverProcess.HasExited) {
            Stop-Process -Id $serverProcess.Id -Force
            Wait-Process -Id $serverProcess.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
    }
}
