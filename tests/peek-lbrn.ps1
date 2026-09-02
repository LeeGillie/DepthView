# Prints the structure of .lbrn2 files with the base64 payloads stripped, so a project's layer
# markup can be read without pulling megabytes of embedded image through anything.

param([Parameter(Mandatory)][string]$Folder)

Get-ChildItem -Path $Folder -Filter *.lbrn2 | ForEach-Object {
    Write-Output ('=' * 78)
    Write-Output $_.Name
    Write-Output ('=' * 78)

    [xml]$doc = Get-Content $_.FullName -Raw
    $root = $doc.DocumentElement

    Write-Output ("<{0} {1}>" -f $root.Name, (($root.Attributes | ForEach-Object {
        "$($_.Name)=`"$($_.Value)`"" }) -join ' '))

    foreach ($node in $root.ChildNodes) {
        if ($node.Name -eq 'Thumbnail') { Write-Output '  <Thumbnail Source="[elided]"/>'; continue }

        $attrs = @()
        foreach ($a in $node.Attributes) {
            $v = if ($a.Value.Length -gt 80) { "[$($a.Value.Length) chars elided]" } else { $a.Value }
            $attrs += "$($a.Name)=`"$v`""
        }
        # LocalName, not Name: PowerShell's XML adapter shadows .Name on any element that has a
        # child called "name", which every CutSetting does - so .Name reports the CLR type.
        Write-Output ("  <{0} {1}>" -f $node.LocalName, ($attrs -join ' '))

        foreach ($c in $node.ChildNodes) {
            if ($c.NodeType -eq 'Text') {
                Write-Output ("      text: {0}" -f $c.Value)
                continue
            }
            $ca = @()
            foreach ($a in $c.Attributes) {
                $v = if ($a.Value.Length -gt 80) { "[$($a.Value.Length) chars elided]" } else { $a.Value }
                $ca += "$($a.Name)=`"$v`""
            }
            $inner = if ($c.HasChildNodes -and $c.FirstChild.NodeType -eq 'Text') { " -> $($c.InnerText)" } else { '' }
            Write-Output ("      <{0} {1}>{2}" -f $c.Name, ($ca -join ' '), $inner)
        }
    }
    Write-Output ''
}
