# Swaps the "What's new" section of the release template, keeping the evergreen half exactly
# as it is. The download table, the unsigned-binary explanation and the checksum instructions
# do not change between releases and should never be retyped.

$root = Split-Path -Parent $PSScriptRoot
$tpl  = Join-Path $root '.github\RELEASE_TEMPLATE.md'
$new  = Join-Path $root '.github\newnotes.tmp.md'

$lines = Get-Content $tpl
$notes = Get-Content $new

# The header comment ends at the first line containing -->; the evergreen half starts at the
# download table. Everything between them is what this release replaces.
$commentEnd = ($lines | Select-String -Pattern '-->' | Select-Object -First 1).LineNumber
$evergreen  = ($lines | Select-String -Pattern '^## Which file do I want' | Select-Object -First 1).LineNumber

if (-not $commentEnd -or -not $evergreen) { throw 'Could not locate the section boundaries.' }

$out = @()
$out += $lines[0..($commentEnd - 1)]
$out += ''
$out += $notes
$out += ''
$out += $lines[($evergreen - 1)..($lines.Count - 1)]

# Written beside the target and moved into place, rather than truncating the original. Any
# editor or indexer holding a read handle on the template makes an in-place Set-Content fail
# halfway, and a half-written release note is worse than none.
$tmp = "$tpl.new"
Set-Content -Path $tmp -Value $out -Encoding UTF8
Move-Item -Path $tmp -Destination $tpl -Force

Write-Output ("rewrote {0}: {1} lines (was {2})" -f (Split-Path $tpl -Leaf), $out.Count, $lines.Count)
