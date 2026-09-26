[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    [ValidateRange(1, 600)]
    [int]$TimeoutSeconds = 120,

    [switch]$HeadlessWebClient
)

$ErrorActionPreference = 'Stop'

$package = (Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop).Path
$server = Join-Path $package 'jellyfin.exe'
$launcher = Join-Path $package 'Start-Jigglefin.ps1'
$launcherHost = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$ffmpeg = Join-Path $package 'ffmpeg.exe'
$web = Join-Path $package 'jellyfin-web'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sampleVideo = Join-Path $repositoryRoot 'tests/Jellyfin.Server.Integration.Tests/Test Data/JigglefinSample.mp4'
$sampleAudioBook = Join-Path $repositoryRoot 'tests/Jellyfin.Server.Integration.Tests/Test Data/JigglefinSample.m4b'
foreach ($requiredFile in @($server, $launcher, $ffmpeg, (Join-Path $web 'index.html'), (Join-Path $web 'config.json'))) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "The Windows package is missing $requiredFile"
    }
}
foreach ($sampleFile in @($sampleVideo, $sampleAudioBook)) {
    if (-not (Test-Path -LiteralPath $sampleFile -PathType Leaf)) {
        throw "The package smoke sample is missing: $sampleFile"
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
# Exercise Windows PowerShell 5.1, a profile path containing spaces, and the launcher's
# bundled FFmpeg/Web defaults from outside the package's working directory.
$serverDataDir = Join-Path $smokeProfile 'server data'
$arguments = @(
    '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
    '-File', ('"{0}"' -f $launcher),
    '-DataDir', ('"{0}"' -f $serverDataDir)
)

function Get-LauncherServer {
    param([System.Diagnostics.Process]$LauncherProcess)

    # Parent PID alone can be reused. Also require the exact binary, isolated profile,
    # and a creation time no earlier than this launch before observing or stopping it.
    Get-CimInstance Win32_Process -Filter "ParentProcessId = $($LauncherProcess.Id)" |
        Where-Object {
            $_.ExecutablePath -eq $server -and
            $_.CommandLine -and $_.CommandLine.Contains($serverDataDir) -and
            $_.CreationDate -ge $LauncherProcess.StartTime
        }
}

function Wait-LauncherServer {
    param([System.Diagnostics.Process]$LauncherProcess)

    $launchDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $launchDeadline) {
        $LauncherProcess.Refresh()
        if ($LauncherProcess.HasExited) {
            throw "The packaged launcher exited before starting its server with code $($LauncherProcess.ExitCode)."
        }
        $children = @(Get-LauncherServer -LauncherProcess $LauncherProcess)
        if ($children.Count -eq 1) {
            return Get-Process -Id $children[0].ProcessId -ErrorAction Stop
        }
        if ($children.Count -gt 1) {
            throw 'The packaged launcher started more than one matching server process.'
        }
        Start-Sleep -Milliseconds 200
    }
    throw "The packaged launcher did not start its server within $TimeoutSeconds seconds."
}

function Assert-ServerListener {
    param([System.Diagnostics.Process]$ServerProcess)

    $listeners = @(Get-NetTCPConnection -LocalPort 8096 -State Listen -ErrorAction Stop)
    if (-not $listeners.Count -or @($listeners | Where-Object OwningProcess -NE $ServerProcess.Id).Count) {
        throw "Port 8096 is not exclusively owned by the packaged server process $($ServerProcess.Id)."
    }
}

$launcherProcess = $null
$serverProcess = $null
$smokeSucceeded = $false
try {
    foreach ($entryPoint in @('executable', 'launcher')) {
        foreach ($option in @('--help', '--version', '--jigglefin-invalid-option')) {
            $cliStdout = Join-Path $smokeProfile "$entryPoint-$($option.TrimStart('-'))-stdout.log"
            $cliStderr = Join-Path $smokeProfile "$entryPoint-$($option.TrimStart('-'))-stderr.log"
            $cliCommand = if ($entryPoint -eq 'launcher') { $launcherHost } else { $server }
            $cliArguments = if ($entryPoint -eq 'launcher') { $arguments + @($option) } else { @($option) }
            if ($entryPoint -eq 'launcher' -and $option -ne '--jigglefin-invalid-option') {
                # Help and version must still work when an encoder is unavailable.
                $cliArguments = $arguments + @('-FfmpegPath', ('"{0}"' -f (Join-Path $smokeProfile 'missing-ffmpeg.exe')), $option)
            }
            $cliProcess = Start-Process -FilePath $cliCommand -ArgumentList $cliArguments -WorkingDirectory $smokeProfile `
                -RedirectStandardOutput $cliStdout -RedirectStandardError $cliStderr -WindowStyle Hidden -PassThru
            try {
                if (-not $cliProcess.WaitForExit(10000)) {
                    throw "The packaged $entryPoint did not exit for $option within 10 seconds."
                }
                $cliOutput = (Get-Content -LiteralPath $cliStdout, $cliStderr -Raw) -join "`n"
                if ($option -eq '--jigglefin-invalid-option') {
                    if ($cliProcess.ExitCode -eq 0 -or $cliOutput -notmatch "Option 'jigglefin-invalid-option' is unknown") {
                        throw "The packaged $entryPoint did not reject an invalid option: $cliOutput"
                    }
                } else {
                    $expectedOutput = if ($option -eq '--help') { '--datadir' } else { 'Jellyfin.Server \d+\.\d+\.\d+' }
                    if ($cliProcess.ExitCode -ne 0 -or $cliOutput -notmatch $expectedOutput -or $cliOutput -match 'ERROR\(S\)') {
                        throw "The packaged $entryPoint failed $option (exit $($cliProcess.ExitCode)): $cliOutput"
                    }
                }
            } finally {
                if (-not $cliProcess.HasExited) {
                    Stop-Process -InputObject $cliProcess -Force -ErrorAction SilentlyContinue
                    Wait-Process -Id $cliProcess.Id -Timeout 10 -ErrorAction SilentlyContinue
                    if ($entryPoint -eq 'launcher') {
                        foreach ($child in @(Get-LauncherServer -LauncherProcess $cliProcess)) {
                            Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
                            Wait-Process -Id $child.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
                        }
                    }
                }
            }
        }
    }
    if (Test-Path -LiteralPath $serverDataDir) {
        throw 'An informational or invalid command unexpectedly created a server profile.'
    }
    Write-Host 'Packaged executable and launcher help, version, and invalid-option checks passed.'

    if ($HeadlessWebClient) {
        $clientScript = Join-Path $repositoryRoot 'tests/WebClientSmoke/smoke.cjs'
        $clientModule = Join-Path $repositoryRoot 'tests/WebClientSmoke/node_modules/playwright/package.json'
        if (-not (Test-Path -LiteralPath $clientScript -PathType Leaf) -or -not (Test-Path -LiteralPath $clientModule -PathType Leaf)) {
            throw 'Install the headless Web test dependencies with npm ci --prefix tests/WebClientSmoke first.'
        }

        $node = (Get-Command node -ErrorAction Stop).Source
        $sampleVideo = Join-Path $smokeProfile 'Headless Web Movie.mp4'
        $sampleAudioBook = Join-Path $smokeProfile 'Headless Web Audio.m4b'
        & $ffmpeg -hide_banner -loglevel error -nostdin -f lavfi -i 'testsrc2=size=320x180:rate=24' `
            -f lavfi -i 'sine=frequency=440:sample_rate=48000' -t 20 -c:v libx264 -preset veryfast `
            -pix_fmt yuv420p -c:a aac -b:a 96k -movflags +faststart -y $sampleVideo
        if ($LASTEXITCODE -ne 0) { throw 'Could not generate the headless Web movie sample.' }
        & $ffmpeg -hide_banner -loglevel error -nostdin -f lavfi -i 'sine=frequency=523:sample_rate=48000' `
            -t 20 -c:a aac -b:a 64k -f ipod -y $sampleAudioBook
        if ($LASTEXITCODE -ne 0) { throw 'Could not generate the headless Web audiobook sample.' }
    }

    $launcherProcess = Start-Process -FilePath $launcherHost -ArgumentList $arguments -WorkingDirectory $smokeProfile `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    $serverProcess = Wait-LauncherServer -LauncherProcess $launcherProcess

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
                    # Public endpoints can respond while the main server still returns 503.
                    # Wait for the startup wizard API before sending setup requests.
                    $candidateFirstUser = Invoke-RestMethod -Uri "$baseUrl/Startup/User" -TimeoutSec 3
                    if ($candidateFirstUser.Name) {
                        $publicInfo = $candidate
                        $webResponse = $candidateWeb
                        break
                    }
                }
            }
        } catch {
            # Startup can temporarily return 503 from /web or /Startup/User and may rebind the port.
        }
        Start-Sleep -Milliseconds 1000
    }

    if ($null -eq $publicInfo) {
        throw "Packaged server did not serve its public API, bundled Web, and startup user within $TimeoutSeconds seconds."
    }

    Assert-ServerListener -ServerProcess $serverProcess
    if ($publicInfo.StartupWizardCompleted -ne $false) {
        throw 'The smoke test did not start with a fresh, unconfigured server profile.'
    }

    # Complete the first-run wizard only in this newly generated, isolated profile.
    $temporaryPassword = 'Tmp-' + [Guid]::NewGuid().ToString('N') + '!1'
    $userName = 'JigglefinSmoke'
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

    $mediaRoot = Join-Path $smokeProfile 'media, with comma'
    $movieDirectory = Join-Path $mediaRoot 'Action/Smoke Film (2026)'
    $trailerMovieDirectory = Join-Path $mediaRoot 'Action/Folder Trailer Film'
    New-Item -ItemType Directory -Path $movieDirectory | Out-Null
    New-Item -ItemType Directory -Path $trailerMovieDirectory | Out-Null
    Copy-Item -LiteralPath $sampleVideo -Destination (Join-Path $movieDirectory 'Smoke Film (2026).mp4')
    Copy-Item -LiteralPath $sampleVideo -Destination (Join-Path $trailerMovieDirectory 'Folder Trailer Film.mp4')
    Copy-Item -LiteralPath $sampleVideo -Destination (Join-Path $trailerMovieDirectory 'Folder Trailer Film-trailer.mp4')
    [System.IO.File]::WriteAllText(
        (Join-Path $movieDirectory 'movie.nfo'),
        '<movie><title>Jigglefin Local Smoke Film</title><year>2026</year><plot>Smoke-test local metadata.</plot></movie>')
    [System.IO.File]::WriteAllText(
        (Join-Path $movieDirectory 'Smoke Film (2026).eng.srt'),
        "1`n00:00:00,000 --> 00:00:01,000`nA local smoke subtitle.`n")
    $libraryName = 'Jigglefin Package Smoke Movies'
    $libraryUrl = "$baseUrl/Library/VirtualFolders?name=$([Uri]::EscapeDataString($libraryName))&collectionType=movies&refreshLibrary=true"
    $libraryPayload = @{
        LibraryOptions = @{
            PathInfos = @(@{ Path = $mediaRoot })
            EnableRealtimeMonitor = $true
        }
    } | ConvertTo-Json -Depth 5 -Compress
    Invoke-WebRequest -Uri $libraryUrl -Method Post -Headers $authenticatedHeaders -ContentType 'application/json' `
        -Body $libraryPayload -TimeoutSec 30 | Out-Null

    $mediaDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $movie = $null
    $trailerMovieFolder = $null
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
                    $trailerMovieFolder = @($films.Items | Where-Object { $_.Name -eq 'Folder Trailer Film' -and $_.Type -eq 'Folder' }) | Select-Object -First 1
                    $lastBrowseState = 'Action exists; movie items: ' + [string]::Join(', ', @($films.Items | ForEach-Object { "$($_.Name):$($_.Type)" }))
                    if ($movie -and $trailerMovieFolder) {
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
    if (-not $movie -or -not $trailerMovieFolder) {
        throw "The packaged server did not expose the NFO-titled movie and loose-trailer folder within $TimeoutSeconds seconds. Last observation: $lastBrowseState"
    }
    $movieDetails = Invoke-RestMethod -Uri "$baseUrl/Items/$($movie.Id)?fields=MediaSources,MediaStreams" -Headers $authenticatedHeaders -TimeoutSec 15
    if ($movieDetails.ProductionYear -ne 2026 -or $movieDetails.Overview -ne 'Smoke-test local metadata.') {
        throw 'The packaged server did not apply local Kodi-style movie metadata to standard item details.'
    }
    $playbackPayload = @{
        DeviceProfile = @{
            Name = 'Jigglefin Package Smoke'
            MaxStreamingBitrate = 1000000
            DirectPlayProfiles = @(@{ Container = 'mp4'; VideoCodec = 'h264'; AudioCodec = 'aac'; Type = 'Video' })
            SubtitleProfiles = @(@{ Format = 'srt'; Method = 'External' })
        }
        EnableDirectPlay = $true
        EnableDirectStream = $true
        EnableTranscoding = $false
    } | ConvertTo-Json -Depth 8 -Compress
    $playback = Invoke-RestMethod -Uri "$baseUrl/Items/$($movie.Id)/PlaybackInfo" -Method Post `
        -Headers $authenticatedHeaders -ContentType 'application/json' -Body $playbackPayload -TimeoutSec 30
    $playbackSource = @($playback.MediaSources) | Select-Object -First 1
    if ($playback.ErrorCode -or -not $playback.PlaySessionId -or @($playback.MediaSources).Count -ne 1 `
        -or -not $playbackSource.SupportsDirectPlay -or $playbackSource.TranscodingUrl) {
        throw 'The standard client playback-info request did not offer direct play of the folder-browsed sample movie.'
    }
    $negotiatedSubtitle = @($playbackSource.MediaStreams | Where-Object { $_.Type -eq 'Subtitle' -and $_.IsExternal }) | Select-Object -First 1
    if (-not $negotiatedSubtitle -or $negotiatedSubtitle.DeliveryMethod -ne 'External' -or -not $negotiatedSubtitle.DeliveryUrl) {
        throw 'The client playback-info response did not offer the local subtitle for external delivery.'
    }
    $mediaSource = @($movieDetails.MediaSources) | Select-Object -First 1
    $subtitle = @($mediaSource.MediaStreams | Where-Object { $_.Type -eq 'Subtitle' -and $_.IsExternal }) | Select-Object -First 1
    if (-not $mediaSource -or -not $subtitle -or $subtitle.Language -ne 'eng') {
        throw 'The packaged server did not expose the local external subtitle in the movie media source.'
    }
    $subtitlePath = Join-Path $smokeProfile 'streamed-subtitle.srt'
    $subtitleUrl = [Uri]::new([Uri]$baseUrl, $negotiatedSubtitle.DeliveryUrl).AbsoluteUri
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

    $trailerDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $looseTrailer = $null
    $lastTrailerBrowseState = 'No loose-trailer folder response yet.'
    while ([DateTime]::UtcNow -lt $trailerDeadline) {
        $trailerItems = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($trailerMovieFolder.Id)&fields=Path" -Headers $authenticatedHeaders -TimeoutSec 5
        $trailerMain = @($trailerItems.Items | Where-Object Path -EQ (Join-Path $trailerMovieDirectory 'Folder Trailer Film.mp4')) | Select-Object -First 1
        $looseTrailer = @($trailerItems.Items | Where-Object Path -EQ (Join-Path $trailerMovieDirectory 'Folder Trailer Film-trailer.mp4')) | Select-Object -First 1
        $lastTrailerBrowseState = 'Folder items: ' + [string]::Join(', ', @($trailerItems.Items | ForEach-Object { "$($_.Name):$($_.Type)" }))
        if ($trailerMain -and $looseTrailer) { break }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $trailerMain -or -not $looseTrailer) {
        throw "The packaged server did not expose both physical movie files within $TimeoutSeconds seconds. Last observation: $lastTrailerBrowseState"
    }
    $localTrailers = Invoke-RestMethod -Uri "$baseUrl/Items/$($trailerMain.Id)/LocalTrailers" -Headers $authenticatedHeaders -TimeoutSec 15
    $localTrailerItems = @($localTrailers)
    if ($localTrailerItems.Count -ne 1 -or $localTrailerItems[0].Type -ne 'Trailer') {
        throw 'The main movie did not retain its normal local-trailer metadata.'
    }
    $looseTrailerStreamPath = Join-Path $smokeProfile 'streamed-loose-trailer.mp4'
    Invoke-WebRequest -Uri "$baseUrl/Videos/$($looseTrailer.Id)/stream?static=true" -Headers $authenticatedHeaders `
        -OutFile $looseTrailerStreamPath -TimeoutSec 30 | Out-Null
    if ((Get-FileHash -LiteralPath $looseTrailerStreamPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sampleVideo -Algorithm SHA256).Hash) {
        throw 'The authenticated loose-trailer stream did not match the sample file.'
    }

    $bookRoot = Join-Path $smokeProfile 'books-media'
    $audioBookDirectory = Join-Path $bookRoot 'Fantasy/Smoke Audio Book'
    New-Item -ItemType Directory -Path $audioBookDirectory | Out-Null
    Copy-Item -LiteralPath $sampleAudioBook -Destination (Join-Path $audioBookDirectory 'Smoke Audio Book.m4b')
    [System.IO.File]::WriteAllText(
        (Join-Path $audioBookDirectory 'audiobook.xml'),
        '<Item><LocalTitle>Smoke Audio Book</LocalTitle><ProductionYear>2026</ProductionYear><Overview>Local audiobook sidecar metadata.</Overview></Item>')
    $bookLibraryName = 'Jigglefin Package Smoke Books'
    $bookLibraryUrl = "$baseUrl/Library/VirtualFolders?name=$([Uri]::EscapeDataString($bookLibraryName))&collectionType=books&paths=$([Uri]::EscapeDataString($bookRoot))&refreshLibrary=true"
    Invoke-WebRequest -Uri $bookLibraryUrl -Method Post -Headers $authenticatedHeaders -ContentType 'application/json' `
        -Body '{"LibraryOptions":{}}' -TimeoutSec 30 | Out-Null

    $bookDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $audioBook = $null
    $audioBookDetails = $null
    $lastBookBrowseState = 'No book library view response yet.'
    while ([DateTime]::UtcNow -lt $bookDeadline) {
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) {
            throw "Packaged server exited during audiobook scan with code $($serverProcess.ExitCode)."
        }

        try {
            $views = Invoke-RestMethod -Uri "$baseUrl/UserViews?presetViews=books" -Headers $authenticatedHeaders -TimeoutSec 5
            $bookLibrary = @($views.Items | Where-Object Name -EQ $bookLibraryName) | Select-Object -First 1
            $lastBookBrowseState = 'Book library view is not listed.'
            if ($bookLibrary -and $bookLibrary.Type -eq 'Folder' -and -not $bookLibrary.CollectionType) {
                $bookGroups = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($bookLibrary.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                $fantasy = @($bookGroups.Items | Where-Object Name -EQ 'Fantasy') | Select-Object -First 1
                $lastBookBrowseState = 'Book library exists, but its Fantasy folder is not listed.'
                if ($fantasy -and $fantasy.Type -eq 'Folder') {
                    $books = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($fantasy.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                    $audioBook = @($books.Items | Where-Object { $_.Name -eq 'Smoke Audio Book' -and $_.Type -eq 'AudioBook' }) | Select-Object -First 1
                    $lastBookBrowseState = 'Fantasy exists; book items: ' + [string]::Join(', ', @($books.Items | ForEach-Object { "$($_.Name):$($_.Type)" }))
                    if ($audioBook) {
                        # The item may be listed before its asynchronous local metadata refresh finishes.
                        $audioBookDetails = Invoke-RestMethod -Uri "$baseUrl/Items/$($audioBook.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                        if ($audioBookDetails.ProductionYear -eq 2026 -and $audioBookDetails.Overview -eq 'Local audiobook sidecar metadata.') {
                            break
                        }
                        $lastBookBrowseState = "Audiobook exists, but XML metadata is pending: year=$($audioBookDetails.ProductionYear), overview=$($audioBookDetails.Overview)"
                    }
                }
            }
        } catch {
            $lastBookBrowseState = 'Book browse request failed: ' + $_.Exception.Message.Substring(0, [Math]::Min(160, $_.Exception.Message.Length))
        }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $audioBook -or $audioBookDetails.ProductionYear -ne 2026 -or $audioBookDetails.Overview -ne 'Local audiobook sidecar metadata.') {
        throw "The packaged server did not expose the audiobook and its local XML metadata through physical folders within $TimeoutSeconds seconds. Last observation: $lastBookBrowseState"
    }

    $audioPlaybackPayload = @{
        DeviceProfile = @{
            Name = 'Jigglefin Package Smoke Audio'
            MaxStreamingBitrate = 1000000
            DirectPlayProfiles = @(@{ Container = 'm4b,m4a,mp4'; AudioCodec = 'aac'; Type = 'Audio' })
        }
        EnableDirectPlay = $true
        EnableDirectStream = $true
        EnableTranscoding = $false
    } | ConvertTo-Json -Depth 8 -Compress
    $audioPlayback = Invoke-RestMethod -Uri "$baseUrl/Items/$($audioBook.Id)/PlaybackInfo" -Method Post `
        -Headers $authenticatedHeaders -ContentType 'application/json' -Body $audioPlaybackPayload -TimeoutSec 30
    $audioSource = @($audioPlayback.MediaSources) | Select-Object -First 1
    if ($audioPlayback.ErrorCode -or -not $audioPlayback.PlaySessionId -or @($audioPlayback.MediaSources).Count -ne 1 `
        -or -not $audioSource.SupportsDirectPlay -or $audioSource.TranscodingUrl) {
        throw 'The standard client playback-info request did not offer direct play of the folder-browsed audiobook.'
    }
    $audioStreamPath = Join-Path $smokeProfile 'streamed-audiobook.m4b'
    Invoke-WebRequest -Uri "$baseUrl/Audio/$($audioBook.Id)/stream?static=true" -Headers $authenticatedHeaders `
        -OutFile $audioStreamPath -TimeoutSec 30 | Out-Null
    if ((Get-FileHash -LiteralPath $audioStreamPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sampleAudioBook -Algorithm SHA256).Hash) {
        throw 'The authenticated audiobook stream did not match the sample file.'
    }

    $musicRoot = Join-Path $smokeProfile 'music-media'
    $albumDirectory = Join-Path $musicRoot 'Rock/Smoke Album'
    New-Item -ItemType Directory -Path $albumDirectory | Out-Null
    Copy-Item -LiteralPath $sampleAudioBook -Destination (Join-Path $albumDirectory 'Track 01.m4a')
    Copy-Item -LiteralPath $sampleVideo -Destination (Join-Path $albumDirectory 'Bonus Clip.mp4')
    [System.IO.File]::WriteAllText(
        (Join-Path $albumDirectory 'album.nfo'),
        '<album><title>Smoke Album</title></album>')
    $musicLibraryName = 'Jigglefin Package Smoke Music'
    $musicLibraryUrl = "$baseUrl/Library/VirtualFolders?name=$([Uri]::EscapeDataString($musicLibraryName))&collectionType=music&paths=$([Uri]::EscapeDataString($musicRoot))&refreshLibrary=true"
    Invoke-WebRequest -Uri $musicLibraryUrl -Method Post -Headers $authenticatedHeaders -ContentType 'application/json' `
        -Body '{"LibraryOptions":{}}' -TimeoutSec 30 | Out-Null

    $musicDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $musicTrack = $null
    $bonusVideo = $null
    $lastMusicBrowseState = 'No music library view response yet.'
    while ([DateTime]::UtcNow -lt $musicDeadline) {
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) {
            throw "Packaged server exited during music scan with code $($serverProcess.ExitCode)."
        }

        try {
            $views = Invoke-RestMethod -Uri "$baseUrl/UserViews?presetViews=music" -Headers $authenticatedHeaders -TimeoutSec 5
            $musicLibrary = @($views.Items | Where-Object Name -EQ $musicLibraryName) | Select-Object -First 1
            $lastMusicBrowseState = 'Music library view is not listed.'
            if ($musicLibrary -and $musicLibrary.Type -eq 'Folder' -and -not $musicLibrary.CollectionType) {
                $musicGroups = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($musicLibrary.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                $rock = @($musicGroups.Items | Where-Object Name -EQ 'Rock') | Select-Object -First 1
                $lastMusicBrowseState = 'Music library exists, but its Rock folder is not listed.'
                if ($rock -and $rock.Type -eq 'Folder') {
                    $albums = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($rock.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                    $album = @($albums.Items | Where-Object { $_.Name -eq 'Smoke Album' -and $_.Type -eq 'MusicAlbum' }) | Select-Object -First 1
                    $lastMusicBrowseState = 'Rock exists; album items: ' + [string]::Join(', ', @($albums.Items | ForEach-Object { "$($_.Name):$($_.Type)" }))
                    if ($album) {
                        $tracks = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($album.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                        $musicTrack = @($tracks.Items | Where-Object Type -EQ 'Audio') | Select-Object -First 1
                        $bonusVideo = @($tracks.Items | Where-Object { $_.Name -eq 'Bonus Clip' -and $_.Type -eq 'MusicVideo' }) | Select-Object -First 1
                        if ($musicTrack -and $bonusVideo) { break }
                        $lastMusicBrowseState = 'Album exists; items: ' + [string]::Join(', ', @($tracks.Items | ForEach-Object { "$($_.Name):$($_.Type)" }))
                    }
                }
            }
        } catch {
            $lastMusicBrowseState = 'Music browse request failed: ' + $_.Exception.Message.Substring(0, [Math]::Min(160, $_.Exception.Message.Length))
        }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $musicTrack -or -not $bonusVideo) {
        throw "The packaged server did not expose the track and bonus video through physical music folders within $TimeoutSeconds seconds. Last observation: $lastMusicBrowseState"
    }
    $musicStreamPath = Join-Path $smokeProfile 'streamed-track.m4a'
    Invoke-WebRequest -Uri "$baseUrl/Audio/$($musicTrack.Id)/stream?static=true" -Headers $authenticatedHeaders `
        -OutFile $musicStreamPath -TimeoutSec 30 | Out-Null
    if ((Get-FileHash -LiteralPath $musicStreamPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sampleAudioBook -Algorithm SHA256).Hash) {
        throw 'The authenticated music track stream did not match the sample file.'
    }
    $bonusStreamPath = Join-Path $smokeProfile 'streamed-bonus-video.mp4'
    Invoke-WebRequest -Uri "$baseUrl/Videos/$($bonusVideo.Id)/stream?static=true" -Headers $authenticatedHeaders `
        -OutFile $bonusStreamPath -TimeoutSec 30 | Out-Null
    if ((Get-FileHash -LiteralPath $bonusStreamPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sampleVideo -Algorithm SHA256).Hash) {
        throw 'The authenticated bonus video stream did not match the sample file.'
    }

    $tvRoot = Join-Path $smokeProfile 'tv-media'
    $seasonDirectory = Join-Path $tvRoot 'Drama/Smoke Show/Season 1'
    New-Item -ItemType Directory -Path $seasonDirectory | Out-Null
    Copy-Item -LiteralPath $sampleVideo -Destination (Join-Path $seasonDirectory 'Smoke Show - S01E01.mp4')
    [System.IO.File]::WriteAllText(
        (Join-Path (Split-Path -Parent $seasonDirectory) 'tvshow.nfo'),
        '<tvshow><title>Smoke Show</title></tvshow>')
    $tvLibraryName = 'Jigglefin Package Smoke TV'
    $tvLibraryUrl = "$baseUrl/Library/VirtualFolders?name=$([Uri]::EscapeDataString($tvLibraryName))&collectionType=tvshows&paths=$([Uri]::EscapeDataString($tvRoot))&refreshLibrary=true"
    Invoke-WebRequest -Uri $tvLibraryUrl -Method Post -Headers $authenticatedHeaders -ContentType 'application/json' `
        -Body '{"LibraryOptions":{}}' -TimeoutSec 30 | Out-Null

    $tvDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $episode = $null
    $lastTvBrowseState = 'No TV library view response yet.'
    while ([DateTime]::UtcNow -lt $tvDeadline) {
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) {
            throw "Packaged server exited during TV scan with code $($serverProcess.ExitCode)."
        }

        try {
            $views = Invoke-RestMethod -Uri "$baseUrl/UserViews?presetViews=tvshows" -Headers $authenticatedHeaders -TimeoutSec 5
            $tvLibrary = @($views.Items | Where-Object Name -EQ $tvLibraryName) | Select-Object -First 1
            $lastTvBrowseState = 'TV library view is not listed.'
            if ($tvLibrary -and $tvLibrary.Type -eq 'Folder' -and -not $tvLibrary.CollectionType) {
                $tvGroups = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($tvLibrary.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                $drama = @($tvGroups.Items | Where-Object Name -EQ 'Drama') | Select-Object -First 1
                $lastTvBrowseState = 'TV library exists, but its Drama folder is not listed.'
                if ($drama -and $drama.Type -eq 'Folder') {
                    $shows = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($drama.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                    $show = @($shows.Items | Where-Object { $_.Name -eq 'Smoke Show' -and $_.Type -eq 'Series' }) | Select-Object -First 1
                    $lastTvBrowseState = 'Drama exists; show items: ' + [string]::Join(', ', @($shows.Items | ForEach-Object { "$($_.Name):$($_.Type)" }))
                    if ($show) {
                        $seasons = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($show.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                        $season = @($seasons.Items | Where-Object { $_.Name -eq 'Season 1' -and $_.Type -eq 'Season' }) | Select-Object -First 1
                        if ($season) {
                            $episodes = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($season.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                            $episode = @($episodes.Items | Where-Object Type -EQ 'Episode') | Select-Object -First 1
                            if ($episode) { break }
                        }
                        $lastTvBrowseState = 'Show exists, but its physical season or episode is not listed.'
                    }
                }
            }
        } catch {
            $lastTvBrowseState = 'TV browse request failed: ' + $_.Exception.Message.Substring(0, [Math]::Min(160, $_.Exception.Message.Length))
        }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $episode) {
        throw "The packaged server did not expose the episode through its physical TV folders within $TimeoutSeconds seconds. Last observation: $lastTvBrowseState"
    }
    $episodeStreamPath = Join-Path $smokeProfile 'streamed-episode.mp4'
    Invoke-WebRequest -Uri "$baseUrl/Videos/$($episode.Id)/stream?static=true" -Headers $authenticatedHeaders `
        -OutFile $episodeStreamPath -TimeoutSec 30 | Out-Null
    if ((Get-FileHash -LiteralPath $episodeStreamPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sampleVideo -Algorithm SHA256).Hash) {
        throw 'The authenticated TV episode stream did not match the sample file.'
    }

    $homeRoot = Join-Path $smokeProfile 'home-media'
    $familyDirectory = Join-Path $homeRoot 'Family'
    $photoDirectory = Join-Path $familyDirectory 'Photos Only'
    New-Item -ItemType Directory -Path $photoDirectory | Out-Null
    Copy-Item -LiteralPath $sampleVideo -Destination (Join-Path $familyDirectory 'Smoke Home Clip.mp4')
    $photoPath = Join-Path $photoDirectory 'Smoke Photo.png'
    & $ffmpeg -hide_banner -loglevel error -nostdin -f lavfi -i 'color=c=steelblue:s=320x180' `
        -frames:v 1 -update 1 -y $photoPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate the home-photo smoke sample.' }
    $homeLibraryName = 'Jigglefin Package Smoke Home Videos'
    $homeLibraryUrl = "$baseUrl/Library/VirtualFolders?name=$([Uri]::EscapeDataString($homeLibraryName))&collectionType=homevideos&paths=$([Uri]::EscapeDataString($homeRoot))&refreshLibrary=true"
    Invoke-WebRequest -Uri $homeLibraryUrl -Method Post -Headers $authenticatedHeaders -ContentType 'application/json' `
        -Body '{"LibraryOptions":{}}' -TimeoutSec 30 | Out-Null

    $homeDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $homeClip = $null
    $photo = $null
    $lastHomeBrowseState = 'No home-video library view response yet.'
    while ([DateTime]::UtcNow -lt $homeDeadline) {
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) {
            throw "Packaged server exited during home-video scan with code $($serverProcess.ExitCode)."
        }

        try {
            $views = Invoke-RestMethod -Uri "$baseUrl/UserViews?presetViews=homevideos" -Headers $authenticatedHeaders -TimeoutSec 5
            $homeLibrary = @($views.Items | Where-Object Name -EQ $homeLibraryName) | Select-Object -First 1
            $lastHomeBrowseState = 'Home-video library view is not listed.'
            if ($homeLibrary -and $homeLibrary.Type -eq 'Folder' -and -not $homeLibrary.CollectionType) {
                $homeGroups = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($homeLibrary.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                $family = @($homeGroups.Items | Where-Object Name -EQ 'Family') | Select-Object -First 1
                $lastHomeBrowseState = 'Home-video library exists, but its Family folder is not listed.'
                if ($family -and $family.Type -eq 'Folder') {
                    $familyItems = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($family.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                    $homeClip = @($familyItems.Items | Where-Object { $_.Name -eq 'Smoke Home Clip' -and $_.Type -eq 'Video' }) | Select-Object -First 1
                    $photoAlbum = @($familyItems.Items | Where-Object { $_.Name -eq 'Photos Only' -and $_.Type -eq 'PhotoAlbum' }) | Select-Object -First 1
                    $lastHomeBrowseState = 'Family exists; items: ' + [string]::Join(', ', @($familyItems.Items | ForEach-Object { "$($_.Name):$($_.Type)" }))
                    if ($homeClip -and $photoAlbum) {
                        $photos = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($photoAlbum.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                        $photo = @($photos.Items | Where-Object { $_.Name -eq 'Smoke Photo' -and $_.Type -eq 'Photo' }) | Select-Object -First 1
                        if ($photo) { break }
                        $lastHomeBrowseState = 'Photo album exists, but its physical photo is not listed.'
                    }
                }
            }
        } catch {
            $lastHomeBrowseState = 'Home-video browse request failed: ' + $_.Exception.Message.Substring(0, [Math]::Min(160, $_.Exception.Message.Length))
        }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $homeClip -or -not $photo) {
        throw "The packaged server did not expose the home video and photo through physical folders within $TimeoutSeconds seconds. Last observation: $lastHomeBrowseState"
    }
    $homeStreamPath = Join-Path $smokeProfile 'streamed-home-video.mp4'
    Invoke-WebRequest -Uri "$baseUrl/Videos/$($homeClip.Id)/stream?static=true" -Headers $authenticatedHeaders `
        -OutFile $homeStreamPath -TimeoutSec 30 | Out-Null
    if ((Get-FileHash -LiteralPath $homeStreamPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sampleVideo -Algorithm SHA256).Hash) {
        throw 'The authenticated home-video stream did not match the sample file.'
    }

    $musicVideoRoot = Join-Path $smokeProfile 'music-video-media'
    $performanceDirectory = Join-Path $musicVideoRoot 'Performances'
    New-Item -ItemType Directory -Path $performanceDirectory | Out-Null
    Copy-Item -LiteralPath $sampleVideo -Destination (Join-Path $performanceDirectory 'Smoke Music Clip.mp4')
    $musicVideoLibraryName = 'Jigglefin Package Smoke Music Videos'
    $musicVideoLibraryUrl = "$baseUrl/Library/VirtualFolders?name=$([Uri]::EscapeDataString($musicVideoLibraryName))&collectionType=musicvideos&paths=$([Uri]::EscapeDataString($musicVideoRoot))&refreshLibrary=true"
    Invoke-WebRequest -Uri $musicVideoLibraryUrl -Method Post -Headers $authenticatedHeaders -ContentType 'application/json' `
        -Body '{"LibraryOptions":{}}' -TimeoutSec 30 | Out-Null

    $musicVideoDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $musicVideo = $null
    $lastMusicVideoBrowseState = 'No music-video library view response yet.'
    while ([DateTime]::UtcNow -lt $musicVideoDeadline) {
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) {
            throw "Packaged server exited during music-video scan with code $($serverProcess.ExitCode)."
        }

        try {
            $views = Invoke-RestMethod -Uri "$baseUrl/UserViews?presetViews=musicvideos" -Headers $authenticatedHeaders -TimeoutSec 5
            $musicVideoLibrary = @($views.Items | Where-Object Name -EQ $musicVideoLibraryName) | Select-Object -First 1
            $lastMusicVideoBrowseState = 'Music-video library view is not listed.'
            if ($musicVideoLibrary -and $musicVideoLibrary.Type -eq 'Folder' -and -not $musicVideoLibrary.CollectionType) {
                $musicVideoGroups = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($musicVideoLibrary.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                $performances = @($musicVideoGroups.Items | Where-Object Name -EQ 'Performances') | Select-Object -First 1
                $lastMusicVideoBrowseState = 'Music-video library exists, but its Performances folder is not listed.'
                if ($performances -and $performances.Type -eq 'Folder') {
                    $musicVideos = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($performances.Id)" -Headers $authenticatedHeaders -TimeoutSec 5
                    $musicVideo = @($musicVideos.Items | Where-Object { $_.Name -eq 'Smoke Music Clip' -and $_.Type -eq 'MusicVideo' }) | Select-Object -First 1
                    $lastMusicVideoBrowseState = 'Performances exists; items: ' + [string]::Join(', ', @($musicVideos.Items | ForEach-Object { "$($_.Name):$($_.Type)" }))
                    if ($musicVideo) { break }
                }
            }
        } catch {
            $lastMusicVideoBrowseState = 'Music-video browse request failed: ' + $_.Exception.Message.Substring(0, [Math]::Min(160, $_.Exception.Message.Length))
        }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $musicVideo) {
        throw "The packaged server did not expose the music video through physical folders within $TimeoutSeconds seconds. Last observation: $lastMusicVideoBrowseState"
    }
    $musicVideoStreamPath = Join-Path $smokeProfile 'streamed-music-video.mp4'
    Invoke-WebRequest -Uri "$baseUrl/Videos/$($musicVideo.Id)/stream?static=true" -Headers $authenticatedHeaders `
        -OutFile $musicVideoStreamPath -TimeoutSec 30 | Out-Null
    if ((Get-FileHash -LiteralPath $musicVideoStreamPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sampleVideo -Algorithm SHA256).Hash) {
        throw 'The authenticated music-video stream did not match the sample file.'
    }

    if ($HeadlessWebClient) {
        $oldBaseUrl = $env:JIGGLEFIN_TEST_BASE_URL
        $oldUser = $env:JIGGLEFIN_TEST_USER
        $oldPassword = $env:JIGGLEFIN_TEST_PASSWORD
        $oldPhotoId = $env:JIGGLEFIN_TEST_PHOTO_ID
        try {
            $env:JIGGLEFIN_TEST_BASE_URL = $baseUrl
            $env:JIGGLEFIN_TEST_USER = $userName
            $env:JIGGLEFIN_TEST_PASSWORD = $temporaryPassword
            $env:JIGGLEFIN_TEST_PHOTO_ID = $photo.Id
            & $node $clientScript
            if ($LASTEXITCODE -ne 0) {
                throw "Headless Jellyfin Web smoke test failed with exit code $LASTEXITCODE"
            }
        } finally {
            $env:JIGGLEFIN_TEST_BASE_URL = $oldBaseUrl
            $env:JIGGLEFIN_TEST_USER = $oldUser
            $env:JIGGLEFIN_TEST_PASSWORD = $oldPassword
            $env:JIGGLEFIN_TEST_PHOTO_ID = $oldPhotoId
        }
    }

    # New libraries created through Jellyfin Web enable real-time monitoring by default.
    # A file copied into an already-running library should appear without a manual scan.
    $liveMoviePath = Join-Path $mediaRoot 'Action/Live Addition.mp4'
    Copy-Item -LiteralPath $sampleVideo -Destination $liveMoviePath
    $liveDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $liveMovie = $null
    while ([DateTime]::UtcNow -lt $liveDeadline) {
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) {
            throw "Packaged server exited while waiting for a live library update with code $($serverProcess.ExitCode)."
        }
        $liveFilms = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($action.Id)" -Headers $authenticatedHeaders -TimeoutSec 15
        $liveMovie = @($liveFilms.Items | Where-Object { $_.Name -eq 'Live Addition' -and $_.Type -eq 'Movie' }) | Select-Object -First 1
        if ($liveMovie) { break }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $liveMovie) {
        throw "The running server did not discover a movie added to a monitored library within $TimeoutSeconds seconds."
    }
    $liveStreamPath = Join-Path $smokeProfile 'streamed-live-addition.mp4'
    Invoke-WebRequest -Uri "$baseUrl/Videos/$($liveMovie.Id)/stream?static=true" -Headers $authenticatedHeaders `
        -OutFile $liveStreamPath -TimeoutSec 30 | Out-Null
    if ((Get-FileHash -LiteralPath $liveStreamPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sampleVideo -Algorithm SHA256).Hash) {
        throw 'The movie added to the running server did not stream its original bytes.'
    }
    # The library monitor can still hold the file open briefly after it first
    # appears in the API. Wait for that read handle before testing removal.
    $deleteDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while ($true) {
        try {
            Remove-Item -LiteralPath $liveMoviePath -ErrorAction Stop
            break
        } catch {
            if (-not (Test-Path -LiteralPath $liveMoviePath)) {
                break
            }
            if ([DateTime]::UtcNow -ge $deleteDeadline) {
                throw
            }
            Start-Sleep -Milliseconds 250
        }
    }
    $removalDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $removedFromBrowse = $false
    while ([DateTime]::UtcNow -lt $removalDeadline) {
        $liveFilms = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($action.Id)" -Headers $authenticatedHeaders -TimeoutSec 15
        $removedFromBrowse = -not @($liveFilms.Items | Where-Object Id -EQ $liveMovie.Id).Count
        if ($removedFromBrowse) { break }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $removedFromBrowse) {
        throw "The running server kept a movie after its physical file was removed for $TimeoutSeconds seconds."
    }

    # Verify that the same portable profile survives a clean stop and restart.
    try {
        Invoke-WebRequest -Uri "$baseUrl/System/Shutdown" -Method Post -Headers $authenticatedHeaders -TimeoutSec 15 | Out-Null
    } catch {
        $serverProcess.Refresh()
        if (-not $serverProcess.HasExited) {
            throw
        }
    }
    if (-not $serverProcess.WaitForExit(30000)) {
        throw 'The packaged server did not shut down gracefully within 30 seconds.'
    }
    if (-not $launcherProcess.WaitForExit(10000) -or $launcherProcess.ExitCode -ne 0) {
        throw 'The packaged launcher did not exit successfully after the server shut down.'
    }
    $offlineMoviePath = Join-Path $mediaRoot 'Action/Offline Addition.mp4'
    Copy-Item -LiteralPath $sampleVideo -Destination $offlineMoviePath
    $offlineRemovedTrailerPath = Join-Path $trailerMovieDirectory 'Folder Trailer Film-trailer.mp4'
    Remove-Item -LiteralPath $offlineRemovedTrailerPath
    $restartStdout = Join-Path $smokeProfile 'restart-stdout.log'
    $restartStderr = Join-Path $smokeProfile 'restart-stderr.log'
    $serverProcess = $null
    $launcherProcess = Start-Process -FilePath $launcherHost -ArgumentList $arguments -WorkingDirectory $smokeProfile `
        -RedirectStandardOutput $restartStdout -RedirectStandardError $restartStderr -WindowStyle Hidden -PassThru
    $serverProcess = Wait-LauncherServer -LauncherProcess $launcherProcess

    $restartDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $restartInfo = $null
    $restartAuthentication = $null
    while ([DateTime]::UtcNow -lt $restartDeadline) {
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) {
            throw "Packaged server exited during restart with code $($serverProcess.ExitCode)."
        }

        try {
            $candidateInfo = Invoke-RestMethod -Uri "$baseUrl/System/Info/Public" -TimeoutSec 3
            if ($candidateInfo.StartupWizardCompleted -eq $true) {
                $candidateAuthentication = Invoke-RestMethod -Uri "$baseUrl/Users/AuthenticateByName" -Method Post `
                    -Headers @{ Authorization = $clientHeader } -ContentType 'application/json' -Body $authPayload -TimeoutSec 3
                if ($candidateAuthentication.AccessToken) {
                    $restartInfo = $candidateInfo
                    $restartAuthentication = $candidateAuthentication
                    break
                }
            }
        } catch {
            # The server may expose its public endpoint before authenticated requests are ready.
        }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $restartAuthentication -or $restartInfo.Id -ne $publicInfo.Id) {
        throw 'The packaged server did not restart with the same configured profile and accept its existing user.'
    }
    Assert-ServerListener -ServerProcess $serverProcess
    $restartHeaders = @{ Authorization = "$clientHeader, Token=$($restartAuthentication.AccessToken)" }
    $restartViews = Invoke-RestMethod -Uri "$baseUrl/UserViews" -Headers $restartHeaders -TimeoutSec 15
    foreach ($expectedName in @($libraryName, $bookLibraryName, $musicLibraryName, $tvLibraryName, $homeLibraryName, $musicVideoLibraryName)) {
        $view = @($restartViews.Items | Where-Object Name -EQ $expectedName) | Select-Object -First 1
        if (-not $view -or $view.Type -ne 'Folder' -or $view.CollectionType) {
            throw "The restarted server did not preserve folder-first library $expectedName."
        }
    }
    $restartedMovieLibrary = @($restartViews.Items | Where-Object Name -EQ $libraryName) | Select-Object -First 1
    $restartedGroups = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($restartedMovieLibrary.Id)" -Headers $restartHeaders -TimeoutSec 15
    $restartedAction = @($restartedGroups.Items | Where-Object Name -EQ 'Action') | Select-Object -First 1
    if (-not $restartedAction -or $restartedAction.Type -ne 'Folder') {
        throw 'The restarted server did not preserve the physical Action folder.'
    }
    $restartedFilms = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($restartedAction.Id)" -Headers $restartHeaders -TimeoutSec 15
    $restartedMovie = @($restartedFilms.Items | Where-Object { $_.Name -eq 'Jigglefin Local Smoke Film' -and $_.Type -eq 'Movie' }) | Select-Object -First 1
    if (-not $restartedMovie -or $restartedMovie.Id -ne $movie.Id) {
        throw 'The restarted server did not preserve the NFO-titled movie and its stable item ID.'
    }
    $restartedMovieDetails = Invoke-RestMethod -Uri "$baseUrl/Items/$($restartedMovie.Id)" -Headers $restartHeaders -TimeoutSec 15
    if ($restartedMovieDetails.Overview -ne 'Smoke-test local metadata.') {
        throw 'The restarted server did not preserve local movie metadata.'
    }
    $restartStreamPath = Join-Path $smokeProfile 'streamed-movie-after-restart.mp4'
    Invoke-WebRequest -Uri "$baseUrl/Videos/$($restartedMovie.Id)/stream?static=true" -Headers $restartHeaders `
        -OutFile $restartStreamPath -TimeoutSec 30 | Out-Null
    if ((Get-FileHash -LiteralPath $restartStreamPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sampleVideo -Algorithm SHA256).Hash) {
        throw 'The authenticated movie stream after restart did not match the sample file.'
    }
    $offlineDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $offlineMovie = $null
    while ([DateTime]::UtcNow -lt $offlineDeadline) {
        $restartedFilms = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($restartedAction.Id)" -Headers $restartHeaders -TimeoutSec 15
        $offlineMovie = @($restartedFilms.Items | Where-Object { $_.Name -eq 'Offline Addition' -and $_.Type -eq 'Movie' }) | Select-Object -First 1
        if ($offlineMovie) { break }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $offlineMovie) {
        throw "The restarted server did not discover a movie added while it was stopped within $TimeoutSeconds seconds."
    }
    $offlineStreamPath = Join-Path $smokeProfile 'streamed-offline-addition.mp4'
    Invoke-WebRequest -Uri "$baseUrl/Videos/$($offlineMovie.Id)/stream?static=true" -Headers $restartHeaders `
        -OutFile $offlineStreamPath -TimeoutSec 30 | Out-Null
    if ((Get-FileHash -LiteralPath $offlineStreamPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sampleVideo -Algorithm SHA256).Hash) {
        throw 'The movie added during downtime did not stream its original bytes after startup scanning.'
    }

    $offlineRemovalDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $promotedTrailerMovie = $null
    while ([DateTime]::UtcNow -lt $offlineRemovalDeadline) {
        $restartedFilms = Invoke-RestMethod -Uri "$baseUrl/Items?parentId=$($restartedAction.Id)" -Headers $restartHeaders -TimeoutSec 15
        $promotedTrailerMovie = @($restartedFilms.Items | Where-Object { $_.Name -eq 'Folder Trailer Film' -and $_.Type -eq 'Movie' }) | Select-Object -First 1
        $staleTrailerFolder = @($restartedFilms.Items | Where-Object Id -EQ $trailerMovieFolder.Id)
        if ($promotedTrailerMovie -and -not $staleTrailerFolder.Count) { break }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $promotedTrailerMovie -or $staleTrailerFolder.Count) {
        throw "The restarted server did not remove the trailer deleted during downtime and restore its remaining movie within $TimeoutSeconds seconds."
    }
    $staleTrailerItems = Invoke-RestMethod -Uri "$baseUrl/Items?ids=$($looseTrailer.Id)" -Headers $restartHeaders -TimeoutSec 15
    if (@($staleTrailerItems.Items).Count -ne 0) {
        throw 'The trailer deleted during downtime remains queryable by its old item ID.'
    }
    # Read the raw JSON: Invoke-RestMethod can surface an empty array as $null,
    # which @($null) would incorrectly count as one trailer.
    $trailerMetadataResponse = Invoke-WebRequest -Uri "$baseUrl/Items/$($promotedTrailerMovie.Id)/LocalTrailers" -Headers $restartHeaders -TimeoutSec 15
    $staleTrailerMetadata = @($trailerMetadataResponse.Content | ConvertFrom-Json)
    if ($staleTrailerMetadata.Count -ne 0) {
        throw "The remaining movie kept owned trailer metadata after its physical trailer was removed during downtime: $($trailerMetadataResponse.Content)"
    }

    $smokeSucceeded = $true
    Write-Host "Packaged server smoke test passed through Start-Jigglefin.ps1: API version $($publicInfo.Version), bundled Web HTTP $($webResponse.StatusCode), login, movie with a loose trailer, audiobook, music with an album bonus video, TV, home-video/photo, and music-video folder browse, local NFO and audiobook XML metadata, client playback negotiation, external subtitle, direct media streams, live library additions and removals, persistence after restart, and discovery of a movie added and a trailer removed during downtime."
} catch {
    Write-Warning "Package smoke test failed. Isolated profile and logs: $smokeProfile"
    foreach ($logPath in @($stdout, $stderr, (Join-Path $smokeProfile 'restart-stdout.log'), (Join-Path $smokeProfile 'restart-stderr.log'))) {
        if (Test-Path -LiteralPath $logPath) {
            Write-Warning "Last lines of $logPath"
            Get-Content -LiteralPath $logPath -Tail 20 | ForEach-Object {
                Write-Warning $_.Substring(0, [Math]::Min(250, $_.Length))
            }
        }
    }
    throw
} finally {
    # Stop the wrapper first so a startup failure cannot leave it spawning a server
    # after cleanup has checked for children. Only stop children proven to be ours.
    if ($null -ne $launcherProcess) {
        $launcherProcess.Refresh()
        if (-not $launcherProcess.HasExited) {
            Stop-Process -InputObject $launcherProcess -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $launcherProcess.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
        foreach ($child in @(Get-LauncherServer -LauncherProcess $launcherProcess)) {
            Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $child.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
        }
    }
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
