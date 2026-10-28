<#
.SYNOPSIS
    Prova: tunnel TCP di RustDesk verso un host della rete remota + Edge sulla porta locale.

.DESCRIPTION
    Il client RustDesk attuale ignora da riga di comando i parametri
    "<porta locale> <host remoto> <porta remota>", ma all'apertura di --port-forward <ID>
    avvia da solo le regole salvate nel file del dispositivo:
        %APPDATA%\<nome client>\config\peers\<ID>.toml   (chiave port_forwards)

    Lo script quindi:
    1. trova una porta locale libera;
    2. scrive nel file del dispositivo un'unica regola porta locale -> host:porta remota
       (dopo averne fatto una copia .bak);
    3. avvia RustDesk con --port-forward <ID> --password ...;
    4. attende che 127.0.0.1:<porta> sia in ascolto e apre Edge.

    Il dispositivo deve essere già stato contattato almeno una volta da questo PC
    (altrimenti il file non esiste). Chiudi eventuali finestre di tunnel già aperte verso
    lo stesso ID prima di lanciare lo script.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\prova-tunnel-web.ps1 -RustDeskId 100200300 -RemoteHost 192.168.1.1
#>
param(
    [Parameter(Mandatory)] [string] $RustDeskId,
    [Parameter(Mandatory)] [string] $RemoteHost,
    [int] $RemotePort = 1180,
    [ValidateSet('http', 'https')] [string] $Scheme = 'http',
    [string] $RustDeskExe = "$env:ProgramFiles\RustDesk\rustdesk.exe",
    [string] $PeerFile,
    [int] $TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $RustDeskExe)) {
    throw "RustDesk non trovato in '$RustDeskExe'. Usa -RustDeskExe per indicare il percorso."
}
if ($RemoteHost -notmatch '^[A-Za-z0-9.\-:]+$') {
    throw "Host remoto non valido: '$RemoteHost'"
}

# File del dispositivo: cerca <ID>.toml in tutte le cartelle config\peers sotto %APPDATA%
if (-not $PeerFile) {
    $PeerFile = Get-ChildItem $env:APPDATA -Directory |
        ForEach-Object { Join-Path $_.FullName "config\peers\$RustDeskId.toml" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}
if (-not $PeerFile -or -not (Test-Path $PeerFile)) {
    throw "File del dispositivo $RustDeskId non trovato sotto $env:APPDATA. Collegati una volta da RustDesk e riprova, oppure indica -PeerFile."
}
Write-Host "File del dispositivo: $PeerFile"

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

function ConvertTo-Plain([securestring] $Secure) {
    [System.Net.NetworkCredential]::new('', $Secure).Password
}

$localPort = Get-FreeTcpPort
$rule = "port_forwards = [[$localPort, `"$RemoteHost`", $RemotePort]]"

# Sostituisce la chiave port_forwards dove si trova (anche se scritta su più righe).
# Se manca, la inserisce prima della prima tabella TOML: le chiavi di primo livello
# devono stare in cima.
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$toml = [System.IO.File]::ReadAllText($PeerFile)
Copy-Item $PeerFile "$PeerFile.bak" -Force

$existing = [regex]::new('(?ms)^port_forwards\s*=\s*\[(?:[^\[\]]|\[[^\[\]]*\])*\]')
if ($existing.IsMatch($toml)) {
    $toml = $existing.Replace($toml, $rule.Replace('$', '$$'), 1)
} else {
    $firstTable = [regex]::Match($toml, '(?m)^\[')
    if ($firstTable.Success) {
        $toml = $toml.Insert($firstTable.Index, "$rule`r`n")
    } else {
        $toml = $toml.TrimEnd() + "`r`n$rule`r`n"
    }
}
[System.IO.File]::WriteAllText($PeerFile, $toml, $utf8NoBom)
Write-Host "Regola scritta: 127.0.0.1:$localPort -> ${RemoteHost}:$RemotePort (copia in $PeerFile.bak)"

$rustDeskPassword = Read-Host 'Password RustDesk' -AsSecureString
$arguments = @('--port-forward', $RustDeskId, '--password', ('"{0}"' -f (ConvertTo-Plain $rustDeskPassword)))
Start-Process -FilePath $RustDeskExe -ArgumentList $arguments
Write-Host "RustDesk avviato, attendo che la porta $localPort sia in ascolto..."

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    Start-Sleep -Milliseconds 500
    $listening = Get-NetTCPConnection -LocalPort $localPort -State Listen -ErrorAction SilentlyContinue
} until ($listening -or (Get-Date) -gt $deadline)

if (-not $listening) {
    Write-Warning "La porta $localPort non è in ascolto dopo $TimeoutSeconds secondi: guarda la finestra del tunnel di RustDesk (la regola compare nell'elenco?)."
    return
}

$url = '{0}://127.0.0.1:{1}/' -f $Scheme, $localPort
Write-Host "Tunnel attivo, apro $url"
Start-Process msedge.exe $url
