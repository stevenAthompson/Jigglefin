[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$IsoPath
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop).Path
$iso = (Resolve-Path -LiteralPath $IsoPath -ErrorAction Stop).Path
if (-not $iso.EndsWith('.iso', [StringComparison]::OrdinalIgnoreCase)) { throw 'Select a DVD ISO.' }
if (Get-NetTCPConnection -LocalPort 8096 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 8096 is already in use; this test will not touch another server.' }
$before = Get-Item -LiteralPath $iso
$profile = Join-Path ([IO.Path]::GetTempPath()) ('jigglefin-dvd-menu-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $profile | Out-Null
$server = Join-Path $package 'jellyfin.exe'
$ffmpeg = Join-Path $package 'ffmpeg.exe'
$base = 'http://127.0.0.1:8096'
$headers = @{}
$process = $null
$sessionId = $null

function Request-Api {
    param([string]$Path, [string]$Method = 'Get', $Body = $null, [int]$TimeoutSec = 30)
    $request = @{ Uri = "$base$Path"; Method = $Method; Headers = $headers; TimeoutSec = $TimeoutSec }
    if ($null -ne $Body) { $request.ContentType = 'application/json'; $request.Body = $Body | ConvertTo-Json -Depth 10 -Compress }
    Invoke-RestMethod @request
}

try {
    $process = Start-Process -FilePath $server -ArgumentList @('--datadir', ('"{0}"' -f $profile), '--ffmpeg', ('"{0}"' -f $ffmpeg)) `
        -WorkingDirectory $package -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $profile 'server-stdout.log') `
        -RedirectStandardError (Join-Path $profile 'server-stderr.log')
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    do {
        if ($process.HasExited) { throw 'The isolated DVD test server exited during startup.' }
        try {
            $public = Request-Api '/System/Info/Public' 'Get' $null 3
            $setup = Request-Api '/Startup/User' 'Get' $null 3
            if ($public.StartupWizardCompleted -eq $false -and $setup.Name) { break }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    if ([DateTime]::UtcNow -ge $deadline) { throw 'Isolated DVD test server did not become ready.' }
    $listeners = @(Get-NetTCPConnection -LocalPort 8096 -State Listen)
    if (@($listeners | Where-Object OwningProcess -ne $process.Id).Count) { throw 'Port 8096 is not owned by the isolated test server.' }

    $password = 'Tmp-' + [Guid]::NewGuid().ToString('N') + '!1'
    Request-Api '/Startup/User' 'Post' @{ Name = 'DvdMenuSmoke'; Password = $password } | Out-Null
    Request-Api '/Startup/Complete' 'Post' @{} | Out-Null
    $device = [Guid]::NewGuid().ToString('N')
    $client = 'MediaBrowser Client="Jigglefin DVD CLI", Device="Windows", DeviceId="' + $device + '", Version="1"'
    $headers = @{ Authorization = $client }
    $auth = Request-Api '/Users/AuthenticateByName' 'Post' @{ Username = 'DvdMenuSmoke'; Pw = $password }
    if (-not $auth.AccessToken) { throw 'Temporary user could not authenticate.' }
    $headers = @{ Authorization = "$client, Token=$($auth.AccessToken)" }

    $root = [IO.Path]::GetDirectoryName($iso)
    Request-Api ('/Library/VirtualFolders?name=' + [Uri]::EscapeDataString('DVD CLI Test')) 'Post' `
        @{ LibraryOptions = @{ PathInfos = @(@{ Path = $root }) } } | Out-Null
    $view = @((Request-Api '/UserViews').Items | Where-Object Name -eq 'DVD CLI Test') | Select-Object -First 1
    if (-not $view) { throw 'The root library was not created.' }
    $listing = Request-Api ('/Items?parentId=' + $view.Id)
    $entry = @($listing.Items | Where-Object Name -eq ([IO.Path]::GetFileName($iso))) | Select-Object -First 1
    if (-not $entry) { throw 'The ISO was not visible in the live root listing.' }
    Write-Host "Selected live ISO item: $($entry.Id)"

    $started = Request-Api ("/Jigglefin/Dvd/$($entry.Id)/Sessions") 'Post' $null 90
    $sessionId = $started.SessionId
    if (-not $sessionId) { throw 'The DVD menu session did not return an ID.' }
    Write-Host "DVD session ready: $sessionId"
    $playlist = Request-Api ("/Jigglefin/Dvd/$sessionId/stream.m3u8")
    if ($playlist -notmatch '#EXTM3U' -or $playlist -notmatch 'segment-\d+\.ts\?ApiKey=') { throw 'The authenticated DVD HLS playlist is invalid.' }
    $segmentName = [regex]::Match($playlist, 'segment-\d+\.ts').Value
    $segment = Invoke-WebRequest -Uri "$base/Jigglefin/Dvd/$sessionId/$segmentName" -Headers $headers -TimeoutSec 15
    if ($segment.StatusCode -ne 200 -or $segment.RawContentLength -lt 1000) { throw 'The DVD menu HLS segment is missing or empty.' }
    foreach ($command in @('down','select','menu')) {
        Request-Api ("/Jigglefin/Dvd/$sessionId/Commands/$command") 'Post' | Out-Null
        Write-Host "DVD command accepted: $command"
        Start-Sleep -Seconds 2
    }
    Request-Api ("/Jigglefin/Dvd/$sessionId") 'Delete' | Out-Null
    $sessionId = $null
    Write-Host 'PASS: isolated authenticated DVD menu endpoints, live segment and navigation.'
    $previousBase = $env:JIGGLEFIN_TEST_BASE_URL
    $previousUser = $env:JIGGLEFIN_TEST_USER
    $previousPassword = $env:JIGGLEFIN_TEST_PASSWORD
    $previousIsoName = $env:JIGGLEFIN_TEST_ISO_NAME
    try {
        $env:JIGGLEFIN_TEST_BASE_URL = $base
        $env:JIGGLEFIN_TEST_USER = 'DvdMenuSmoke'
        $env:JIGGLEFIN_TEST_PASSWORD = $password
        $env:JIGGLEFIN_TEST_ISO_NAME = [IO.Path]::GetFileName($iso)
        & node (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/WebClientSmoke/dvd-menu.cjs')
        if ($LASTEXITCODE -ne 0) { throw "Headless DVD menu web test failed with exit code $LASTEXITCODE" }
        $remaining = @(Get-ChildItem -LiteralPath (Join-Path $profile 'cache/dvd-menus') -Directory -ErrorAction SilentlyContinue)
        if ($remaining.Count) { throw 'A stopped DVD menu session left temporary worker files behind.' }
    } finally {
        $env:JIGGLEFIN_TEST_BASE_URL = $previousBase
        $env:JIGGLEFIN_TEST_USER = $previousUser
        $env:JIGGLEFIN_TEST_PASSWORD = $previousPassword
        $env:JIGGLEFIN_TEST_ISO_NAME = $previousIsoName
    }
}
finally {
    if ($sessionId) { try { Request-Api ("/Jigglefin/Dvd/$sessionId") 'Delete' | Out-Null } catch { } }
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force; Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue }
    $after = Get-Item -LiteralPath $iso
    if ($before.Length -ne $after.Length -or $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc) { throw 'The DVD ISO changed during the read-only test.' }
    Write-Host "ISO unchanged. Isolated test logs: $profile"
}
