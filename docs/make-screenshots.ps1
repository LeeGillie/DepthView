# Regenerates every image the README and the tuning guide use.
#
# The application captures its own windows (--screenshot) and renders its own relief art
# (--render), so documentation images are reproducible from a command line rather than
# hand-grabbed and left to go quietly stale as the UI changes.
#
# The published images show real coins: -Coin is the Blodgett Arch map, -SecondCoin the wolf
# map, -ThirdCoin the Huey (First Aviation) map and -FourthCoin the FOE Eagle map, all Lee's
# and none distributed with the repository (samples/ is an allow-list, and a third-party
# depth map is never committed - only pictures of it). Without -Coin the
# README images fall back to the synthetic test maps, so anyone can still run this, and the
# tuning guide's images are skipped.
#
# Run BOTH generators first if tests\fixtures is empty. They produce different things:
# make_fixtures.py writes the analysis test images, make_textures.py writes relief_demo.png.
#   python tests\make_fixtures.py
#   python tests\make_textures.py
#
#   powershell -ExecutionPolicy Bypass -File docs\make-screenshots.ps1 `
#       -Coin "<Blodgett_Arch_16bit.png>" -SecondCoin "<the wolf map>" `
#       -ThirdCoin "<HueyService.png>" -FourthCoin "<Eagle Coin.png>" `
#       -PictureCoin "<Sept 9 revision.png>"
# (-PictureCoin is a picture of a coin, not a depth map: the guide shows what that does.)
# The maps are Lee's and are never committed; each is copied into the ignored
# samples\doc-maps first, so no screenshot shows where it really lives.

param(
    [string] $Configuration = "Release",
    [string] $Coin = "",
    [string] $SecondCoin = "",
    [string] $ThirdCoin = "",
    [string] $FourthCoin = "",
    [string] $PictureCoin = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root "src\DepthView\bin\$Configuration\net8.0\DepthView.exe"
$img = Join-Path $root 'docs\images'
$guide = Join-Path $img 'tuning'
$fix = Join-Path $root 'tests\fixtures'
$work = Join-Path $env:TEMP 'depthview-screenshots'

if (-not (Test-Path $exe)) { throw "Build DepthView first - not found at $exe" }
if (-not (Test-Path (Join-Path $fix 'imposter_x257.png'))) {
    throw "Analysis fixtures missing. Run: python tests\make_fixtures.py"
}
if (-not (Test-Path (Join-Path $fix 'relief_demo.png'))) {
    throw "relief_demo.png missing. Run: python tests\make_textures.py"
}
$useCoin = $Coin -ne "" -and (Test-Path $Coin)
if ($Coin -ne "" -and -not $useCoin) { throw "No such map: $Coin" }
New-Item -ItemType Directory -Force -Path $img, $guide, $work | Out-Null

# The maps are copied into samples\doc-maps first - ignored by git like everything else in
# samples\ - because the analysis window prints the full path of the file it shows, and the
# folders a map really lives in name people and projects that are nobody else's business.
$maps = Join-Path $root 'samples\doc-maps'
New-Item -ItemType Directory -Force -Path $maps | Out-Null
function Stage([string] $path, [string] $name) {
    if ($path -eq "" -or -not (Test-Path $path)) { return "" }
    $to = Join-Path $maps $name
    Copy-Item $path $to -Force
    return $to
}
if ($useCoin) { $Coin = Stage $Coin 'Blodgett_Arch_16bit.png' }
$SecondCoin = Stage $SecondCoin 'Wolf.png'
$ThirdCoin = Stage $ThirdCoin 'HueyService.png'
$FourthCoin = Stage $FourthCoin 'Eagle Coin.png'
$PictureCoin = Stage $PictureCoin 'Sept 9 revision.png'

# A screenshot run opens the Tune window, and closing it saves the pass count it was at.
# Put the user's own preferences back afterwards.
$prefs = Join-Path $env:APPDATA 'DepthView\preferences.json'
$prefsBackup = Join-Path $work 'preferences.json'
if (Test-Path $prefs) { Copy-Item $prefs $prefsBackup -Force }

# The window captures itself after the analysis and any relief render have settled, then
# exits on its own. Give each one room, and clean up if a run ever wedges.
function Capture([string[]] $arguments, [int] $waitSeconds) {
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    if (-not $p.WaitForExit($waitSeconds * 1000)) { $p | Stop-Process -Force }
}
function Run([string[]] $arguments) {
    Start-Process -Wait -NoNewWindow -FilePath $exe -ArgumentList $arguments
}
function Q([string] $path) { '"' + $path + '"' }

# The blank every coin image is drawn for: 40 x 4 mm, 0.72 mm deep (18% of the thickness).
$blank = @('--blank', '40', '--thick', '4', '--depth-mm', '0.72')

# The tuning the guide arrives at for the Blodgett Arch coin - the wizard's recommendations.
$tuneCommon = @('--blank', '40', '--depth-mm', '0.72', '--rim-mm', '1', '--cover-rim',
                '--uniform-surround', '--black', '6509', '--white', '59845', '--spot', '30')
$makeit = @('--ramp-mm', '0.3', '--bits', '8', '--passes', '72')
$lightburn = @('--ramp-mm', '0.2', '--bits', '16', '--passes', '256')

if ($useCoin) {
    $c = Q $Coin
    $tunedMakeIt = Join-Path $work 'coin-tuned-makeit.png'
    $tunedLightBurn = Join-Path $work 'coin-tuned-lightburn.png'
    Write-Host "Tuning the coin..." -ForegroundColor Cyan
    Run (@('--tune', $c) + $tuneCommon + $makeit + @('--out', (Q $tunedMakeIt)))
    Run (@('--tune', $c) + $tuneCommon + $lightburn + @('--outline', '--out', (Q $tunedLightBurn)))

    # An imposter made from the same coin: its levels squeezed to 8 bits and saved back into
    # 16 (v x 257), exactly what an 8-bit export resaved as 16-bit looks like.
    $imposter = Join-Path $maps 'Blodgett_Arch-8bit-saved-as-16bit.png'
    python -c "import numpy as np; from PIL import Image; a=np.array(Image.open(r'$Coin')).astype(np.uint32); b=((a*255+32767)//65535*257).astype(np.uint16); Image.fromarray(b).save(r'$imposter')"
}

Write-Host "Capturing window screenshots..." -ForegroundColor Cyan
if ($useCoin) {
    Capture @((Q $imposter), '--screenshot', "$img\analysis-imposter.png") 14
    Capture @($c, '--screenshot', "$img\analysis-genuine.png") 14
    Capture (@((Q $tunedLightBurn), '--orbit', '24', '42') + $blank + @('--exag', '2',
              '--screenshot', "$img\relief-preview.png")) 18
    # At the LightBurn settings: 256 passes is where the README's before-and-after is quoted.
    Capture (@($c, '--tune-ui') + $tuneCommon + $lightburn + @('--write-dpi',
              '--screenshot', "$img\tune.png")) 16
    Capture (@($c, '--wizard', '--wizard-step', '5') + $blank + @('--screenshot', "$img\wizard.png")) 16
    # Where the layers will show at MakeIt's 72 passes, with a line across the deer's back.
    # The terrace view measures at full resolution in the background, hence the long delay.
    Capture (@($c, '--tune-ui') + $tuneCommon + $makeit + @('--show-terraces',
              '--profile-line', '0.30,0.60,0.62,0.60', '--delay', '9000',
              '--screenshot', "$img\terraces.png")) 30
    # The finishing preview on the same tuning: JAX Brown-Black rubbed back with a flat pad,
    # the guidance panel on the pad. The full-resolution map and the simulation take a moment.
    Capture (@($c, '--tune-ui') + $tuneCommon + $makeit + @('--finish-ui',
              '"darken=jax_brownblack;relieve=propad;seal=wax;stage=relieve"', '--delay', '9000',
              '--screenshot', "$img\finishing.png")) 30
} else {
    Capture @("$fix\imposter_x257.png", '--screenshot', "$img\analysis-imposter.png") 10
    Capture @("$fix\true16.png",        '--screenshot', "$img\analysis-genuine.png")  10
    Capture @("$fix\relief_demo.png", '--orbit', '24', '42',
              '--blank', '40', '--thick', '4', '--depth-mm', '1.1', '--exag', '2',
              '--screenshot', "$img\relief-preview.png") 14
    Capture @("$root\samples\07-wasted-headroom.png", '--tune-ui', '--blank', '40',
              '--rim-mm', '0.9', '--fit', '--screenshot', "$img\tune.png") 10
    Capture @("$root\samples\01-genuine-16bit.png", '--wizard', '--wizard-step', '6',
              '--blank', '40', '--screenshot', "$img\wizard.png") 12
    Capture @("$root\samples\01-genuine-16bit.png", '--tune-ui', '--blank', '40', '--passes', '72',
              '--spot', '30', '--show-terraces', '--profile-line', '0.2,0.5,0.8,0.5', '--delay', '6000',
              '--screenshot', "$img\terraces.png") 20
}

# The credit roll is moving, so pin the capture to a fixed delay: the same --delay always
# lands on the same line of the roll, which keeps this image stable between runs.
Capture @('--about', '--delay', '900', '--screenshot', "$img\about.png") 8

Write-Host "Rendering relief art..." -ForegroundColor Cyan
# --exag is doublings around --depth-mm (0 = true scale): 2 draws the relief 4x deep.
if ($useCoin) {
    $common = $blank + @('--exag', '2', '--size', '1100', '--material', 'Polished brass',
                         '--orbit', '26', '40', '--zoom', '0.86')
    Run (@('--render', (Q $tunedLightBurn)) + $common + @('--out', "$img\relief-continuous.png"))
    Run (@('--render', (Q $tunedLightBurn)) + $common + @('--slices', '16', '--out', "$img\relief-terraced.png"))
} else {
    $demo = "$fix\relief_demo.png"
    $common = @('--blank', '40', '--thick', '4', '--depth-mm', '1.1', '--exag', '2',
                '--size', '1100', '--material', 'Polished brass',
                '--orbit', '26', '40', '--zoom', '0.86')
    Run (@('--render', $demo) + $common + @('--out', "$img\relief-continuous.png"))
    Run (@('--render', $demo) + $common + @('--slices', '16', '--out', "$img\relief-terraced.png"))
}

# The two padding choices, rendered as metal. This pair is the argument for the default:
# with an untouched fill the boundary of the source image comes out as a square step around
# the coin, which is far more convincing seen than described.
Write-Host "Rendering the padding comparison..." -ForegroundColor Cyan
if ($useCoin) {
    $padSource = $c
    $fitCommon = @('--blank', '40', '--rim-mm', '0.9', '--fit', 'canvas', '--uniform-surround',
                   '--black', '6509', '--white', '59845')
} else {
    $padSource = "$root\samples\07-wasted-headroom.png"
    $fitCommon = @('--blank', '40', '--rim-mm', '0.9', '--fit', 'canvas')
}
foreach ($pad in 'background', 'untouched') {
    $tuned = Join-Path $work "fit-pad-$pad.png"
    Run (@('--tune', $padSource) + $fitCommon + @('--pad', $pad, '--out', (Q $tuned)))
    Run @('--render', (Q $tuned), '--material', 'Polished brass',
          '--blank', '40', '--thick', '4', '--depth-mm', '0.72', '--exag', '2',
          '--size', '560', '--out', "$img\fit-pad-$pad.png")
}

if ($useCoin) {
    Write-Host "Tuning guide images..." -ForegroundColor Cyan
    $tall = @('--window', '1300', '1400')
    Capture (@($c, '--tune-ui') + $blank + $tall + @('--screenshot', "$guide\01-before.png")) 16
    Capture (@($c, '--tune-ui') + $tuneCommon + $makeit + $tall + @('--thick', '4', '--write-dpi',
              '--screenshot', "$guide\02-makeit-settings.png")) 16
    Capture (@($c, '--tune-ui') + $tuneCommon + $lightburn + $tall + @('--thick', '4', '--write-dpi', '--outline',
              '--screenshot', "$guide\04-lightburn-settings.png")) 16
    $relief = $blank + @('--exag', '2', '--size', '900', '--material', 'Polished brass',
                         '--orbit', '20', '40', '--zoom', '0.9')
    Run (@('--render', (Q $tunedMakeIt)) + $relief + @('--out', "$guide\03-makeit-relief.png"))
    Run (@('--render', (Q $tunedLightBurn)) + $relief + @('--out', "$guide\05-lightburn-relief.png"))

    # The wizard: the rim question on the Huey coin (a wide drawn rim on a clean black
    # surround) when it is given, else on the Blodgett coin; then the Blodgett coin's deepest
    # areas with their empty gap, and the wolf's two blacks for the background question.
    $rimCoin = if ($ThirdCoin -ne "" -and (Test-Path $ThirdCoin)) { Q $ThirdCoin } else { $c }
    Capture (@($rimCoin, '--wizard', '--wizard-step', '3') + $blank + @('--screenshot', "$guide\06-wizard-rim.png")) 16
    Capture (@($c, '--wizard', '--wizard-step', '5') + $blank + @('--screenshot', "$guide\07-wizard-deepest.png")) 16
    if ($SecondCoin -ne "" -and (Test-Path $SecondCoin)) {
        Capture (@((Q $SecondCoin), '--wizard', '--wizard-step', '2') + $blank +
                 @('--screenshot', "$guide\08-wolf-background.png")) 16
    }

    # Outcomes side by side, as cross-sections near the edge (docs\make-profiles.py): the
    # Huey coin with its drawn rim replaced and kept, and the wolf coin with its background
    # read as a surround and as a floor. Same blank and target as everything else.
    $edge = @('--blank', '40', '--depth-mm', '0.72', '--rim-mm', '1', '--ramp-mm', '0.3')
    $profiles = Join-Path $root 'docs\make-profiles.py'
    if ($ThirdCoin -ne "" -and (Test-Path $ThirdCoin)) {
        $h = Q $ThirdCoin
        Run (@('--tune', $h) + $edge + @('--cover-rim', '--levels-from', 'design', '--out', (Q "$work\huey-replaced.png")))
        Run (@('--tune', $h) + $edge + @('--fit', 'design', '--levels-from', 'design', '--out', (Q "$work\huey-kept.png")))
        python $profiles "$guide\09-huey-rim-profile.png" "First Aviation coin: the edge, with the drawn rim replaced and kept" `
            40 0.72 4 "Drawn rim replaced (--cover-rim)" "$work\huey-replaced.png" "Drawn rim kept (--fit design)" "$work\huey-kept.png"
    }
    if ($SecondCoin -ne "" -and (Test-Path $SecondCoin)) {
        $s = Q $SecondCoin
        Run (@('--tune', $s) + $edge + @('--cover-rim', '--levels-from', 'design', '--out', (Q "$work\wolf-surround.png")))
        Run (@('--tune', $s) + $edge + @('--cover-rim', '--levels-from', 'floor', '--out', (Q "$work\wolf-floor.png")))
        python $profiles "$guide\10-wolf-background-profile.png" "Wolf coin: the background as a surround, and as a floor" `
            40 0.72 6 "Background a surround" "$work\wolf-surround.png" "Background a floor" "$work\wolf-floor.png"
    }

    # The FOE Eagle coin: an 8-bit export with stray grid lines left in its white corners -
    # what the analysis says about it, and the wizard finding the marked surround.
    if ($FourthCoin -ne "" -and (Test-Path $FourthCoin)) {
        $e = Q $FourthCoin
        Capture @($e, '--screenshot', "$guide\11-eagle-analysis.png") 14
        Capture (@($e, '--wizard', '--wizard-step', '2') + $blank + @('--screenshot', "$guide\12-eagle-background.png")) 16
    }

    # A picture of a coin that is not a depth map: what the analysis says, and what engraving
    # it as one would make.
    if ($PictureCoin -ne "" -and (Test-Path $PictureCoin)) {
        $pc = Q $PictureCoin
        Capture @($pc, '--screenshot', "$guide\13-picture-analysis.png") 14
        Run (@('--render', $pc) + $relief + @('--out', "$guide\14-picture-as-relief.png"))
    }
}

if (Test-Path $prefsBackup) { Copy-Item $prefsBackup $prefs -Force }
Get-ChildItem $img, $guide -File | ForEach-Object { "  {0,-28} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB) }
Write-Host "Done." -ForegroundColor Green
