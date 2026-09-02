# Round-trips the LightBurn UDP control module against fake-lightburn.ps1.
#
# Proves the parts that are ours: the datagram goes to the right port, the listener binds the
# reply port, and a reply is paired back to the call that caused it. What LightBurn itself
# actually answers is not documented and is not what this checks.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root 'src\DepthView\bin\Debug\net8.0\DepthView.exe'

if (-not (Test-Path $exe)) { throw "Build first: $exe not found" }

$fake = Start-Process powershell -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', (Join-Path $PSScriptRoot 'fake-lightburn.ps1'),
    '-Seconds', '25') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 900

function Send-Lb {
    param([string[]]$LbArgs, [string]$Label)

    $out = Join-Path $env:TEMP "lb-$([guid]::NewGuid().ToString('N')).txt"
    $p = Start-Process -FilePath $exe -ArgumentList (@('--lb') + $LbArgs) `
         -RedirectStandardOutput $out -PassThru -Wait -NoNewWindow
    $text = (Get-Content $out -Raw).Trim()
    Remove-Item $out -ErrorAction SilentlyContinue
    [pscustomobject]@{ Label = $Label; Exit = $p.ExitCode; Text = $text }
}

$failures = 0
function Expect {
    param($Result, [string]$Want)
    if ($Result.Text -eq $Want -and $Result.Exit -eq 0) {
        Write-Output ("ok    {0,-22} -> '{1}'" -f $Result.Label, $Result.Text)
    } else {
        Write-Output ("FAIL  {0,-22} -> exit {1}, '{2}' (wanted '{3}')" -f `
                      $Result.Label, $Result.Exit, $Result.Text, $Want)
        $script:failures++
    }
}

try {
    Expect (Send-Lb @('ping')            'ping')       'OK'
    Expect (Send-Lb @('status')          'status')     'IDLE'
    Expect (Send-Lb @('load', 'C:\x.lbrn2') 'load')    'LOADED'
    Expect (Send-Lb @('raw', 'HELLO')    'raw')        'ECHO:HELLO'

    # An unknown command still reaches the far end and still pairs its reply back. That is the
    # behaviour that matters for a protocol whose command list is community hearsay.
    Expect (Send-Lb @('raw', 'NOTACOMMAND') 'raw unknown') 'ECHO:NOTACOMMAND'

    # Nothing listening on a wrong port: the call must come back promptly rather than hang, and
    # must report no reply rather than inventing success.
    $miss = Send-Lb @('ping', '--send-port', '19899', '--timeout', '600') 'no listener'
    if ($miss.Exit -eq 1 -and $miss.Text -match 'No reply') {
        Write-Output ("ok    {0,-22} -> reported no reply, exit 1" -f 'no listener')
    } else {
        Write-Output ("FAIL  {0,-22} -> exit {1}, '{2}'" -f 'no listener', $miss.Exit, $miss.Text)
        $failures++
    }
}
finally {
    if (-not $fake.HasExited) { Stop-Process -Id $fake.Id -Force -ErrorAction SilentlyContinue }
}

Write-Output ''
if ($failures -eq 0) {
    Write-Output 'LightBurn UDP control: send, listen, pairing and timeout all as expected.'
    exit 0
} else {
    Write-Output "LightBurn UDP control: $failures check(s) failed."
    exit 1
}
