[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Fixture, [Parameter(Mandatory = $true)][string]$MediaRoot)
$ErrorActionPreference = 'Stop'
$fixturePath = (Resolve-Path -LiteralPath $Fixture).Path.TrimEnd('\')
$mediaPath = (Resolve-Path -LiteralPath $MediaRoot).Path.TrimEnd('\')
if (-not $mediaPath.StartsWith($fixturePath + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFileName($fixturePath)).StartsWith('jigglefin-zip-upgrade-')) {
    throw 'Only an owned upgrade fixture may be exclusively locked.'
}
$streams = [Collections.Generic.List[IO.FileStream]]::new()
try {
    foreach ($file in [IO.Directory]::GetFiles($mediaPath, '*', [IO.SearchOption]::AllDirectories)) {
        $streams.Add([IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None))
    }
    [Console]::WriteLine("LOCKED $($streams.Count)")
    [Console]::Out.Flush()
    [void][Console]::ReadLine()
} finally {
    foreach ($stream in $streams) { $stream.Dispose() }
}
