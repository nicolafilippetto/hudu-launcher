# Hudu Launcher — Architettura

Stato: v0.2 · ottobre 2026 — implementato in `server/` e `connector/`

## 1. Obiettivo

Ridare ai tecnici la comodità di Royal TS (doppio clic → sono dentro al server del cliente)
senza le sue debolezze, usando quello che l'azienda ha già:

- **Hudu** come unica fonte di verità per clienti, asset, IP e credenziali;
- **RustDesk Pro** (self-hosted, login 365) come gateway sicuro verso le reti dei clienti;
- **Entra ID** per identità, MFA e Accesso condizionale.

Esperienza attesa: il tecnico apre `launcher.example.com`, cerca il cliente, clicca
su una connessione; si apre una scheda RDP già autenticata (o SSH / pagina web) attraverso
un tunnel RustDesk. Nessuna credenziale da digitare, nessun segreto salvato sul PC.

## 2. Componenti

```
                      Internet
 ┌──────────────────────────────────────────────────────────────┐
 │  PC tecnico (Entra joined / Intune)                          │
 │  ┌──────────────┐  hudulauncher://connect?ticket=…           │
 │  │   Browser    │ ─────────────────────────────┐             │
 │  └──────┬───────┘                              ▼             │
 │         │ HTTPS (cookie 365)        ┌─────────────────────┐  │
 │         │                           │ Connettore (WPF)    │  │
 │         │                           │ - riscatta biglietto│  │
 │         │                           │ - tunnel RustDesk   │  │
 │         │                           │ - schede RDP        │  │
 │         │                           └───┬───────────┬─────┘  │
 │         │          HTTPS (token MSAL)   │           │ avvia  │
 │         │      ┌────────────────────────┘           ▼        │
 │         │      │                          ┌──────────────┐   │
 │         │      │                          │ RustDesk     │   │
 │         │      │                          │ client       │   │
 └─────────┼──────┼──────────────────────────┴──────┬───────┴───┘
           ▼      ▼                                 │ tunnel cifrato
 ┌──────────────────────────┐                       ▼
 │ WAF / proxy (HTTPS)      │             ┌───────────────────┐
 │ proxy inverso            │             │ RustDesk Server   │
 └────────────┬─────────────┘             │ Pro               │
              │ HTTP + X-Forwarded-*      └─────────┬─────────┘
              ▼                                     ▼
 ┌──────────────────────────┐             ┌───────────────────┐
 │ Server (Docker)          │             │ Jump host cliente │──► VM / server /
 │ - portale web + API      │             │ (RustDesk)        │    firewall / web
 │ - cache inventario       │             └───────────────────┘
 │ - biglietti, audit       │
 │ - chiave Hudu, token RD  │──► Hudu cloud (API)  ──► RustDesk API (rubrica)
 └──────────────────────────┘
```

| Componente | Tecnologia | Responsabilità | Segreti che conosce |
|---|---|---|---|
| Server | ASP.NET Core, Docker | Portale web, API, cache, biglietti, audit, rate limit | Chiave API Hudu, token API RustDesk, password RustDesk |
| Connettore | C# / WPF .NET 10, installer per utente (Velopack) via Intune | Gestore `hudulauncher://`, tunnel, RDP integrato | Nessuno a riposo; una credenziale in memoria durante la sessione |
| WAF / proxy inverso | Esistente | Unico ingresso pubblico, certificato, IPS | — |
| Entra ID | Servizio Microsoft | Login 365 del portale (OIDC) e del connettore (WAM), Accesso condizionale, gruppi assegnati all'app | — |
| RustDesk Pro | Esistente | Trasporto cifrato verso i clienti, autorizzazione dei dispositivi | — |
| Hudu | Esistente (cloud) | Fonte di verità | — |

## 3. Flusso di una connessione

1. Il tecnico apre il portale (dietro il WAF) e accede con 365: MFA e dispositivo conforme si
   impongono con l'Accesso condizionale sull'app. Il portale mostra clienti e connessioni dalla
   **cache** (nessuna password).
2. Clic su "SRV-DC01 · RDP". Il server crea un **biglietto**:
   - casuale, 256 bit;
   - monouso, valido **30 secondi**;
   - legato a **utente** (UPN/oid) e **connessione** (id asset + protocollo).
3. Il browser apre `hudulauncher://connect?ticket=<biglietto>`. Il link non contiene altro.
4. Il connettore ottiene un token 365 in silenzio (MSAL + WAM, accesso singolo di Windows)
   e chiama `POST /api/connector/redeem` sul portale.
5. Il server verifica biglietto valido, non usato, non scaduto e **stesso utente** del token.
   Legge la credenziale da Hudu **in quel momento** e restituisce:
   ID RustDesk del jump host, host e porta di destinazione, protocollo, utente, password,
   password RustDesk. Registra l'evento e brucia il biglietto.
6. Il connettore sceglie una porta locale libera, configura la regola del tunnel, avvia
   RustDesk, attende che `127.0.0.1:<porta>` sia in ascolto, apre la scheda RDP integrata
   con le credenziali impostate da codice.
7. Alla chiusura della scheda: chiusura del tunnel, rimozione della regola, credenziale
   cancellata dalla memoria.

SSH: tunnel + terminale.

Web: per ogni asset si sceglie (Opzioni pagine web, con predefiniti dell'amministratore) se aprire
la pagina in una scheda del Launcher o nel browser predefinito.

- **Scheda del Launcher** (predefinito): browser integrato (WebView2) con un profilo proprio per
  scheda e un proxy SOCKS5 locale del Launcher. La pagina usa l'indirizzo vero dell'apparato
  (l'URL indicato nel portale, es. `https://vcenter.cliente.local/ui`, oppure schema, host e porta
  di destinazione); il proxy porta quell'host e quella porta nel tunnel RustDesk, e il nome viene
  risolto dal jump host. Intestazione `Host`, certificato e reindirizzamenti restano quelli
  dell'apparato: funzionano iDRAC (controllo dell'host) e vCenter (SSO sull'FQDN). La cifratura TLS
  resta tra pagina e apparato. Le altre porte dello stesso host sono bloccate; gli altri indirizzi
  (es. risorse su internet) vanno diretti. Certificati non validi: chiedi e ricorda l'impronta
  (come le chiavi SSH), ignora o blocca.
- **Browser predefinito**: tunnel + `https://127.0.0.1:<porta>` con lo stesso percorso
  dell'URL (in diretta l'indirizzo vero).

Utente e password non si copiano mai dal portale né dal Launcher: per le interfacce web si
prendono da Hudu, e per il web il portale non invia nemmeno la credenziale al Launcher.

## 4. Sicurezza

### 4.1 Principi

- La chiave API di Hudu **non lascia mai il server**. Né in chiaro, né "per la sessione".
- L'inventario (cache) non contiene password. Le password si leggono da Hudu una alla volta,
  al momento, e non vengono salvate.
- Il connettore non si fida del contenuto del link: solo il biglietto, tutto il resto arriva
  dal server.
- Ogni credenziale consegnata è legata a un utente 365 e registrata.

### 4.2 Minacce e contromisure

| Minaccia | Contromisura |
|---|---|
| Furto della chiave Hudu dal PC del tecnico | La chiave non è mai sul PC |
| Furto della chiave Hudu dal server | Segreto Docker / Key Vault; se Hudu lo consente, chiave limitata all'IP pubblico del server e senza permessi di scrittura/cancellazione |
| Credenziali 365 di un tecnico rubate | MFA + Accesso condizionale "dispositivo conforme" sull'app Entra: da un PC non aziendale il login non riesce |
| Token di un tecnico rubato usato per scaricare tutte le password | Nessuna API "tutte le password"; una credenziale per richiesta; limite (es. 20 in 5 min) con blocco e avviso; token valido ~1 ora |
| Link `hudulauncher://` malevolo da un altro sito | Il connettore accetta solo biglietti; nessun parametro host/porta/utente dal link |
| Biglietto intercettato o finito in cronologia | Monouso, 30 s, valido solo per l'utente a cui è stato emesso |
| Accesso diretto al container saltando il WAF | Porta esposta solo verso il WAF; senza HTTPS inoltrato da un proxy fidato il portale rifiuta le richieste; l'identità si valida comunque |
| Brute force sull'accesso admin di emergenza | Solo da reti ammesse, blocco dopo 5 tentativi, limite per IP, MFA TOTP obbligatoria |
| Tecnico che lascia l'azienda | Blocco o rimozione in Amministrazione → Utenti (effetto entro un minuto) e disabilitazione in 365 |
| Nessuna tracciabilità nel registro Hudu (vede solo "chiave API") | Registro del server: utente, ora, asset, protocollo, IP |

### 4.3 Identità

- **Browser:** il portale fa da sé il login OIDC con Entra (codice + PKCE), presentandosi con un
  certificato (asserzione firmata) invece di un segreto. L'utente è identificato da tenant + object
  ID: al primo accesso resta in attesa finché un amministratore non lo approva nel portale, come
  tecnico o amministratore. Assegnazione obbligatoria e gruppi in Entra restano una seconda barriera.
- **Connettore:** client pubblico MSAL con broker di Windows (WAM) sulla stessa registrazione
  dell'app; chiede lo scope `api://<client-id>/Launcher.Connect`. Il server valida il token
  (firma, emittente, audience, tenant, scope), verifica che l'object ID sia di un utente approvato e
  che sia lo stesso utente del biglietto.
- **Accesso locale di emergenza:** solo admin, password + TOTP obbligatorio, solo dalle reti
  ammesse nelle impostazioni.

### 4.4 Permessi

Nessuna granularità: tutti i tecnici vedono tutti i clienti. Accesso al portale riservato a
un gruppo Entra (es. `SG-Launcher-Tecnici`) assegnato all'app enterprise.

## 5. Dati

### 5.1 Cosa serve per ogni connessione

| Dato | Fonte |
|---|---|
| Cliente | Hudu `companies` |
| Nome, tipo asset | Hudu `assets` |
| IP di destinazione | Hudu, campo `IP Address` (Virtual Machines) |
| Protocollo / porta | Dedotto dal tipo (Windows → RDP 3389, Linux → SSH 22, firewall → HTTPS 443) o da campo dedicato |
| Credenziale | Hudu asset password collegata all'asset |
| Jump host RustDesk del cliente | **Da definire** (vedi 5.2) |
| Password RustDesk | Configurazione del server |

### 5.2 Lacune emerse dai layout attuali

- `Virtual Machines` (28) ha `IP Address`. 
- `Physical Server` (31) ha `Hostname` e `iDRAC / iLO / IPMI IP` ma **non un IP di
  management** del sistema operativo.
- `VPN & Remote Access` (32) prevede `Connection Type = RustDesk` ma **non un campo per
  l'ID RustDesk** né un collegamento al dispositivo che fa da jump host.

Proposta (modifica amministrativa ai layout):

1. In `VPN & Remote Access`: campo `RustDesk ID` (testo) e `Gateway Device` (AssetTag) →
   un record per cliente che dice "da qui si entra".
2. In `Physical Server`: campo `Management IP`.
3. Facoltativo, sugli asset collegabili: `Launcher Protocol` (lista: RDP, SSH, HTTPS, Nessuno)
   e `Launcher Port`, per le eccezioni al protocollo dedotto.

In alternativa al punto 1 si può abbinare il cliente alla rubrica RustDesk tramite tag o
alias, ma è più fragile.

### 5.3 Cache

- Inventario (clienti, asset, campi, collegamenti, ID RustDesk) aggiornato ogni 10–15 minuti
  e a richiesta, nel rispetto dei limiti dell'API Hudu.
- In memoria o SQLite nel container; nessuna password.

## 6. Automazione di RustDesk (verificata sul codice sorgente)

| Funzione | Stato |
|---|---|
| `rustdesk --connect <ID>` / `rustdesk://<ID>` | Funziona |
| Password rubrica condivisa usata da riga di comando | **No**: usata solo cliccando la scheda in rubrica. Si passa `--password` |
| `--port-forward <ID> <porta> <host> <porta>` | Supportato dal motore, **ignorato dall'interfaccia Flutter** attuale |
| Regole salvate in `%APPDATA%\RustDesk\config\peers\<ID>.toml` (`port_forwards`) | Avviate automaticamente all'apertura di `--port-forward <ID>` |
| Tunnel in ascolto | Solo `127.0.0.1` |

Strategia:

- **Subito:** il connettore scrive la regola nel file del peer e avvia
  `rustdesk --port-forward <ID> --password <pwd>`.
- **Richiesta a RustDesk:** inoltrare i parametri `<porta> <host> <porta>` dall'interfaccia
  Flutter al motore (modifica minima, già supportata dal core); usare la password della
  rubrica condivisa anche per le connessioni da riga di comando.

## 7. RDP integrato

Il connettore ospita il controllo ActiveX RDP di Microsoft (`MsRdpClient`, lo stesso usato da
Royal TS e Remote Desktop Manager):

- utente e password impostati da codice (`AdvancedSettings.ClearTextPassword`), solo in
  memoria;
- nessun `cmdkey`, nessun file `.rdp`, nessuna credenziale salvata in Windows;
- più sessioni contemporanee in schede, ognuna sulla propria porta locale.

## 8. Rischi aperti

| Rischio | Impatto | Verifica |
|---|---|---|
| Formato del file peer di RustDesk non documentato | Un aggiornamento può rompere i tunnel | Prototipo + richiesta a RustDesk |
| Il client RustDesk già avviato non rilegge il file peer | Regola ignorata | Prototipo |
| Chiusura del tunnel alla fine della sessione | Tunnel che restano aperti | Prototipo |
| Riapertura dei tunnel verso lo stesso gateway | Le sessioni SSH vanno riaperte | Richiesta a RustDesk (parametri da riga di comando) |
| Limiti di frequenza dell'API Hudu | Aggiornamento cache lento | Misurare su istanza reale |

## 9. Stato e prossimi passi

Fatto:
- prove con script PowerShell (tunnel RDP con login automatico, tunnel web) — riuscite;
- portale web e API (`server/`), immagine Docker, test automatici;
- Launcher per Windows (`connector/`) con RDP integrato, SSH/Telnet, web, installer per utente.

Da fare:
1. Prima installazione reale (vedi `docs/INSTALLAZIONE.md`) e collaudo del Launcher sui PC dei tecnici.
2. Aggiunta del campo `RustDesk ID` al layout *VPN & Remote Access* e `Management IP` a *Physical Server*.
3. Richiesta a RustDesk: parametri del tunnel da riga di comando e password della rubrica
   condivisa anche per le connessioni avviate da comando.

## 10. Struttura del repository

```
docs/            documentazione di progetto
server/          ASP.NET Core: portale, API, integrazioni Hudu e RustDesk, Docker
connector/       WPF: gestore del protocollo, tunnel RustDesk, RDP/SSH/Telnet/web, installer
scripts/         script PowerShell delle prime prove
.github/         pipeline di build e Dependabot
```
