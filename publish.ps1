# Builds self-contained single-file DepthView binaries for every desktop target, then
# packs each into the zip a user downloads (dist\DepthView-<version>-<rid>.zip) with
# packaging\make_bundle.py - the same step the release workflow runs. Needs Python 3.
# Nothing needs to be installed on the target machine - the .NET runtime is inside
# the executable. All targets cross-compile from this one machine, but a macOS zip
# built here is unsigned and will not start on Apple silicon; release those from CI.
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

$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$python = if (Get-Command py -ErrorAction SilentlyContinue) { 'py' } else { 'python' }
$dist = Join-Path $root 'dist'
Write-Host "DepthView $version publish -> $out, zips -> $dist" -ForegroundColor Cyan

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

    $bin = Join-Path $dest $(if ($rid -like 'win-*') { 'DepthView.exe' } else { 'DepthView' })
    & $python (Join-Path $root 'packaging\make_bundle.py') --rid $rid --version $version --binary $bin --dist $dist
    if ($LASTEXITCODE -ne 0) { throw "bundling failed for $rid" }
}

Write-Host "`nDone. Hand a user the zip for their platform from dist\." -ForegroundColor Green
