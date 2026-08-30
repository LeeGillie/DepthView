# Builds self-contained single-file DepthView binaries for every desktop target.
# Nothing needs to be installed on the target machine - the .NET runtime is inside
# the executable. All targets cross-compile from this one machine.
#
#   powershell -ExecutionPolicy Bypass -File publish.ps1
#   powershell -ExecutionPolicy Bypass -File publish.ps1 -Rids win-x64,linux-x64

param(
    [string[]] $Rids = @('win-x64', 'win-x86', 'win-arm64',
                         'linux-x64', 'linux-arm64',
                         'osx-x64', 'osx-arm64'),
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src\DepthView\DepthView.csproj'
$out  = Join-Path $root 'publish'

Write-Host "DepthView publish -> $out" -ForegroundColor Cyan

foreach ($rid in $Rids) {
    $dest = Join-Path $out $rid
    Write-Host "`n=== $rid ===" -ForegroundColor Yellow

    dotnet publish $proj `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -o $dest `
        --nologo -v quiet

    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    Get-ChildItem $dest -File |
        Where-Object { $_.Extension -in '', '.exe' } |
        ForEach-Object { "  {0,-16} {1,10:N1} MB" -f $_.Name, ($_.Length / 1MB) }
}

Write-Host "`nDone. Hand a user the single file from publish\<their platform>\." -ForegroundColor Green
Write-Host "On macOS and Linux they will need: chmod +x DepthView" -ForegroundColor DarkGray
