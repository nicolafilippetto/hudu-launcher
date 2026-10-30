# Hudu Launcher — Scheda di sicurezza

Per il team Cyber: cosa custodisce il sistema, come lo protegge e come viene verificato.
Stato: ottobre 2026.

## 1. Cosa custodisce il server

| Dato | Dove | Protezione |
|---|---|---|
| Password dei clienti | **Non salvate.** Lette da Hudu solo al momento della connessione, consegnate al Launcher e scartate | Mai su disco, mai nei log |
| Elenco asset, IP, nomi delle credenziali e nomi utente | Cache SQLite nel volume Docker | Solo metadati, nessun segreto |
| Chiave API di Hudu, token RustDesk, password di connessione RustDesk, chiave privata del certificato Entra | Database, **cifrati** (ASP.NET Data Protection, AES) | Le chiavi di cifratura sono a loro volta cifrate (AES-GCM) con la chiave master, fuori dal database (Docker secret, file 400 di proprietà del container) |
| Account admin di emergenza | Database (ASP.NET Identity) | Password con hash PBKDF2, MFA TOTP obbligatoria, codici di recupero monouso |
| Registro accessi | Database | Chi, cosa, quando, da quale IP; conservazione configurabile |

Senza la chiave master, il database copiato non permette di leggere i segreti.

## 2. Hudu: solo lettura

La chiave API di Hudu non si può limitare alla sola lettura. Il blocco è quindi nel codice: il
client HTTP verso Hudu ha un filtro (`ReadOnlyHandler`) che **rifiuta qualsiasi richiesta che non
sia GET** prima che parta, ed è verificato dai test automatici (POST, PUT, PATCH e DELETE bloccati).
Il portale non ha alcuna funzione di scrittura verso Hudu.

Mitigazioni consigliate lato Hudu: chiave dedicata solo a questo server, limitata all'IP pubblico del
server (se la licenza lo consente), rotazione periodica, alert sugli accessi API.

## 3. Chi può fare cosa

- **Utenti Microsoft 365**: accesso con OIDC (codice + PKCE), solo dal proprio tenant. Al primo accesso
  l'utente resta **in attesa** e non vede nulla finché un amministratore non lo approva (come tecnico o
  admin). È riconosciuto dall'**object ID** Entra, immutabile, non dall'email; bloccato o rimosso perde
  portale e Launcher entro un minuto. In Entra restano consigliati assegnazione utente obbligatoria e
  Accesso condizionale con MFA: le due barriere sono indipendenti.
- **Accessi di emergenza**: utenti locali, sempre admin, password + MFA TOTP, solo da reti interne.
- **Credenziale del portale verso Entra**: certificato generato dal portale (RSA 3072, 2 anni); la chiave
  privata è cifrata nel database e non si scarica, in Entra c'è solo il certificato pubblico. Al login il
  portale firma un'asserzione valida 5 minuti invece di inviare un segreto.
- **Launcher (Windows)**: per ricevere una credenziale serve un **biglietto** monouso (256 bit casuali,
  30 secondi, legato all'utente; nel database c'è solo l'hash) **e** un token Microsoft 365 dello stesso
  utente, ancora approvato nel portale, con scope dedicato. Un link `hudulauncher://` contraffatto non contiene host né credenziali e
  viene rifiutato.
- **Connessione diretta** (senza RustDesk): stesso biglietto e stessa autenticazione; il Launcher non
  riceve la password di RustDesk, che non gli serve.
- **Cache d'emergenza offline**: il tecnico sceglie nel portale fino a N asset (N deciso dall'admin,
  predefinito 10; 0 = funzione spenta). Il Launcher, con biglietto e token 365 come sempre, riceve le
  connessioni dirette con le credenziali e le tiene **solo in memoria** per la durata impostata
  (predefinita 60 minuti): mai su disco, cancellate alla scadenza, chiudendo il Launcher o spegnendo il
  PC; un nuovo pacchetto sostituisce il precedente. Ogni preparazione e ogni asset consegnato sono nel
  registro (`offline.prepare`, `offline.redeem`).
- **Pagine web nel Launcher**: aprendo la pagina il portale non invia password, solo nome e utente
  delle credenziali proponibili (collegate all'asset, scelta per la connessione o con l'URL sullo
  stesso host) e un'autorizzazione firmata per quella scheda (10 ore, legata a utente, asset,
  connessione e credenziali). Con "Accesso" il Launcher chiede la password della sola credenziale
  scelta: servono l'autorizzazione e un token 365 dello stesso utente ancora approvato (o, per gli
  admin locali, lo stesso indirizzo); opzione dell'asset e credenziali si ricontrollano in quel
  momento, la password si legge da Hudu nel container (la chiave API non esce mai) e ogni richiesta
  è nel registro (`web.login`, rifiuti in `web.login.reject`). Il Launcher la scrive solo nei campi
  di una pagina sull'host dell'asset e non la conserva; gli strumenti per sviluppatori sono spenti.
  Gli URL con utente e password vengono rifiutati. Ogni scheda ha un profilo del browser proprio (cookie
  separati tra clienti con gli stessi IP), cancellato alla chiusura salvo l'opzione "Ricorda per
  questo asset"; salvataggio password e compilazione automatica sono spenti. Il proxy SOCKS5 della
  scheda ascolta solo su `127.0.0.1` e porta nel tunnel unicamente host e porta dell'asset. I
  certificati non validi si accettano solo per l'host dell'asset, secondo l'opzione scelta
  (predefinito: chiedi e ricorda l'impronta, con avviso se cambia).

## 4. Difese dell'applicazione web

- Input validati lato server (IP/hostname, porte, ID RustDesk, opzioni da elenco chiuso, espressioni
  regolari compilate prima del salvataggio). Database interrogato solo con query parametrizzate (EF Core).
- Output sempre codificato (Razor), **Content Security Policy** senza script inline, frame vietati,
  HSTS, `nosniff`, `no-referrer`, isolamento tra origini.
- Token antiforgery su tutti i form e le API del portale; cookie `__Host-`, HttpOnly, Secure, SameSite.
- Limiti per IP: login, MFA e Setup 10 ogni 5 minuti; connessioni e riscatti 30 ogni 5 minuti;
  aggiornamento da Hudu una volta al minuto per cliente. Blocco dell'account dopo 5 errori
  (password o MFA). Messaggi di errore che non rivelano se un utente esiste.
- Accesso di emergenza rifiutato se il proxy davanti al portale non è riconosciuto (IP reale non
  verificabile).
- Upload solo di immagini raster riconosciute dal contenuto (niente SVG), richieste massimo 2 MB.
- Container non root, filesystem in sola lettura, nessuna capability, nessuna shell.

## 5. Verifiche eseguite

| Tipo | Strumento | Esito |
|---|---|---|
| SAST C# | Analizzatori di sicurezza .NET (`AnalysisModeSecurity=All`), **attivi in ogni compilazione**: un avviso blocca la build | Server: nessun problema. Launcher: 1 avviso (ricerca delle DLL di sistema, CA5392) **corretto** |
| SAST multi-linguaggio e segreti | Semgrep 1.182 (regole C#, JavaScript, OWASP Top 10, segreti, Dockerfile, GitHub Actions) | 6 segnalazioni: 4 corrette (attesa sugli aggiornamenti Dependabot), 2 falsi positivi motivati nel codice → **0 aperte** |
| Dipendenze | `dotnet list package --vulnerable` + Dependabot | Nessuna libreria vulnerabile |
| DAST | OWASP ZAP 2.16.1, scansione attiva **con sessione da amministratore**, in modalità produzione | 1 difetto reale: il pulsante Esci non inviava il token antiforgery (logout in errore, nessun rischio CSRF perché la richiesta veniva rifiutata) → **corretto e coperto da test**. Altri avvisi analizzati: falsi positivi ("XSLT" dal testo "Microsoft 365") o comportamenti voluti (IP dei clienti mostrati ai tecnici autenticati) |
| Test automatici | 92 test server, 62 Launcher | Includono: biglietti rubati/scaduti/riusati, token di altro tenant o senza scope, utenti in attesa o bloccati, asserzione del certificato, solo lettura verso Hudu, antiforgery, ruoli |

In CI (GitHub Actions) girano a ogni modifica, e ogni settimana anche senza modifiche:

- **Semgrep** e controllo delle librerie vulnerabili (workflow *Sicurezza*);
- **CodeQL** con le regole di sicurezza estese su C#, JavaScript e workflow (workflow *CodeQL*);
- **revisione delle dipendenze** nelle pull request: blocca librerie o azioni con vulnerabilità note;
- gli analizzatori .NET, in ogni compilazione.

I risultati di Semgrep e CodeQL sono in **Security → Code scanning**. Le azioni di GitHub sono
fissate per hash del commit e Dependabot propone gli aggiornamenti dopo 7 giorni dall'uscita.

## 6. Rischi residui e raccomandazioni

- **Compromissione del server**: chi ottiene il controllo del container può usare la chiave di Hudu.
  Mitigazioni: server dedicato in DMZ, aggiornamenti, accesso amministrativo limitato, backup della
  chiave master separato dal backup del volume.
- **Compromissione di un account tecnico 365**: permette connessioni come quel tecnico. Mitigazioni:
  Accesso condizionale (MFA resistente al phishing, dispositivo conforme), assegnazione utenti al gruppo.
- **Accesso web compilato**: la password finisce nel campo della pagina dell'apparato, come se il
  tecnico la digitasse; le pagine con il pulsante "mostra password" la renderebbero visibile.
  L'opzione si può spegnere per tutti o per singolo asset.
- **Proxy locale delle pagine web**: finché la scheda è aperta, un altro programma sullo stesso PC
  potrebbe usare il proxy su `127.0.0.1` per raggiungere l'apparato, come già vale per la porta
  locale del tunnel RustDesk. Antivirus/EDR e PC gestiti.
- **PC del tecnico**: durante la sessione, e per la durata della cache offline, le credenziali sono in
  memoria del Launcher. Antivirus/EDR e Intune sui PC; durata e numero di asset offline limitati dall'admin.
- Un codice MFA resta valido nella sua finestra di ~30 secondi.
- Consigliato prima del go-live: **penetration test indipendente** sull'istanza pubblicata dietro il WAF.
