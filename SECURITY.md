# Sicurezza

## Segnalare una vulnerabilità

Non aprire una issue pubblica. Usa **Security → Report a vulnerability** di questo repository
(segnalazione privata di GitHub) indicando:

- versione del portale (immagine Docker) o del Launcher;
- passi per riprodurre il problema e impatto previsto;
- eventuale proposta di correzione.

Riceverai una risposta appena possibile; la correzione viene pubblicata in una nuova versione e,
se serve, in un avviso di sicurezza del repository.

## Versioni supportate

Si corregge solo l'ultima versione pubblicata (tag `vX.Y.Z` e immagine `latest`).

## Cosa sapere prima di installare

Il portale custodisce la chiave API di Hudu, che dà accesso alle password dei clienti: va
installato secondo [`docs/INSTALLAZIONE.md`](docs/INSTALLAZIONE.md) (dietro un WAF/proxy inverso,
porta non esposta, chiave master fuori dal volume dati). Protezioni, verifiche e rischi residui
sono descritti in [`docs/SICUREZZA.md`](docs/SICUREZZA.md).
