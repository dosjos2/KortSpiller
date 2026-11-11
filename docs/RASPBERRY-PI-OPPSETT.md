# Raspberry Pi-oppsett for Kortspiller (detaljert)

Målet: en Raspberry Pi uten skjerm som ved oppstart starter seg selv, kobler til Wi-Fi, logger inn på Spotify og spiller
sangen som hører til kortet du holder mot leseren.

> **Ærlig forbehold:** Programvaren er testet på PC. Maskinvaredelen (leser, potmeter, knapp) er skrevet etter
> dokumentasjonen til `System.Device.Gpio` / `Iot.Device.Bindings`, men er **ikke prøvd på ekte maskinvare**. Jeg
> kunne heller ikke åpne produktsidene til leserne dine (AliExpress/RS blokkerte), så jeg har **antatt**:
> *AliExpress-leseren = RC522 (SPI, 13,56 MHz)* og *RS-leseren = PN532 NFC-modul eller en USB-leser*.
> Sjekk typenavnet som står på kortet ditt og velg riktig kapittel.

---

## 0. Innkjøpsliste

* Raspberry Pi 3B/3B+/4/Zero 2 W (anbefalt – enkel og godt støttet). Pi 5 går også, men GPIO-biblioteket er nyere.
* microSD-kort 16 GB+, god strømforsyning (5 V / 2,5 A+).
* Lyd: 3,5 mm-utgang (Pi 3/4), **USB-lydkort**, eller I²S-DAC (PCM5102 / HiFiBerry) for best lyd.
* Aktiv høyttaler eller forsterker.
* RFID-leser (RC522 eller PN532), RFID-kort/brikker (MIFARE 13,56 MHz).
* Valgfritt: MCP3008 (ADC), 10 kΩ lineær potmeter, trykknapp, LED + 330 Ω, koblingsbrett og hunn-hunn-/hann-hunn-ledninger.
* Spotify **Premium**-konto.

---

## 1. Legg Raspberry Pi OS på SD-kortet (headless)

1. Installer **Raspberry Pi Imager** på PC-en.
2. Velg *Raspberry Pi OS Lite (64-bit)* (Bookworm eller nyere).
3. Trykk tannhjul / **Rediger innstillinger**:
   * Vertsnavn: `kortspiller`
   * Aktiver **SSH** (passord eller – bedre – offentlig nøkkel)
   * Brukernavn/passord
   * **Wi-Fi**: SSID, passord, land = NO
   * Tidssone `Europe/Oslo`, tastatur `no`
4. Skriv til kortet, sett det i Pi-en og start den. Vent 1–2 minutter.
5. Fra PC: `ssh <bruker>@kortspiller.local` (eller finn IP-en i ruteren).

```bash
sudo apt update && sudo apt full-upgrade -y
sudo reboot
```

---

## 2. Koble maskinvaren (skru av strømmen først!)

Se tegningene **`docs/kobling-rc522.svg`** og **`docs/kobling-pn532.svg`**. Pinnenumre = fysiske pinner på 40-pinners header.

### 2.1 RC522 (SPI0, CE0) – kun 3,3 V!

| RC522 | Pi (fysisk pinne) | GPIO |
|---|---|---|
| 3.3V | 1 | 3V3 |
| GND | 6 | GND |
| RST | 22 | GPIO25 |
| MISO | 21 | GPIO9 |
| MOSI | 19 | GPIO10 |
| SCK | 23 | GPIO11 |
| SDA (SS) | 24 | GPIO8 (CE0) |
| IRQ | – | ikke tilkoblet |

### 2.2 Volum-potmeter via MCP3008 (SPI0, CE1)

Pi-en har ingen analoge innganger, derfor en liten ADC-brikke.

| MCP3008 | Kobles til |
|---|---|
| VDD (16) **og** VREF (15) | 3V3 (pinne 17) |
| AGND (14) **og** DGND (9) | GND (pinne 25) |
| CLK (13) | SCLK pinne 23 |
| DOUT (12) | MISO pinne 21 |
| DIN (11) | MOSI pinne 19 |
| CS (10) | CE1 / GPIO7, pinne 26 |
| CH0 (1) | **Midtbenet** på potmeteret |

Potmeterets to ytterben: det ene til 3V3, det andre til GND. Bytt om hvis volumet virker «baklengs».

### 2.3 Pauseknapp og LED

* Knapp: GPIO27 (pinne 13) → knapp → GND (pinne 14). Intern pull-up brukes (ingen motstand nødvendig).
* Status-LED: GPIO22 (pinne 15) → 330 Ω → LED anode (+); LED katode (−) → GND.
  * Lyser fast = klar · blinker rolig = spiller opp · blinker raskt = feil.

### 2.4 PN532 (hvis det er leseren du bruker)

**I²C** (anbefalt): VCC → pinne 1 (3V3), GND → pinne 9, SDA → pinne 3, SCL → pinne 5. DIP: I²C-modus.
**UART:** VCC → 3V3, GND → GND, Pi TXD (pinne 8) → PN532 RXD, Pi RXD (pinne 10) → PN532 TXD. DIP: HSU-modus.
Rekkefølgen på DIP-bryterne varierer mellom produsenter – les kortets trykk.

> Pinnene **GPIO18–21** brukes av I²S-lyd-DAC-er. Koblingen over holder seg unna dem.

---

## 3. Aktiver grensesnitt

```bash
sudo raspi-config
#  3 Interface Options → SPI  → Yes     (RC522, MCP3008)
#  3 Interface Options → I2C  → Yes     (kun PN532 over I²C)
#  3 Interface Options → Serial Port → login shell: No, hardware: Yes   (kun PN532 over UART)
sudo reboot
```

PN532 på I²C hos Pi trenger lavere hastighet. Legg til i `/boot/firmware/config.txt`:

```
dtparam=i2c_arm=on,i2c_arm_baudrate=100000
```

Sjekk at enhetene sees:

```bash
ls /dev/spidev*                 # forventer spidev0.0 og spidev0.1
sudo apt install -y i2c-tools && sudo i2cdetect -y 1   # PN532 vises som 24
```

---

## 4. Lyd og Spotify-enhet (raspotify)

Player styrer Spotify via nettet; selve lyden spilles av **raspotify** (librespot), som gjør Pi-en til en «Spotify Connect»-høyttaler.

```bash
sudo apt-get -y install curl
curl -sL https://dtcooper.github.io/raspotify/install.sh | sh
```

Rediger `/etc/raspotify/conf` (se `deploy/raspotify.conf.example`):

```
LIBRESPOT_NAME="Kortspiller"
LIBRESPOT_BITRATE="160"
LIBRESPOT_INITIAL_VOLUME="40"
# LIBRESPOT_DEVICE="plughw:CARD=Headphones,DEV=0"    # finn riktig med: aplay -L
```

```bash
sudo systemctl restart raspotify
sudo systemctl enable raspotify
```

### 4.1 Lydkort-valg
* **3,5 mm:** enklest. `sudo raspi-config` → System → Audio.
* **USB-lydkort:** `aplay -l` viser kortnavnet.
* **I²S-DAC (PCM5102/HiFiBerry):** legg til `dtoverlay=hifiberry-dac` i `config.txt` (og fjern/disable `dtparam=audio=on`).

### 4.2 Engangs-pålogging til Spotify (uten skjerm på Pi-en)
Raspotify logger inn via Spotify Connect («zeroconf»):

1. Pi-en og telefonen/PC-en med Spotify-appen må være på **samme nettverk**.
2. Åpne Spotify → spill noe → trykk **Enheter** → velg **Kortspiller**.
3. Raspotify lagrer innloggingen i `/var/cache/raspotify`, og kobler til automatisk ved hver oppstart.

Etter dette er enheten alltid synlig for Web API-et – også etter omstart. Test: spill noe i appen via «Kortspiller» og sjekk
at lyden kommer ut av høyttaleren.

*Alternativ hvis zeroconf ikke fungerer (f.eks. gjestenett/mDNS blokkert):* se raspotify-wikien for `--enable-oauth`
og logg inn én gang via SSH.

---

## 5. Lag en Spotify Developer-app (for Player)

1. Gå til <https://developer.spotify.com/dashboard> og logg inn med Premium-kontoen.
2. **Create app**. Navn: `Kortspiller`. **Redirect URI:** `http://127.0.0.1:8888/callback` (må være akkurat dette, `127.0.0.1` ikke `localhost`).
   Velg **Web API**.
3. Under *Settings* kopier **Client ID**. (Ingen client secret trengs – Player bruker PKCE.)
4. Appen er i *Development mode*, som holder fint til eget bruk. Legg eventuelt e-posten din under *User Management*.

---

## 6. Installer Player

### 6.1 Bygg på PC-en
```powershell
cd KortSpiller
.\deploy\publish.ps1            # 64-bit OS.  32-bit: -PiRid linux-arm
scp -r dist\pi <bruker>@kortspiller.local:~/kortspiller-install
```

### 6.2 Kjør installasjonsskriptet på Pi-en
```bash
cd ~/kortspiller-install
chmod +x install.sh
sudo ./install.sh
```
Skriptet installerer `libgpiod`, lager brukeren `kortspiller` (i gruppene `gpio spi i2c input`), kopierer filene til
`/opt/kortspiller` og registrerer systemd-tjenesten `kortspiller-player` som starter ved boot.

### 6.3 Konfigurer
Lag/rediger `/opt/kortspiller/appsettings.local.json` (overstyrer `appsettings.json`, og overskrives ikke ved oppdatering):

```json
{
  "Spotify": { "ClientId": "DIN_CLIENT_ID", "DeviceName": "Kortspiller" },
  "Mapping": { "Source": "https://DIN-SERVER/mappings.json" },
  "Player": { "Readers": [ { "Type": "Mfrc522", "SpiBus": 0, "SpiChipSelect": 0, "ResetPin": 25 } ] },
  "Volume": { "Enabled": true, "ChipSelect": 1, "Channel": 0 },
  "PauseButton": { "Enabled": true, "Pin": 27 },
  "StatusLed": { "Enabled": true, "Pin": 22 }
}
```

PN532 over I²C: `{ "Type": "Pn532", "Interface": "I2C", "I2cBus": 1, "I2cAddress": 36 }` (36 = 0x24).
PN532 over UART: `{ "Type": "Pn532", "Interface": "UART", "SerialPort": "/dev/serial0" }`.
USB-leser: `{ "Type": "UsbKeyboard", "DeviceName": "" }` (tom = første enhet som ser ut som en RFID-leser; ellers deler av navnet).

### 6.4 Logg inn Player mot Spotify (uten skjerm på Pi-en)

Enklest: gjør innloggingen på PC-en og kopier token-filen.

På PC-en (i `src\KortSpiller.Player`, med `Spotify:ClientId` satt i `appsettings.local.json`):

```powershell
dotnet run -- auth
```
Nettleseren åpnes; godkjenn. Token lagres i `data\spotify-token.json`. Kopier til Pi-en:

```powershell
scp data\spotify-token.json <bruker>@kortspiller.local:/tmp/
```
```bash
sudo mv /tmp/spotify-token.json /opt/kortspiller/data/ && sudo chown kortspiller: /opt/kortspiller/data/spotify-token.json
sudo systemctl restart kortspiller-player
```

Alternativt direkte på Pi-en over SSH: `cd /opt/kortspiller && sudo -u kortspiller ./KortSpiller.Player auth --manual`.
Åpne lenken på en annen maskin, godkjenn, og lim inn adressen du blir sendt til (siden vises som feil – det er normalt).

Token fornyes automatisk, og lagres på nytt hver gang Spotify roterer den.

### 6.5 Se at det virker
```bash
systemctl status kortspiller-player
journalctl -u kortspiller-player -f
```
Skann et kort: du skal se `Kort 04A1B2C3: spiller «…»`. Ukjente kort logges med ID-en slik at du kan legge dem inn i mapperen.

---

## 7. Mapperen: koble kort til sanger

Kjør `KortSpiller.Mapper` (fra `dist\mapper-win-x64` eller `dotnet run`) på PC-en, eller på en Pi med skjerm
(`dist/mapper-linux-arm64`). Åpne <http://localhost:5050>.

1. Hold kortet mot en **USB-leser** (feltet «Kort-ID» er i fokus, ID skrives inn og Enter flytter til lenkefeltet),
   eller sett `Mapper:Reader` i `appsettings.json` så vises **Skann**-knapp.
2. Lim inn Spotify-lenken (Spotify → ⋯ → Del → *Kopier lenke*). Låt, album, spilleliste, artist, podkast støttes.
   Tittel og omslagsbilde hentes automatisk.
3. **Lagre**. Bruk fanen *Kommando-kort* for pause/fortsett/neste/forrige.

### Hvordan får Player filen?

| Metode | Slik |
|---|---|
| **Azure Blob (enkelt og gratis nok)** | Lag en Storage-konto + container `kort`. Lag en SAS-URL (Read+Write+Create) til blobben `mappings.json`. Legg den i Mapper `Mapper:Publish:Url` med `Headers: { "x-ms-blob-type": "BlockBlob" }` → **Publiser**-knapp. Player leser samme URL (en egen read-only SAS) i `Mapping:Source`. |
| Egen webserver | `Mapper:Publish:Url` = `https://…/mappings.json` med HTTP PUT og `BearerToken`, eller last opp manuelt. |
| GitHub secret gist | Last opp filen, bruk «Raw»-URL som `Mapping:Source`. |
| Uten server | `scp mappings.json <bruker>@kortspiller.local:/opt/kortspiller/data/mappings.json` og sett `Mapping:Source` til `data/mappings.json` (filsti; oppdateringer plukkes opp automatisk). |

Player husker siste fil i `data/mappings.cache.json` og virker derfor også uten nett.

---

## 8. Forslag til bruk av begge leserne dine

1. **Beste fordeling (anbefalt):** RC522 (billig, SPI) **på Pi-en** som spillerens leser. PN532/USB-leseren **ved PC-en** som
   «skanner» i mapperen. Da slipper du å koble lesere om hverandre. Mapperen gjenkjenner kortet uansett hvilken leser som ble brukt,
   fordi hex-ID (RC522/PN532) og desimal-ID (de fleste USB-lesere) behandles som samme kort.
2. **Begge på Pi-en samtidig:** Legg to oppføringer i `Player:Readers` (f.eks. RC522 på SPI og PN532 på I²C). Bruk RC522 til
   album/sanger og den andre som «kommandostasjon» med kommando-kort (pause, neste, forrige) i hyggelige former
   (f.eks. kort limt på en kloss). Begge leses parallelt.
3. **Én leser per bruker:** Én på stuebordet, én på barnerommet – kjør to Pi-er med hver sin `DeviceName` og samme `mappings.json`.
4. **PN532 som «fremtidssikker» leser:** PN532 leser også NFC-tagger/telefoner. Bruk den i stedet for RC522 hvis du får problemer
   med rekkevidde eller vil bruke NFC-stickers.
5. **USB-leser på Pi-en:** Settes opp som `UsbKeyboard` – krever ingen kobling og er den enkleste «plug and play»-løsningen
   (gruppen `input` er satt opp av installasjonsskriptet).

---

## 9. Feilsøking

| Symptom | Tiltak |
|---|---|
| Ingen kort leses (RC522) | Sjekk at SPI er på (`ls /dev/spidev*`), 3,3 V (ikke 5 V!), ledninger korte, RST på GPIO25. |
| `Fant ikke Spotify-enheten «Kortspiller»` | `systemctl status raspotify`; velg enheten én gang i Spotify-appen; samme navn i `LIBRESPOT_NAME` og `Spotify:DeviceName`. |
| `Ingen Spotify-innlogging funnet` | Kjør `auth` (kap. 6.4) og kopier `spotify-token.json` til `/opt/kortspiller/data/`. |
| 401/403 fra Spotify | Konto uten Premium, eller token tilbakekalt → kjør `auth` på nytt. |
| Volum hopper / støyer | Legg 100 nF kondensator mellom potmeter-midtbenet og GND; sjekk at VREF = 3V3. |
| Volum «baklengs» | Bytt de to ytterbena på potmeteret. |
| Libgpiod-feil ved oppstart | `sudo apt install libgpiod2` (Bookworm) eller `libgpiod3` (Trixie). |
| Kortet leses flere ganger | Øk `Player:RescanGapSeconds`. |
| USB-leser gir ingen treff i `UsbKeyboard` | Sjekk `cat /proc/bus/input/devices`, sett `DeviceName` til deler av navnet, og at brukeren er i gruppen `input`. |
| Ny lydløs enhet etter omstart | Vent noen sekunder – Player prøver å finne enheten i opptil 30 s etter boot. |

Nyttige kommandoer: `journalctl -u kortspiller-player -f`, `journalctl -u raspotify -f`, `sudo systemctl restart kortspiller-player`.

---

## 10. Oppdatere
Kjør `publish.ps1` og `install.sh` på nytt. Konfig (`appsettings.local.json`) og `data/` beholdes.
