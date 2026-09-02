# Pulls the embedded bitmap out of a .lbrn2 so it can be compared byte-for-byte and pixel-for
# -pixel against the file the project was built from.

param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Out
)

[xml]$doc = Get-Content $Project -Raw
$shape = $doc.DocumentElement.ChildNodes |
         Where-Object { $_.LocalName -eq 'Shape' -and $_.Type -eq 'Bitmap' } |
         Select-Object -First 1

if (-not $shape) { throw 'No Bitmap shape found' }

$b64 = $shape.GetAttribute('Data')
[System.IO.File]::WriteAllBytes($Out, [Convert]::FromBase64String($b64))

Write-Output ("extracted {0:N0} bytes to {1}" -f (Get-Item $Out).Length, $Out)
Write-Output ("XForm: {0}" -f $shape.XForm)
Write-Output ("W={0} H={1} File={2}" -f $shape.W, $shape.H, $shape.GetAttribute('File'))
