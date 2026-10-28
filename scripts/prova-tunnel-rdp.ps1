<#
.SYNOPSIS
    Prova: tunnel RDP di RustDesk + mstsc con credenziali inserite in automatico.

.DESCRIPTION
    1. Salva una credenziale temporanea per TERMSRV/localhost (quella che mstsc cerca
       quando si collega a localhost:<porta>).
    2. Avvia RustDesk in modalità RDP verso l'ID indicato, passando la password di RustDesk.
       RustDesk apre il tunnel su una porta casuale e lancia mstsc da solo.
    3. Appena mstsc è partito, attende qualche secondo e cancella la credenziale.

    Solo per test: cmdkey riceve la password come argomento, quindi per un istante è
    visibile nell'elenco dei processi dell'utente. La versione definitiva userà il
    controllo RDP integrato, senza cmdkey.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\prova-tunnel-rdp.ps1 -RustDeskId 100200300
#>
param(
    [Parameter(Mandatory)] [string] $RustDeskId,
    [string] $RustDeskExe = "$env:ProgramFiles\RustDesk\rustdesk.exe",
    [int] $CredentialLifetimeSeconds = 20,
    [int] $TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $RustDeskExe)) {
    throw "RustDesk non trovato in '$RustDeskExe'. Usa -RustDeskExe per indicare il percorso."
}

function ConvertTo-Plain([securestring] $Secure) {
    [System.Net.NetworkCredential]::new('', $Secure).Password
}

$rustDeskPassword = Read-Host 'Password RustDesk' -AsSecureString
$rdpCredential = Get-Credential -Message 'Credenziali RDP del PC remoto (es. DOMINIO\utente o .\utente)'

$target = 'TERMSRV/localhost'
$mstscBefore = @(Get-Process mstsc -ErrorAction SilentlyContinue | ForEach-Object Id)

try {
    cmdkey "/generic:$target" "/user:$($rdpCredential.UserName)" "/pass:$(ConvertTo-Plain $rdpCredential.Password)" | Out-Null
    Write-Host "Credenziale temporanea salvata per $target"

    $arguments = @('--rdp', $RustDeskId, '--password', ('"{0}"' -f (ConvertTo-Plain $rustDeskPassword)))
    Start-Process -FilePath $RustDeskExe -ArgumentList $arguments
    Write-Host "RustDesk avviato in modalità RDP verso $RustDeskId, attendo mstsc..."

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $mstsc = Get-Process mstsc -ErrorAction SilentlyContinue |
            Where-Object { $mstscBefore -notcontains $_.Id }
    } until ($mstsc -or (Get-Date) -gt $deadline)

    if (-not $mstsc) {
        Write-Warning "mstsc non è partito entro $TimeoutSeconds secondi: controlla la finestra di RustDesk."
        return
    }

    Write-Host "mstsc avviato (PID $($mstsc.Id -join ', ')). Cancello la credenziale tra $CredentialLifetimeSeconds secondi..."
    Start-Sleep -Seconds $CredentialLifetimeSeconds
}
finally {
    cmdkey "/delete:$target" | Out-Null
    Write-Host "Credenziale temporanea rimossa."
}
