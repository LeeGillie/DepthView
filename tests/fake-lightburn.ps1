# Stands in for LightBurn so the UDP control module can be tested without a laser attached.
#
# Listens on 19840 the way LightBurn does, and answers on 19841. Deliberately dumb: it echoes
# back what it was asked, because the point is to prove the transport and the request/response
# pairing, not to model LightBurn's replies - which are undocumented anyway.

param(
    [int]$ListenPort = 19840,
    [int]$ReplyPort  = 19841,
    [int]$Seconds    = 25
)

$rx = New-Object System.Net.Sockets.UdpClient($ListenPort)
$tx = New-Object System.Net.Sockets.UdpClient
$any = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
$deadline = (Get-Date).AddSeconds($Seconds)

Write-Output "fake-lightburn: listening on $ListenPort, replying to $ReplyPort"

try {
    while ((Get-Date) -lt $deadline) {
        if ($rx.Available -gt 0) {
            $bytes = $rx.Receive([ref]$any)
            $text  = [System.Text.Encoding]::UTF8.GetString($bytes)
            Write-Output "fake-lightburn: got '$text'"

            $reply = switch -Wildcard ($text) {
                'PING'        { 'OK' }
                'STATUS'      { 'IDLE' }
                'LOADFILE:*'  { 'LOADED' }
                'FORCELOAD:*' { 'LOADED' }
                'START'       { 'STARTED' }
                default       { "ECHO:$text" }
            }

            $out = [System.Text.Encoding]::UTF8.GetBytes($reply)
            [void]$tx.Send($out, $out.Length, '127.0.0.1', $ReplyPort)
            Write-Output "fake-lightburn: sent '$reply'"
        }
        Start-Sleep -Milliseconds 40
    }
}
finally {
    $rx.Close()
    $tx.Close()
    Write-Output "fake-lightburn: stopped"
}
