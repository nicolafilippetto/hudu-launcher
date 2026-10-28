# Hudu Launcher

Connessioni ai server dei clienti con un clic, come Royal TS, ma con **Hudu** come unica fonte di
verità, **RustDesk Pro** come gateway sicuro e **Microsoft 365** per l'accesso.

```
Portale web (Docker)  ──►  biglietto monouso  ──►  Launcher per Windows  ──►  tunnel RustDesk  ──►  server del cliente
launcher.example.com       hudulauncher://          RDP integrato, SSH,          jump host o
Hudu + rubrica RustDesk                             Telnet, web                  RustDesk sull'asset
```

- Il tecnico cerca cliente o server nel portale e preme **RDP / SSH / Web**, scegliendo se passare
  dal jump host RustDesk o collegarsi **direttamente** (dal cliente o in VPN).
- Il Launcher si apre, ottiene in silenzio il token 365, riscatta il biglietto, apre il tunnel
  RustDesk e mostra la sessione **già autenticata** in una scheda.
- La chiave API di Hudu resta solo sul server; le password arrivano al PC una alla volta, al
  momento della connessione, e ogni consegna è registrata.

## Installazione rapida

- **Portale**: immagine Docker pubblica `ghcr.io/nicolafilippetto/hudu-launcher/launcher-server`
  (`latest` o la versione, es. `1.1.0`), con [`server/docker-compose.yml`](server/docker-compose.yml).
- **Launcher per Windows**: `HuduLauncher-win-Setup.exe` dalle
  [Release](../../releases) (installazione per utente, senza diritti di amministratore), con
  `SHA256SUMS.txt` per verificarlo.
- Tutti i passaggi (Entra ID, chiavi API, WAF, Intune) sono in
  [`docs/INSTALLAZIONE.md`](docs/INSTALLAZIONE.md).

## Contenuto del repository

| Cartella | Cosa contiene |
|---|---|
| [`server/`](server) | Portale web e API (ASP.NET Core 10, SQLite), Dockerfile, `docker-compose.yml`, test |
| [`connector/`](connector) | Launcher per Windows (WPF .NET 10): schede RDP/SSH/Telnet/web, installer Velopack, test |
| [`docs/ARCHITETTURA.md`](docs/ARCHITETTURA.md) | Architettura, sicurezza, decisioni |
| [`docs/INSTALLAZIONE.md`](docs/INSTALLAZIONE.md) | Guida passo passo: Entra ID, chiavi API, Docker, WAF, Intune |
| [`docs/SICUREZZA.md`](docs/SICUREZZA.md) | Scheda per il team Cyber: dati custoditi, protezioni, SAST/DAST, rischi residui |
| [`scripts/`](scripts) | Script PowerShell delle prime prove (tunnel RDP e web con RustDesk) |

## Sviluppo

Requisiti: .NET SDK 10.

```bash
# Server: test e avvio locale (http://localhost:5080, dati in server/src/Launcher.Server/local-data)
cd server
dotnet test
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Launcher.Server --urls http://localhost:5080

# Launcher per Windows: test della logica (anche su Linux) e compilazione
cd connector
dotnet test tests/Launcher.Connector.Tests
dotnet build src/Launcher.Connector -c Release
```

L'app Windows si compila anche su Linux ma si esegue solo su Windows. Le pipeline GitHub
(`.github/workflows`) eseguono i test, pubblicano l'immagine Docker su GitHub Container Registry e
producono l'installer `HuduLauncher-win-Setup.exe`. Dependabot propone gli aggiornamenti
delle dipendenze ogni settimana.

## Licenza

[MIT](LICENSE).

## Sicurezza

Le vulnerabilità vanno segnalate in privato: vedi [`SECURITY.md`](SECURITY.md).

Progetto indipendente, non affiliato a Hudu Technologies né a RustDesk: i nomi sono dei
rispettivi proprietari e sono usati solo per indicare con cosa si integra.
