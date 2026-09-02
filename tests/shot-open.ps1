# Opens a file in the GUI and captures it, killing any stragglers first so a hung window from
# a previous run cannot lock the executable and make the next build look broken.

param(
    [Parameter(Mandatory)][string]$File,
    [Parameter(Mandatory)][string]$Out,
    [int]$DelayMs = 5000
)

$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root 'src\DepthView\bin\Debug\net8.0\DepthView.exe'

Get-Process DepthView -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$p = Start-Process -FilePath $exe -ArgumentList @(
        ('"' + $File + '"'), '--screenshot', ('"' + (Join-Path $root $Out) + '"'),
        '--delay', $DelayMs) -PassThru

# A window that never screenshots is a bug worth surfacing rather than waiting out forever.
if (-not $p.WaitForExit(($DelayMs + 15000))) {
    Write-Output "TIMED OUT - the window never closed itself. Killing it."
    $p | Stop-Process -Force
    exit 1
}

$path = Join-Path $root $Out
if (Test-Path $path) {
    Write-Output ("ok  {0}  {1:N0} bytes" -f $Out, (Get-Item $path).Length)
} else {
    Write-Output ("FAIL  no screenshot written for {0}" -f $File)
    exit 1
}
