# First look at a .wws container: header bytes, embedded file signatures, and any readable
# strings. Evidence gathering only - nothing here interprets the format, and nothing based on
# it should be written into a parser without WeCreat confirming what the fields mean.

param([Parameter(Mandatory)][string]$Path)

$bytes = [System.IO.File]::ReadAllBytes($Path)
Write-Output ("file    {0}" -f (Split-Path $Path -Leaf))
Write-Output ("size    {0:N0} bytes" -f $bytes.Length)
Write-Output ''

Write-Output 'first 256 bytes'
for ($i = 0; $i -lt [Math]::Min(256, $bytes.Length); $i += 16) {
    $n = [Math]::Min(16, $bytes.Length - $i)
    $hex = ($bytes[$i..($i+$n-1)] | ForEach-Object { '{0:x2}' -f $_ }) -join ' '
    $asc = -join ($bytes[$i..($i+$n-1)] | ForEach-Object {
        if ($_ -ge 32 -and $_ -lt 127) { [char]$_ } else { '.' } })
    Write-Output ("  {0:x6}  {1,-47}  {2}" -f $i, $hex, $asc)
}
Write-Output ''

# Where the known payload formats appear inside, if at all.
$sigs = @{
    'PNG'      = [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A)
    'JPEG'     = [byte[]](0xFF,0xD8,0xFF)
    'ZIP'      = [byte[]](0x50,0x4B,0x03,0x04)
    'GZIP'     = [byte[]](0x1F,0x8B,0x08)
    'SQLite'   = [System.Text.Encoding]::ASCII.GetBytes('SQLite format 3')
    'JSON {"'  = [System.Text.Encoding]::ASCII.GetBytes('{"')
}

Write-Output 'embedded signatures (first 8 hits each)'
foreach ($name in $sigs.Keys | Sort-Object) {
    $sig = $sigs[$name]
    $hits = @()
    for ($i = 0; $i -le $bytes.Length - $sig.Length -and $hits.Count -lt 8; $i++) {
        if ($bytes[$i] -ne $sig[0]) { continue }
        $match = $true
        for ($j = 1; $j -lt $sig.Length; $j++) {
            if ($bytes[$i+$j] -ne $sig[$j]) { $match = $false; break }
        }
        if ($match) { $hits += $i }
    }
    if ($hits.Count -gt 0) {
        Write-Output ("  {0,-9} at {1}" -f $name, (($hits | ForEach-Object { '0x{0:x}' -f $_ }) -join ', '))
    }
}
Write-Output ''

# Printable ASCII runs of 6+ characters, deduplicated. Field names, if the format carries any,
# will be in here.
Write-Output 'strings (6+ chars, first 120 distinct)'
$sb = New-Object System.Text.StringBuilder
$found = New-Object System.Collections.Generic.List[string]
foreach ($b in $bytes) {
    if ($b -ge 32 -and $b -lt 127) { [void]$sb.Append([char]$b) }
    else {
        if ($sb.Length -ge 6) { [void]$found.Add($sb.ToString()) }
        [void]$sb.Clear()
    }
}
if ($sb.Length -ge 6) { [void]$found.Add($sb.ToString()) }

$found | Select-Object -Unique | Select-Object -First 120 | ForEach-Object { Write-Output ("  " + $_) }
