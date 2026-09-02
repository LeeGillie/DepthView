# Swaps the "What's new" section of the release template, keeping the evergreen half exactly
# as it is. The download table, the unsigned-binary explanation and the checksum instructions
# do not change between releases and should never be retyped.

$root = Split-Path -Parent $PSScriptRoot
$tpl  = Join-Path $root '.github\RELEASE_TEMPLATE.md'
$new  = Join-Path $root '.github\newnotes.tmp.md'

# -Encoding UTF8 on the way in, and not optional. Windows PowerShell 5.1 reads a file as the
# system ANSI codepage unless told otherwise, so a UTF-8 em-dash arrives as three mangled
# characters - and Set-Content then writes that mangling back out as perfectly valid UTF-8.
# The result reads fine to every tool and wrong to every human.
$lines = Get-Content $tpl -Encoding UTF8
$notes = Get-Content $new -Encoding UTF8

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

# UTF-8 without a BOM, written through .NET rather than Set-Content. PowerShell 5.1's UTF8
# encoding means "with BOM", and a BOM at the head of a Markdown file turns up as an invisible
# character before the first heading everywhere it is rendered.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($tmp, [string[]]$out, $utf8NoBom)
Move-Item -Path $tmp -Destination $tpl -Force

Write-Output ("rewrote {0}: {1} lines (was {2})" -f (Split-Path $tpl -Leaf), $out.Count, $lines.Count)
