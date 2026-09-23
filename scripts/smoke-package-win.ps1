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
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sampleVideo = Join-Path $repositoryRoot 'tests/Jellyfin.Server.Integration.Tests/Test Data/JigglefinSample.mp4'
foreach ($requiredFile in @($server, $ffmpeg, (Join-Path $web 'index.html'), (Join-Path $web 'config.json'))) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "The Windows package is missing $requiredFile"
    }
}
if (-not (Test-Path -LiteralPath $sampleVideo -PathType Leaf)) {
    throw "The package smoke sample is missing: $sampleVideo"
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
$smokeSucceeded = $false
try {
    $serverProcess = Start-Process -FilePath $server -ArgumentList $arguments -WorkingDirectory $package `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru

    $baseUrl = 'http://127.0.0.1:8096'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $publicInfo = $null
    $webResponse = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) {
            throw "Packaged server exited during startup with code $($serverProcess.ExitCode)."
        }

        try {
            $candidate = Invoke-RestMethod -Uri "$baseUrl/System/Info/Public" -TimeoutSec 3
            if ($candidate -isnot [string] -and $candidate.Version -and $candidate.Id) {
                # Jellyfin's temporary setup server exposes this API before the real web app is ready.
                $candidateWeb = Invoke-WebRequest -Uri "$baseUrl/web/index.html" -TimeoutSec 3
                if ($candidateWeb.StatusCode -eq 200 -and $candidateWeb.Content -match '<html') {
                    $publicInfo = $candidate
                    $webResponse = $candidateWeb
                    break
                }
            }
        } catch {
            # Startup temporarily returns 503 HTML from /web and may briefly rebind the port.
        }
        Start-Sleep -Milliseconds 1000
    }

    if ($null -eq $publicInfo) {
        throw "Packaged server did not serve both its public API and bundled Web within $TimeoutSeconds seconds."
    }

    $listeners = @(Get-NetTCPConnection -LocalPort 8096 -State Listen -ErrorAction Stop)
    if (-not ($listeners | Where-Object OwningProcess -EQ $serverProcess.Id)) {
        throw "Port 8096 is not owned by the packaged server process $($serverProcess.Id)."
    }
    if ($publicInfo.StartupWizardCompleted -ne $false) {
        throw 'The smoke test did not start with a fresh, unconfigured server profile.'
    }

    # Complete the first-run wizard only in this newly generated, isolated profile.
    $temporaryPassword = 'Tmp-' + [Guid]::NewGuid().ToString('N') + '!1'
    $userName = 'JigglefinSmoke'
    $firstUser = Invoke-RestMethod -Uri "$baseUrl/Startup/User" -TimeoutSec 15
    if (-not $firstUser.Name) {
        throw 'The packaged server did not initialize its first startup user.'
    }
    $userPayload = @{ Name = $userName; Password = $temporaryPassword } | ConvertTo-Json -Compress
    Invoke-WebRequest -Uri "$baseUrl/Startup/User" -Method Post -ContentType 'application/json' `
        -Body $userPayload -TimeoutSec 15 | Out-Null
    Invoke-WebRequest -Uri "$baseUrl/Startup/Complete" -Method Post -Body '' -TimeoutSec 15 | Out-Null

    $deviceId = [Guid]::NewGuid().ToString('N')
    $clientHeader = 'MediaBrowser Client="Jigglefin%20Package%20Smoke", DeviceId="' + $deviceId + '", Device="Windows", Version="13.0.0"'
    $authPayload = @{ Username = $userName; Pw = $temporaryPassword } | ConvertTo-Json -Compress
    $authentication = Invoke-RestMethod -Uri "$baseUrl/Users/AuthenticateByName" -Method Post `
        -Headers @{ Authorization = $clientHeader } -ContentType 'application/json' -Body $authPayload -TimeoutSec 15
    if (-not $authentication.AccessToken) {
        throw 'The packaged server did not issue an access token for its temporary smoke-test user.'
    }
    $authenticatedHeaders = @{ Authorization = "$clientHeader, Token=$($authentication.AccessToken)" }
    $me = Invoke-RestMethod -Uri "$baseUrl/Users/Me" -Headers $authenticatedHeaders -TimeoutSec 15
    if ($me.Name -ne $userName) {
        throw 'The authenticated user endpoint did not return the temporary smoke-test user.'
    }

    $mediaRoot = Join-Path $smokeProfile 'media'
    $movieDirectory = Join-Path $mediaRoot 'Action/Smoke Film (2026)'
    New-Item -ItemType Directory -Path $movieDirectory | Out-Null
    Copy-Item -LiteralPath $sampleVideo -Destination (Join-Path $movieDirectory 'Smoke Film (2026).mp4')
    [System.IO.File]::WriteAllText(
        (Join-Path $movieDirectory 'movie.nfo'),
        '<movie><title>Jigglefin Local Smoke Film</title><year>2026</year><plot>Smoke-test local metadata.</plot></movie>')
    [System.IO.File]::WriteAllText(
        (Join-Path $movieDirectory 'Smoke Film (2026).eng.srt'),
        "1`n00:00:00,000 --> 00:00:01,000`nA local smoke subtitle.`n")
    $libraryName = 'Jigglefin Package Smoke Movies'
    $libraryUrl = "$baseUrl/Library/VirtualFolders?name=$([Uri]::EscapeDataString($libraryName))&collectionType=movies&paths=$([Uri]::EscapeDataString($mediaRoot))&refreshLibrary=true"
    Invoke-WebRequest -Uri $libraryUrl -Method Post -Headers $authenticatedHeaders -ContentType 'application/json' `
        -Body '{"LibraryOptions":{}}' -TimeoutSec 30 | Out-Null

    $mediaDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $movie = $null
    $lastBrowseState = 'No library view response yet.'
    while ([DateTime]::UtcNow -lt $mediaDeadline) {
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) {
            throw "Packaged server exited during media scan with code $($serverProcess.ExitCode)."
        }

        try {
            $views = Invoke-RestMethod -Uri "$baseUrl/UserViews?presetViews=movies" -Headers $authenticatedHeaders -TimeoutSec 5
            $library = @($views.Items | Where-Object Name -EQ $libraryName) | Select-Object -First 1
            $lastBrowseState = 'Library view is not listed.'
            if ($library -and $library.Type -eq 'Folder' -and -not $library.CollectionType) {
                $groups = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($library.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                $action = @($groups.Items | Where-Object Name -EQ 'Action') | Select-Object -First 1
                $lastBrowseState = 'Library view exists, but its Action folder is not listed.'
                if ($action -and $action.Type -eq 'Folder') {
                    $films = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($action.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                    $movie = @($films.Items | Where-Object { $_.Name -eq 'Jigglefin Local Smoke Film' -and $_.Type -eq 'Movie' }) | Select-Object -First 1
                    $lastBrowseState = 'Action exists; movie items: ' + [string]::Join(', ', @($films.Items | ForEach-Object { "$($_.Name):$($_.Type)" }))
                    if ($movie) {
                        break
                    }
                }
            }
        } catch {
            # The library scan is asynchronous; retry until the path is indexed.
            $lastBrowseState = 'Browse request failed: ' + $_.Exception.Message.Substring(0, [Math]::Min(160, $_.Exception.Message.Length))
        }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $movie) {
        throw "The packaged server did not expose the NFO-titled sample movie through its physical folders within $TimeoutSeconds seconds. Last observation: $lastBrowseState"
    }
    $movieDetails = Invoke-RestMethod -Uri "$baseUrl/Items/$($movie.Id)?fields=MediaSources,MediaStreams" -Headers $authenticatedHeaders -TimeoutSec 15
    if ($movieDetails.ProductionYear -ne 2026 -or $movieDetails.Overview -ne 'Smoke-test local metadata.') {
        throw 'The packaged server did not apply local Kodi-style movie metadata to standard item details.'
    }
    $mediaSource = @($movieDetails.MediaSources) | Select-Object -First 1
    $subtitle = @($mediaSource.MediaStreams | Where-Object { $_.Type -eq 'Subtitle' -and $_.IsExternal }) | Select-Object -First 1
    if (-not $mediaSource -or -not $subtitle -or $subtitle.Language -ne 'eng') {
        throw 'The packaged server did not expose the local external subtitle in the movie media source.'
    }
    $subtitlePath = Join-Path $smokeProfile 'streamed-subtitle.srt'
    $subtitleUrl = "$baseUrl/Videos/$($movie.Id)/$([Uri]::EscapeDataString($mediaSource.Id))/Subtitles/$($subtitle.Index)/Stream.srt"
    Invoke-WebRequest -Uri $subtitleUrl -Headers $authenticatedHeaders -OutFile $subtitlePath -TimeoutSec 30 | Out-Null
    if (-not (Get-Content -LiteralPath $subtitlePath -Raw).Contains('A local smoke subtitle.', [StringComparison]::Ordinal)) {
        throw 'The standard subtitle endpoint did not serve the local subtitle text.'
    }

    $streamPath = Join-Path $smokeProfile 'streamed-movie.mp4'
    Invoke-WebRequest -Uri "$baseUrl/Videos/$($movie.Id)/stream?static=true" -Headers $authenticatedHeaders `
        -OutFile $streamPath -TimeoutSec 30 | Out-Null
    if ((Get-FileHash -LiteralPath $streamPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sampleVideo -Algorithm SHA256).Hash) {
        throw 'The authenticated video stream did not match the sample file.'
    }

    $smokeSucceeded = $true
    Write-Host "Packaged server smoke test passed: API version $($publicInfo.Version), bundled Web HTTP $($webResponse.StatusCode), login, folder browse, local NFO metadata, external subtitle, and direct movie stream."
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
            Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $serverProcess.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
    }

    if ($smokeSucceeded) {
        $tempRootFull = [System.IO.Path]::GetFullPath($tempRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        $smokePathFull = [System.IO.Path]::GetFullPath($smokeProfile)
        $isOwnTempProfile = $smokePathFull.StartsWith(
            $tempRootFull + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) `
            -and [System.IO.Path]::GetFileName($smokePathFull) -match '^jigglefin-package-smoke-[0-9a-f]{32}$'
        if ($isOwnTempProfile) {
            $cleanupError = $null
            for ($attempt = 0; $attempt -lt 10; $attempt++) {
                try {
                    Remove-Item -LiteralPath $smokePathFull -Recurse -Force
                    $cleanupError = $null
                    break
                } catch {
                    $cleanupError = $_
                    Start-Sleep -Milliseconds 500
                }
            }
            if ($null -ne $cleanupError) {
                Write-Warning "Could not remove the isolated smoke-test profile at $smokePathFull`: $cleanupError"
            }
        } else {
            Write-Warning "Refusing to remove a smoke-test profile outside the expected temp directory: $smokePathFull"
        }
    }
}
