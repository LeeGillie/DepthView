# Regenerates every image the README uses.
#
# The application captures its own windows (--screenshot) and renders its own relief art
# (--render), so documentation images are reproducible from a command line rather than
# hand-grabbed and left to go quietly stale as the UI changes.
#
# Run BOTH generators first if tests\fixtures is empty. They produce different things:
# make_fixtures.py writes the analysis test images, make_textures.py writes relief_demo.png,
# which is the map the relief screenshots use.
#   python tests\make_fixtures.py
#   python tests\make_textures.py
#
#   powershell -ExecutionPolicy Bypass -File docs\make-screenshots.ps1

param(
    [string] $Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root "src\DepthView\bin\$Configuration\net8.0\DepthView.exe"
$img = Join-Path $root 'docs\images'
$fix = Join-Path $root 'tests\fixtures'

if (-not (Test-Path $exe)) { throw "Build DepthView first - not found at $exe" }
if (-not (Test-Path (Join-Path $fix 'imposter_x257.png'))) {
    throw "Analysis fixtures missing. Run: python tests\make_fixtures.py"
}
if (-not (Test-Path (Join-Path $fix 'relief_demo.png'))) {
    throw "relief_demo.png missing. It comes from a different generator than the analysis " +
          "fixtures. Run: python tests\make_textures.py"
}
New-Item -ItemType Directory -Force -Path $img | Out-Null

# The window captures itself after the analysis and any relief render have settled, then
# exits on its own. Give each one room, and clean up if a run ever wedges.
function Capture([string[]] $arguments, [int] $waitSeconds) {
    Start-Process -FilePath $exe -ArgumentList $arguments
    Start-Sleep -Seconds $waitSeconds
    Get-Process DepthView -ErrorAction SilentlyContinue | Stop-Process -Force
}

Write-Host "Capturing window screenshots..." -ForegroundColor Cyan
Capture @("$fix\imposter_x257.png", '--screenshot', "$img\analysis-imposter.png") 10
Capture @("$fix\true16.png",        '--screenshot', "$img\analysis-genuine.png")  10
Capture @("$fix\relief_demo.png", '--orbit', '24', '42',
          '--screenshot', "$img\relief-preview.png") 14

# The tuning dialog, opened with a rim already configured. The flags are how the rim case
# gets captured at all: it is two checkbox clicks in the UI and neither of them is a thing a
# script can do, and the rim is the layout most likely to overflow on a small screen.
Capture @("$root\samples\07-wasted-headroom.png", '--tune-ui', '--blank', '40',
          '--rim-mm', '0.9', '--fit', '--screenshot', "$img\tune.png") 10

# The credit roll is moving, so pin the capture to a fixed delay: the same --delay always
# lands on the same line of the roll, which keeps this image stable between runs.
Capture @('--about', '--delay', '900', '--screenshot', "$img\about.png") 8

Write-Host "Rendering relief art..." -ForegroundColor Cyan
$demo = "$fix\relief_demo.png"
$common = @('--exag', '1.6', '--size', '1100', '--material', 'Polished brass',
            '--orbit', '26', '40', '--zoom', '0.86')

Start-Process -Wait -NoNewWindow -FilePath $exe -ArgumentList (
    @('--render', $demo) + $common + @('--out', "$img\relief-continuous.png"))
Start-Process -Wait -NoNewWindow -FilePath $exe -ArgumentList (
    @('--render', $demo) + $common + @('--slices', '16', '--out', "$img\relief-terraced.png"))

# The two padding choices, rendered as metal. This pair is the argument for the default:
# with an untouched fill the boundary of the source image comes out as a square step around
# the coin, which is far more convincing seen than described.
Write-Host "Rendering the padding comparison..." -ForegroundColor Cyan
$coin = "$root\samples\07-wasted-headroom.png"
$fitCommon = @('--blank', '40', '--rim-mm', '0.9', '--fit', 'canvas')

foreach ($pad in 'background', 'untouched') {
    $tuned = Join-Path $env:TEMP "fit-pad-$pad.png"
    Start-Process -Wait -NoNewWindow -FilePath $exe -ArgumentList (
        @('--tune', $coin) + $fitCommon + @('--pad', $pad, '--out', $tuned))
    Start-Process -Wait -NoNewWindow -FilePath $exe -ArgumentList (
        @('--render', $tuned, '--material', 'Polished brass', '--exag', '1.4',
          '--size', '560', '--out', "$img\fit-pad-$pad.png"))
}

Get-ChildItem $img | ForEach-Object { "  {0,-26} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB) }
Write-Host "Done." -ForegroundColor Green
