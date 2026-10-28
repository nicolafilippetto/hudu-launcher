# Installazione e configurazione

Guida passo passo per mettere in funzione Hudu Launcher: server Docker, Microsoft 365, Hudu,
RustDesk e app Windows distribuita con Intune.

Indirizzo di esempio usato nella guida: `https://launcher.example.com`.

---

## 1. Registrazione dell'app in Microsoft Entra ID

Una sola registrazione serve sia al portale web sia all'app Windows.

1. **Entra admin center → Identità → Applicazioni → Registrazioni app → Nuova registrazione**
   - Nome: `Hudu Launcher`
   - Tipi di account: *Solo account in questa directory organizzativa*
   - URI di reindirizzamento: piattaforma **Web**, `https://launcher.example.com/signin-oidc`
2. Annotare **ID applicazione (client)** e **ID directory (tenant)**.
3. **Credenziale dell'app: certificato** (consigliato, nessun segreto da copiare). Il certificato
   si genera dal portale dopo l'installazione (sezione 4) e qui si carica solo la parte pubblica:
   **Certificati e segreti → Certificati → Carica certificato**. In alternativa, un **segreto client**
   (Certificati e segreti → Nuovo segreto client), da passare poi al certificato.
4. **Esponi un'API**
   - URI ID applicazione: accettare `api://<client-id>`
   - **Aggiungi un ambito**: nome `Launcher.Connect`, consenso *Amministratori e utenti*,
     descrizione "Connessioni tramite Hudu Launcher", stato abilitato.
5. **Autenticazione → Aggiungi una piattaforma → App per dispositivi mobili e desktop**
   - URI personalizzato: `ms-appx-web://microsoft.aad.brokerplugin/<client-id>`
     (serve all'app Windows per l'accesso automatico tramite Windows).
6. **Autorizzazioni API → Aggiungi un'autorizzazione → API personali → Hudu Launcher →
   Launcher.Connect**, poi **Concedi il consenso amministratore**.
7. **Manifesto**: impostare `"accessTokenAcceptedVersion": 2` (consigliato).
8. **Applicazioni aziendali → Hudu Launcher**
   - **Proprietà → Assegnazione utente obbligatoria: Sì**
   - **Utenti e gruppi**: aggiungere il gruppo dei tecnici. Chi non è nel gruppo non entra né nel
     portale né nell'app.
9. **Accesso condizionale** (consigliato): criterio sull'app *Hudu Launcher* con MFA
   obbligatoria e dispositivo conforme o aggiunto a Entra.

**Chi entra lo decide anche il portale.** Al primo accesso con Microsoft 365 l'utente resta *in
attesa* (nessun accesso) finché un amministratore non lo approva in **Amministrazione → Utenti**,
come tecnico o come amministratore. Gli utenti sono riconosciuti dal loro *object ID* Entra, non
dall'email; bloccarli o rimuoverli toglie l'accesso al portale e al Launcher entro un minuto. Così
un errore di configurazione in Entra (ad esempio l'assegnazione obbligatoria dimenticata) non apre
il portale a tutto il tenant.

---

## 2. Chiavi API

### Hudu

**Admin → API Keys → New API Key**
- Nome descrittivo, es. `Launcher - connessioni - Prod`
- **Password access: abilitato** (serve al momento della connessione)
- Se disponibile, limitare la chiave all'IP pubblico in uscita del server Docker
- Niente permessi di scrittura o cancellazione

Documentare l'esistenza della chiave (scopo, data, rotazione) **senza** il valore.

### RustDesk Server Pro (facoltativo: senza RustDesk il portale usa solo connessioni dirette)

**Console web → Settings → Tokens → Create**
- Permesso **Address Book: lettura** (eventualmente anche *Device: lettura*)
- Indirizzo API: quello della console (es. `https://rustdesk.example.com` o la porta 21114)

### Password di connessione RustDesk

La password unica dei dispositivi va inserita nel portale (Impostazioni → RustDesk). Viene
consegnata all'app solo al momento della connessione.

---

## 3. Server Docker

Requisiti: host Linux con Docker e Docker Compose, raggiungibile dal WAF / proxy inverso, con accesso in
uscita a Hudu cloud, al server RustDesk e a `login.microsoftonline.com`.

```bash
mkdir -p /opt/launcher && cd /opt/launcher
# copiare qui server/docker-compose.yml dal repository

# Chiave master: cifra i segreti salvati. CONSERVARNE UNA COPIA come password in Hudu.
mkdir -p secrets
openssl rand -base64 32 > secrets/master_key
# Il container gira come utente non root (uid 1654): il file deve essere suo.
chown 1654:1654 secrets/master_key
chmod 400 secrets/master_key
```

Nel `docker-compose.yml` adattare:

| Voce | Valore |
|---|---|
| `ports` | IP interno dell'host raggiungibile dal WAF, es. `"10.0.0.50:8080:8080"` |
| `Launcher__TrustedProxies` | IP del WAF visto dal container |
| `AllowedHosts` | `launcher.example.com;127.0.0.1` |

```bash
docker compose pull      # immagine ghcr.io/nicolafilippetto/hudu-launcher/launcher-server
docker compose up -d
docker compose logs launcher | grep CONFIGURAZIONE   # token per la configurazione iniziale
```

L'immagine è pubblicata dalla pipeline GitHub (`latest` per il branch principale, tag con nome del
branch e commit). Il pacchetto è pubblico: non serve `docker login`.

Il container gira **non root**, con filesystem in sola lettura, nessuna capability e senza shell.
I dati stanno nel volume `launcher-data` (database SQLite e chiavi cifrate).

### Prova interna prima della pubblicazione

Per provare il portale da rete interna, senza certificato né WAF, aggiungere nel compose:

```yaml
      Launcher__AllowHttp: "true"
      AllowedHosts: "*"
```

e aprire `http://<ip-server>:8080`. Funzionano configurazione iniziale, accesso admin locale,
impostazioni, sincronizzazione e home; il login con Microsoft 365 no (Entra accetta indirizzi di
ritorno `http` solo su localhost). **Togliere entrambe le righe prima della pubblicazione.**

Per provare anche il Launcher in questa modalità: *Impostazioni → Portale →* **Permetti agli admin
entrati con utente locale di usare il Launcher anche senza Microsoft 365**. Il Launcher, aperto dal
primo link del portale, chiede se usare `http://<ip-server>:8080` come portale (http è accettato
solo verso indirizzi IP interni). Le credenziali viaggiano in chiaro sulla rete interna: solo per prova.

### Portainer (in alternativa a `docker compose`)

**Add stack → Web editor**, con il compose di `server/docker-compose.yml` e due modifiche:

- segreto con **percorso assoluto** (Portainer non vede la cartella del compose):
  `file: /opt/launcher/secrets/master_key` (file `1654:1654`, permessi `400`);
- volume esistente dichiarato **esterno**, altrimenti Portainer ne crea uno vuoto e si perdono
  admin, MFA, impostazioni e chiavi (verificare il nome con `docker volume ls`):

```yaml
volumes:
  launcher-data:
    external: true
    name: launcher_launcher-data
```

Prima di avviare lo stack: `docker compose down` nella vecchia cartella (**senza `-v`**).
Aggiornamenti: **Update the stack** con **Re-pull image**. Non mettere la chiave master in una
variabile d'ambiente dello stack (sarebbe visibile in Portainer e in `docker inspect`).

### Firewall: porta 8080 solo dal WAF

Docker inoltra le porte pubblicate tramite FORWARD: ufw non le vede. Si filtra nella catena
`DOCKER-USER`, come per gli altri container (IP del container fisso, regole persistenti con
`netfilter-persistent`). Attenzione: **non** dichiarare `DOCKER-USER` in `/etc/ufw/after.rules`,
perché a ogni avvio di ufw verrebbe svuotata, cancellando anche le regole degli altri container.

1. Rete dedicata con sottorete libera (verificare con `docker network inspect $(docker network ls -q)
   --format '{{.Name}} {{range .IPAM.Config}}{{.Subnet}}{{end}}'`):
   `docker network create --driver bridge --subnet 172.28.0.0/24 launcher_net`
2. Nel compose/stack: porta solo sull'IPv4 del server (niente ascolto IPv6 `[::]`, che non passa da
   `DOCKER-USER`) e IP fisso:

```yaml
services:
  launcher:
    ports:
      - "10.0.0.50:8080:8080"
    networks:
      launcher_net:
        ipv4_address: 172.28.0.2

networks:
  launcher_net:
    external: true
```

3. Regole (accodate a quelle esistenti, senza toccarle) e salvataggio:

```bash
iptables -A DOCKER-USER -p tcp -s <IP-WAF> -d 172.28.0.2 --dport 8080 -j RETURN
iptables -A DOCKER-USER -p tcp -d 172.28.0.2 --dport 8080 -j DROP
netfilter-persistent save
```

4. Verifica: `iptables -L DOCKER-USER -n -v --line-numbers`, `ss -ltnp | grep 8080` (solo
   `10.0.0.50:8080`); da un PC qualsiasi `curl -m 5 http://10.0.0.50:8080/healthz` deve andare
   in timeout.

**Fail2ban**: non applicarlo al launcher a livello di host. Tutto il traffico arriva dall'IP del
WAF: un ban bloccherebbe tutti. Contro la forza bruta lavorano già i limiti per IP reale del
portale, il blocco account e le regole del WAF.

### WAF / proxy inverso

- Sito `launcher.example.com` con certificato valido, solo HTTPS
- Upstream: `http://<ip-host-docker>:8080`
- Inoltro delle intestazioni `X-Forwarded-For` e **`X-Forwarded-Proto: https`**
- Intestazione `Host` originale conservata (serve per l'indirizzo di ritorno del login 365)
- Corpo massimo delle richieste: 2 MB
- Se il portale risponde "Il portale va raggiunto in HTTPS tramite il proxy", il WAF non invia
  `X-Forwarded-Proto` o il suo IP non è in `Launcher__TrustedProxies`.

### Prima di pubblicare su internet

- Nel compose **togliere** `Launcher__AllowHttp` e `AllowedHosts: "*"`; impostare
  `AllowedHosts: "launcher.example.com"` e `Launcher__TrustedProxies` con l'IP del WAF.
- In **Amministrazione** non deve comparire "Proxy non riconosciuto" né "Modalità test HTTP".
- Entra: *Assegnazione utente obbligatoria* sull'app aziendale con il solo gruppo dei tecnici;
  Accesso condizionale con MFA (e dispositivo conforme, se possibile).
- *Impostazioni → Portale*: reti per l'accesso locale ristrette alla LAN/VPN dei tecnici
  (es. `10.0.10.0/24`), non tutte le reti private; connessioni senza 365 **spente**.
- WAF: solo HTTPS, limite di richieste per IP, blocco geografico se possibile, e
  `/Account/LocalLogin` e `/Setup` raggiungibili solo dagli IP interni.
- Almeno due accessi di emergenza con MFA configurata.
- Credenziale Entra con **certificato** (niente segreto client) e utenti approvati uno per uno.

### Aggiornamento, backup, ripristino

```bash
docker compose pull && docker compose up -d          # aggiornamento
docker run --rm -v launcher-data:/data -v $PWD:/backup busybox \
    tar czf /backup/launcher-data-$(date +%F).tgz -C /data .   # backup del volume
```

Per ripristinare servono **volume + chiave master**: senza la chiave i segreti salvati non si
leggono più (vanno reinseriti dalla sezione admin).

---

## 4. Configurazione iniziale del portale

1. Aprire `https://launcher.example.com/Setup` e inserire il token preso dai log, la propria
   email e una password robusta: si crea il primo **accesso di emergenza** (amministratore locale).
2. Al primo accesso locale si configura la verifica in due passaggi (Microsoft Authenticator) e si
   salvano i codici di recupero, ad esempio come password in Hudu.
3. **Amministrazione → Impostazioni**
   - *Microsoft 365*: tenant ID e client ID → **Salva**; poi **Genera certificato**, scaricare il
     `.cer`, caricarlo in Entra (Certificati e segreti → Certificati) e premere **Prova e attiva**.
     La chiave privata resta cifrata nel portale e non si scarica. Il certificato dura 2 anni: un
     mese prima della scadenza la pagina di stato lo segnala; il rinnovo è lo stesso percorso e
     la credenziale attuale resta valida finché il nuovo certificato non è attivo.
   - *Hudu*: indirizzo `https://yourcompany.huducloud.com` e chiave → **Prova**
   - *RustDesk* (facoltativo): **Usa RustDesk come jump host**, indirizzo API, token, password di
     connessione → **Prova**. Per un client personalizzato (RustDesk Server Pro) si può indicare il
     percorso dell'eseguibile; il Launcher lo usa solo se il file esiste dentro Programmi. Con
     RustDesk spento il portale nasconde gateway e rubrica e apre solo connessioni dirette.
   - *Portale*: reti ammesse per l'accesso admin di emergenza (solo reti interne o VPN).
     L'opzione *connessioni senza Microsoft 365* lascia usare il Launcher agli admin entrati con
     utente locale: biglietto monouso valido solo dallo stesso IP del browser, registrato come
     "senza 365". Tenerla spenta in esercizio e accenderla solo per emergenze.
   - *Portale → Cache offline*: quanti asset un tecnico può preparare per l'uso senza internet
     (predefinito 10, 0 = funzione spenta) e per quanti minuti restano nel Launcher (predefinito 60).
     I tecnici selezionano gli asset con le caselle (anche un gruppo intero); la barra in basso
     offre **Offline** (grigio oltre il limite o se manca un IP), **Modifica in blocco** (gateway,
     credenziale se gli asset sono dello stesso cliente, protocollo, visibilità) e **Nascondi**.
   - *Desktop remoto*: predefiniti per tutte le connessioni RDP. Consigliati: solo appunti,
     tasti di Windows al computer remoto, desktop adattato alla scheda. Ogni asset (matita →
     *Opzioni Desktop remoto*) può cambiarli, ad esempio sessione /admin o WebAuthn.
   - *Terminale SSH e Telnet*: regole di colore `colore espressione`, una per riga.
4. **Stato e sincronizzazione → Sincronizza ora**
5. **Layout Hudu**: abilitare i layout collegabili (es. Virtual Machines, Physical Server,
   Firewalls, Switches) e verificare i campi proposti. Per avere il gateway RustDesk letto da Hudu,
   indicare il campo *RustDesk ID* nel layout *VPN & Remote Access* (va prima aggiunto al layout).
   Gli indirizzi si prendono dall'**IPAM** di Hudu: l'IP va collegato all'asset (Related Items →
   IP Addresses) e il layout va abilitato. Nell'editor di un asset si sceglie solo tra gli IP
   dell'IPAM; se l'asset non ne ha, il portale lo segnala ("va caricato") e usa per ora il campo
   IP del layout; se ne ha più di uno, chiede di scegliere quello giusto (vale il campo IP del
   layout, se coincide con uno di questi). Dopo una modifica in Hudu, **Aggiorna da Hudu** nella
   pagina del cliente rilegge subito solo quel cliente (al massimo una volta al minuto).
6. **Accessi di emergenza**: crearne almeno un secondo (password robusta + app di autenticazione).
7. Entrare con **Accedi con Microsoft 365**: la richiesta finisce in attesa. Con l'accesso di
   emergenza approvarla in **Utenti → Come admin**; da quel momento l'accesso locale serve solo per
   le emergenze. I colleghi che entrano dopo si approvano allo stesso modo (il pulsante Admin mostra
   quante richieste sono in attesa).

---

## 5. App Windows (Hudu Launcher)

### Compilazione

La pipeline **Hudu Launcher per Windows** su GitHub produce, a ogni modifica, l'artefatto
`HuduLauncher-<versione>` con `HuduLauncher-win-Setup.exe`. Per una versione ufficiale si crea un
tag `vX.Y.Z` (es. `git tag v1.1.0 && git push origin v1.1.0`): la pipeline pubblica l'installer,
i pacchetti di aggiornamento e `SHA256SUMS.txt` nelle **Release** del repository.

L'indirizzo del portale si può inserire nell'app con la variabile **Settings → Secrets and
variables → Actions → Variables** `LAUNCHER_PORTAL_URL`. In un repository pubblico l'installer è
scaricabile da chiunque: meglio lasciarla vuota e impostare l'indirizzo con Intune (registro, vedi
sotto) o lasciarlo imparare dal primo link.

Se l'indirizzo non è impostato in nessuno dei due modi, il Launcher lo **chiede al primo link**
aperto dal portale ("Usare questo indirizzo come portale?") e lo ricorda; si cambia
dall'ingranaggio in basso a destra. Un link che arriva da un portale diverso da quello impostato
viene rifiutato.

### Distribuzione con Intune

L'installazione è **per utente**, in `%LocalAppData%\HuduLauncher`, senza diritti
amministrativi. L'app registra da sola il protocollo `hudulauncher://` per l'utente.

1. Preparare il pacchetto: `IntuneWinAppUtil.exe -c <cartella> -s HuduLauncher-win-Setup.exe -o <output>`
2. **App → Windows → Aggiungi → App di Windows (Win32)**
   - Comando di installazione: `HuduLauncher-win-Setup.exe --silent`
   - Comando di disinstallazione: `"%LocalAppData%\HuduLauncher\Update.exe" --uninstall --silent`
   - Comportamento di installazione: **Utente**
   - Regola di rilevamento: file `%LocalAppData%\HuduLauncher\current\HuduLauncher.exe` esistente
   - Assegnazione: gruppo dei tecnici
3. Nuove versioni: caricare il nuovo Setup come nuova app che sostituisce la precedente
   (supersedence) oppure aggiornare il pacchetto.

Il runtime WebView2 (terminale SSH/Telnet) è già presente in Windows 10 e 11.

### Impostazioni facoltative nel registro

Chiave `HKCU\Software\Policies\HuduLauncher` (o `HKLM\...` per tutto il PC):

| Valore | Tipo | Uso |
|---|---|---|
| `PortalUrl` | Stringa | Indirizzo del portale, se non inserito in compilazione |
| `RustDeskPath` | Stringa | Percorso del client RustDesk, se non è trovato da solo (link `rustdesk://` registrato, `C:\Program Files\RustDesk` o una cartella con "rustdesk" nel nome). Ha la precedenza sul percorso impostato nel portale |

### Niente richiesta "Aprire hudulauncher?" nel browser

Intune → **Catalogo impostazioni → Microsoft Edge** (e Google Chrome se usato) → *Define a list of
protocols that can launch an external application from listed origins without prompting the user*:

```json
[{"protocol": "hudulauncher", "allowed_origins": ["https://launcher.example.com"]}]
```

---

## 6. Prima prova sul campo

1. Accedere al portale con 365, aprire un cliente con gateway impostato, premere **RDP** su un server.
2. Il browser apre il Launcher: passi *Accesso 365 → Richiesta al portale → Tunnel RustDesk → Connessione*.
3. La scheda RDP entra con le credenziali di Hudu. La finestra "Port Forward" di RustDesk viene
   ridotta a icona e chiusa quando si chiude l'ultima scheda.
4. Se qualcosa non va: pulsante **registri** in basso a destra nel Launcher
   (`%LocalAppData%\HuduLauncherData\logs`) e **Amministrazione → Registro accessi** nel portale.

## 7. Limiti noti

| Limite | Effetto | Soluzione definitiva |
|---|---|---|
| RustDesk non accetta da riga di comando porta e destinazione del tunnel | Il Launcher scrive la regola nel file del dispositivo e apre `--port-forward` | Richiesta a RustDesk: inoltrare `--port-forward <ID> <porta> <host> <porta>` dall'interfaccia al motore (già supportato dal motore) |
| Se un tunnel verso lo stesso gateway è già aperto, RustDesk non rilegge il file | Il Launcher riapre i tunnel: le sessioni RDP si riconnettono da sole, quelle SSH vanno riaperte | Come sopra |
| La password di RustDesk passa come argomento a RustDesk | Visibile per un istante ai processi dello stesso utente | Usare la password della rubrica condivisa anche da riga di comando (richiesta a RustDesk) |
| Primo collegamento a un dispositivo mai usato da quel PC | RustDesk potrebbe non trovare il file del dispositivo | Collegarsi una volta dalla rubrica di RustDesk |
| Certificato RDP non verificabile (ci si collega a 127.0.0.1) | Nessun avviso del certificato | Canale già cifrato end-to-end da RustDesk |
