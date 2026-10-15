# Creare l'installer di Elledi Transfer

Questa procedura produce `dist\EllediTransfer-Setup-<versione>.exe`, un unico file che installa
l'applicazione grafica (Elledi Transfer) e lo strumento a riga di comando (`EllediTransfer.exe`).

## Requisiti (una volta sola)

- **.NET SDK 8 o successivo**: verifica con `dotnet --list-sdks`.
- **Inno Setup 6**: scaricalo da <https://jrsoftware.org/isdl.php> e installalo con le opzioni
  predefinite. Lo script lo cerca in `C:\Program Files (x86)\Inno Setup 6`,
  `C:\Program Files\Inno Setup 6` o `%LOCALAPPDATA%\Programs\Inno Setup 6`.

Sul PC di destinazione non serve installare .NET: l'applicazione viene pubblicata
"self-contained", con il runtime incluso.

## Procedura

1. **Chiudi Elledi Transfer** e ferma il debug in Visual Studio. Se l'app è aperta, i suoi file
   restano bloccati e la pubblicazione non riesce.

2. **Aggiorna il numero di versione** in `EllediTransfer.Gui\EllediTransfer.Gui.csproj`, ad esempio:

   ```xml
   <Version>1.1.0</Version>
   ```

   Usa sempre una versione più alta della precedente: così l'installer aggiorna
   l'installazione esistente invece di affiancarne un'altra.

3. **Apri PowerShell nella cartella della soluzione** (quella che contiene `EllediTransfer.slnx`)
   ed esegui:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
   ```

   Per forzare una versione senza modificare il progetto:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Version 1.1.0
   ```

4. **Prendi l'installer** in `dist\EllediTransfer-Setup-<versione>.exe`. Lo script stampa il
   percorso esatto alla fine ("Installer pronto: …").

La build richiede qualche minuto: la prima volta scarica anche il runtime .NET per `win-x64`.

## Cosa fa lo script

`build-installer.ps1` esegue tre passi:

1. legge la versione da `EllediTransfer.Gui.csproj`, salvo che venga passata con `-Version`;
2. pubblica `EllediTransfer.Gui` e `EllediTransfer` (riga di comando) in Release, `win-x64`, self-contained,
   nella cartella `publish\app`, che viene svuotata a ogni build;
3. compila `installer\EllediTransfer.iss` con Inno Setup, che crea l'eseguibile in `dist`.

## Installare e aggiornare

- Avvia `EllediTransfer-Setup-<versione>.exe`. Di norma installa **solo per l'utente corrente**
  in `%LOCALAPPDATA%\Programs\Elledi Transfer`, senza permessi di amministratore. Se l'installer
  viene avviato da un amministratore, propone anche l'installazione per tutti gli utenti.
- Crea il collegamento **Elledi Transfer** nel menu Start; l'icona sul desktop è facoltativa.
- Per aggiornare basta eseguire l'installer della nuova versione: chiude l'app se è aperta e
  sostituisce i file. Impostazioni, server attendibili e ultima connessione restano in
  `%APPDATA%\EllediTransfer\settings.json` e non vengono toccati. Se manca, al primo avvio
  vengono lette quelle delle versioni precedenti in `%APPDATA%\WinScp\settings.json`.
- Per disinstallare: **Impostazioni → App → App installate → Elledi Transfer → Disinstalla**.

Il WinSCP originale, se presente, resta installato e non viene modificato: nome, cartella e
collegamenti sono diversi.

## File coinvolti

| File | Ruolo |
|------|-------|
| `build-installer.ps1` | Script da eseguire: pubblicazione e creazione dell'installer |
| `installer\EllediTransfer.iss` | Configurazione di Inno Setup (nome, cartella, collegamenti, lingua) |
| `EllediTransfer.Gui\EllediTransfer.Gui.csproj` | Versione dell'applicazione e icona |
| `EllediTransfer.Gui\Assets\app.ico` | Icona di applicazione, installer e collegamenti |
| `publish\app\` | File pubblicati (generata, si può cancellare) |
| `dist\` | Installer prodotti (generata) |

Nel file `.iss` non cambiare mai la riga `AppId`: è ciò che permette alle nuove versioni di
riconoscere e aggiornare l'installazione esistente.

## Problemi comuni

- **"Inno Setup 6 non trovato"**: installa Inno Setup 6, oppure aggiungi il suo percorso
  all'elenco `$iscc` in `build-installer.ps1`.
- **"Pubblicazione di … non riuscita" con file in uso**: l'app è ancora aperta, o il debug di
  Visual Studio è attivo. Chiudila e riesegui lo script.
- **Lo script non parte ("l'esecuzione di script è disabilitata")**: usa il comando con
  `-ExecutionPolicy Bypass` indicato sopra.
