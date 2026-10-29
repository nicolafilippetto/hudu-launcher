# Configurazione di Microsoft 365 (Entra ID)

Una sola **registrazione app** serve sia al portale web (accesso con Microsoft 365) sia al
Launcher per Windows (token per ricevere le connessioni). Questa guida la crea passo per passo.
I nomi dei menu sono quelli, in inglese, del [centro di amministrazione di Entra](https://entra.microsoft.com).

Nella guida:

| Segnaposto | Significato |
|---|---|
| `launcher.example.com` | Indirizzo pubblico del portale |
| `<client-id>` | *Application (client) ID* della registrazione (passo 1) |
| `<tenant-id>` | *Directory (tenant) ID* del vostro tenant (passo 1) |

Tempo richiesto: circa 15 minuti. Serve un ruolo che possa registrare app e concedere il consenso
amministratore (es. *Application Administrator* o *Cloud Application Administrator*).

---

## Riepilogo

Alla fine la registrazione deve avere:

| Sezione | Cosa | Valore |
|---|---|---|
| Authentication | Web | `https://launcher.example.com/signin-oidc` |
| Authentication | Mobile and desktop applications | `ms-appx-web://microsoft.aad.brokerplugin/<client-id>` e `https://login.microsoftonline.com/common/oauth2/nativeclient` |
| Authentication → Settings | Allow public client flows | **No** |
| Expose an API | Application ID URI | `api://<client-id>` |
| Expose an API | Scope | `Launcher.Connect`, *Admins only*, *Enabled* |
| API permissions | Microsoft Graph (Delegated) | `openid`, `profile`, `email` |
| API permissions | Hudu Launcher (Delegated) | `Launcher.Connect` |
| API permissions | Consenso | *Granted for <tenant>* su tutte e quattro |
| Manifest | `accessTokenAcceptedVersion` | `2` |
| Certificates & secrets | Credenziale | Certificato generato dal portale (o, all'inizio, un segreto) |
| Enterprise applications | Assignment required | **Yes**, con il gruppo dei tecnici |

---

## 1. Nuova registrazione

**Entra admin center → Identity → Applications → App registrations → New registration**

- **Name**: `Hudu Launcher`
- **Supported account types**: *Accounts in this organizational directory only (single tenant)*
- **Redirect URI**: piattaforma **Web**, `https://launcher.example.com/signin-oidc`

**Register**, poi dalla pagina **Overview** annotare *Application (client) ID* (`<client-id>`) e
*Directory (tenant) ID* (`<tenant-id>`): vanno inseriti nel portale.

Consigliato: **Owners → Add owners** e aggiungere sé stessi (e un collega). Oltre a facilitare la
gestione, serve perché l'API compaia in *My APIs* al passo 4.

## 2. Authentication: indirizzi di ritorno

**Authentication** (nella nuova interfaccia *Authentication (Preview)* → *Redirect URI configuration*).

1. La riga **Web** con `https://launcher.example.com/signin-oidc` c'è già dal passo 1.
2. **Add Redirect URI** (o, nell'interfaccia classica, **Add a platform**) → **Mobile and desktop
   applications**:
   - spuntare `https://login.microsoftonline.com/common/oauth2/nativeclient`;
   - nel campo dell'URI personalizzato inserire
     `ms-appx-web://microsoft.aad.brokerplugin/<client-id>`;
   - lasciare senza spunta `login.live.com` (account Microsoft personali) e `msal<client-id>://auth`.

   Il primo indirizzo è quello del broker di Windows (WAM), con cui il Launcher usa in silenzio
   l'account già collegato al PC; il secondo è l'indirizzo standard delle app desktop, usato se il
   broker non è disponibile.
3. Scheda **Settings**: **Allow public client flows: No**. Il Launcher non ne ha bisogno, e così
   restano bloccati flussi come il *device code*, molto usato nel phishing dei token.

## 3. Expose an API: il permesso del Launcher

**Expose an API**

1. **Application ID URI → Add/Edit**: accettare il valore proposto `api://<client-id>` → **Save**.
2. **Add a scope**:

   | Campo | Valore |
   |---|---|
   | Scope name | `Launcher.Connect` |
   | Who can consent? | **Admins only** |
   | Admin consent display name | `Connessioni tramite Hudu Launcher` |
   | Admin consent description | `Permette al Launcher per Windows di aprire, per conto dell'utente collegato, le connessioni preparate nel portale` |
   | User consent display name / description | vuoti |
   | State | **Enabled** |

   **Add scope**. Con *Admins only* nessun utente può concedere il permesso da solo: lo concede
   l'amministratore per tutti al passo 4.
3. **Authorized client applications**: lasciare vuoto.

Il portale mostra questo valore in *Impostazioni → Microsoft 365* come "Scope per il connettore
Windows" (`api://<client-id>/Launcher.Connect`): il Launcher lo scarica da solo, non va inserito
da nessuna parte.

## 4. API permissions

**API permissions**

1. **Add a permission → Microsoft APIs → Microsoft Graph → Delegated permissions** → nella sezione
   *OpenId permissions* spuntare `openid`, `profile`, `email` → **Add permissions**.
2. **Add a permission → My APIs → Hudu Launcher → Delegated permissions** → spuntare
   `Launcher.Connect` → **Add permissions**.
   - Se *My APIs* è vuota (non si è tra gli *Owners* della registrazione): scheda **APIs my
     organization uses**, cercare `<client-id>` o `Hudu Launcher`; oppure aggiungersi in **Owners**
     e riprovare dopo un minuto.
3. Se c'è `User.Read` (aggiunto in automatico da Entra): **… → Remove permission**. Il portale non
   legge nulla da Microsoft Graph.
4. **Grant admin consent for <tenant>** → **Yes**.

Risultato atteso in *Configured permissions*: quattro righe, tutte con *Granted for <tenant>*:
`Launcher.Connect` (Hudu Launcher) ed `email`, `openid`, `profile` (Microsoft Graph). La sezione
*Other permissions granted* resta vuota. L'avviso arancione sul consenso a livello di tenant si può
ignorare: il nuovo consenso contiene già tutti i permessi necessari.

## 5. Manifest

**Manifest** → impostare `"accessTokenAcceptedVersion": 2` (se è `null`) → **Save**. I token del
Launcher useranno il formato v2, quello consigliato.

## 6. Credenziale del portale: certificato

Il portale deve dimostrare a Entra di essere la vostra app. Il modo consigliato è un **certificato**:
lo genera il portale, la chiave privata resta cifrata sul server e in Entra si carica solo la parte
pubblica. Nessun segreto da copiare, conservare o rinnovare a mano.

Il certificato si genera dopo aver installato il portale ([INSTALLAZIONE.md](INSTALLAZIONE.md),
sezione 4):

1. Nel portale: **Amministrazione → Impostazioni → Microsoft 365**, inserire `<tenant-id>` e
   `<client-id>` → **Salva** → **Genera certificato** → scaricare il file `.cer`.
2. In Entra: **Certificates & secrets → Certificates → Upload certificate** → scegliere il `.cer`
   → **Add**. L'impronta (*Thumbprint*) deve coincidere con quella mostrata dal portale.
3. Nel portale: **Prova e attiva**. Se Entra accetta il certificato, diventa la credenziale attiva.
4. In Entra: eliminare l'eventuale segreto client (**Certificates & secrets → Client secrets**).

In alternativa, per una prima prova, si può usare un segreto: **Certificates & secrets → Client
secrets → New client secret**, copiare il *Value* nel portale e passare al certificato appena possibile.

**Rinnovo**: il certificato dura 2 anni; un mese prima della scadenza la pagina di stato del portale
lo segnala. Si ripetono i passi 1-3 (la credenziale attuale resta valida finché il nuovo certificato
non è attivo), poi si elimina il vecchio certificato in Entra.

## 7. Enterprise applications: chi può entrare

**Identity → Applications → Enterprise applications → Hudu Launcher**

1. **Properties → Assignment required?: Yes** → **Save**.
2. **Users and groups → Add user/group**: il gruppo dei tecnici (e degli amministratori del portale).

Chi non è assegnato non riceve token né per il portale né per il Launcher. È la prima barriera; la
seconda è il portale stesso: al primo accesso ogni utente resta *in attesa* finché un
amministratore non lo approva in **Amministrazione → Utenti**.

## 8. Accesso condizionale (consigliato)

**Protection → Conditional Access → Create new policy**

- **Target resources**: l'app *Hudu Launcher*.
- **Users**: il gruppo dei tecnici (escludere gli account di emergenza del tenant).
- **Grant**: *Require multifactor authentication* (meglio *Require authentication strength →
  Phishing-resistant MFA*) e, se i PC sono gestiti, *Require device to be marked as compliant*.

Consigliato anche un criterio a livello di tenant che blocca il *device code flow*
(**Conditions → Authentication flows → Device code flow → Block**).

---

## Problemi frequenti

| Messaggio | Causa | Soluzione |
|---|---|---|
| `AADSTS50011` (redirect URI non corrisponde) | Indirizzo del portale diverso da quello registrato, o il proxy non conserva l'intestazione `Host` | Controllare la riga **Web** al passo 2 e la configurazione del WAF |
| `AADSTS65001` (consenso richiesto) nel Launcher | Manca `Launcher.Connect` o il consenso amministratore | Passo 4, poi **Grant admin consent** |
| `AADSTS50105` (utente non assegnato) | *Assignment required* attivo e utente fuori dal gruppo | Passo 7 |
| `AADSTS700016` / `AADSTS90002` | `<client-id>` o `<tenant-id>` errati nel portale | Copiarli di nuovo dalla pagina **Overview** |
| `AADSTS700027` / `AADSTS7000215` | Certificato non caricato in Entra (o segreto scaduto/errato) | Passo 6; **Prova e attiva** indica l'errore preciso |
| *Richiesta di accesso registrata* nel portale | Primo accesso dell'utente | Approvarlo in **Amministrazione → Utenti** |
| Il Launcher chiede sempre l'account | Broker di Windows non disponibile o URI del broker mancante | Passo 2 (URI `ms-appx-web://…`), PC aggiunto a Entra |
