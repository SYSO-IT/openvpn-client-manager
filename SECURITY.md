# Beveiligingsontwerp

Dit document beschrijft welke maatregelen in OpenVPN Client Manager zitten en waarom.

## Uitgangspunten

- **Geen beheerdersrechten.** De app draait `asInvoker` en vraagt nooit elevation, met één uitzondering: de knop die de gebruiker aan de groep *OpenVPN Administrators* toevoegt start een apart `net.exe`-proces via UAC. De app zelf blijft standaard gebruiker.
- **openvpn.exe draait als de gebruiker.** De Interactive Service voert alleen de netwerkconfiguratie uit met verhoogde rechten; het openvpn-proces (dat de config en sleutels verwerkt) heeft de rechten van de gebruiker. Dit is het ontwerp van OpenVPN 2.4+.
- **Minimale dependencies.** Geen NuGet-packages; alleen het .NET 8 framework (WPF + WinForms voor het systeemvak). Twee P/Invokes: DPAPI (`crypt32`) en DWM (donkere titelbalk).
- **Geen netwerkverkeer van de app zelf.** Geen telemetrie, geen updates, geen externe calls. De enige socket is de loopback-verbinding naar de management-interface van openvpn.

## Management-interface

- Bindt uitsluitend op `127.0.0.1` op een willekeurige vrije poort.
- Beveiligd met een per-verbinding gegenereerd wachtwoord (32 random bytes, hex) dat via **stdin** aan openvpn wordt doorgegeven, niet via de commandoregel.
- `--auth-nocache` zorgt dat openvpn wachtwoorden niet in het geheugen cachet tussen herverbindingen; de app levert ze opnieuw aan vanuit de DPAPI-store of vraagt ze opnieuw.

## Configuraties

Bij import wordt elke config genormaliseerd:

- **Verwijderd**: `management*`, `log`, `log-append`, `daemon`, `service`, `writepid`, `askpass <bestand>`, `auth-retry`, en het bestandsargument van `auth-user-pass` (wachtwoordbestanden in platte tekst worden nooit meegenomen; de gebruiker kan de gevonden gegevens desgewenst DPAPI-versleuteld opslaan).
- **Geblokkeerd (standaard verwijderd, met expliciete waarschuwing)**: `up`, `down`, `route-up`, `route-pre-down`, `ipchange`, `tls-verify`, `auth-user-pass-verify`, `client-connect`, `client-disconnect`, `learn-address`, `plugin`, `dns-updown`, `up-restart`, `script-security 2+`. Deze directives kunnen willekeurige code uitvoeren.
- **Defense in depth**: los van bovenstaande wordt openvpn gestart met `--script-security 1` op de commandoregel (commandoregel wint van de config), tenzij de gebruiker dit bewust uitzet in Instellingen.
- Groottelimieten: config ≤ 1 MB, ingesloten bestanden ≤ 1 MB, bundel ≤ 64 MB, max. 5000 entries. Bestandsnamen in bundels worden **nooit** uit de zip overgenomen; alle paden worden afgeleid van GUID's uit het manifest (geen path traversal / zip slip).
- Certificaten en sleutels worden **inline** opgeslagen zodat elke site één bestand is; er blijven geen losse `.key`-bestanden rondslingeren.

## Opslag van geheimen

- Inloggegevens: DPAPI `CryptProtectData` met `CRYPTPROTECT_UI_FORBIDDEN`, scope *CurrentUser*, plus per-site entropy (app-constante + site-GUID). Alleen ontsleutelbaar door hetzelfde Windows-account op dezelfde machine (of hetzelfde roaming-profiel bij domeinaccounts).
- Buffers met platte tekst worden na gebruik gewist (`CryptographicOperations.ZeroMemory`, unmanaged buffer wordt genuld voor `LocalFree`).
- De datamap `%LOCALAPPDATA%\OpenVpnClientManager` krijgt een expliciete ACL: overerving uit, alleen de gebruiker en SYSTEM.
- `config.json` bevat geen geheimen. De applicatielog bevat geen wachtwoorden, sleutels of management-wachtwoorden.

## Export-bundels

- Formaat: `OVCMBNDL` + versie + PBKDF2-iteraties + salt(16) + nonce(12) + GCM-tag(16) + ciphertext.
- Sleutelafleiding: PBKDF2-HMAC-SHA512, 600 000 iteraties, wachtwoordzin genormaliseerd (NFKC). Minimaal 12 tekens afgedwongen in de UI.
- Encryptie: AES-256-GCM; de header is *associated data*, dus manipulatie van salt/iteraties wordt gedetecteerd.
- Bij import worden de KDF-parameters gevalideerd (100k–10M iteraties) tegen DoS via een manipulatief manifest.
- Inloggegevens gaan **alleen** mee in een versleutelde bundel. Een onversleutelde `.zip` bevat wel sleutels/certificaten; de UI waarschuwt hier expliciet voor.

## Proceshygiëne

- Eén instantie (mutex `Local\OpenVpnClientManager.SingleInstance`).
- Bij afsluiten worden alle verbindingen netjes gestopt (`signal SIGTERM`); daarna sluit de pipe naar de Interactive Service, die openvpn.exe zo nodig hard beëindigt.
- Als de app draait met beheerdersrechten wordt dat gemeld als probleem: openvpn zou dan ook verhoogd draaien.
- Reproduceerbare build (`Deterministic`), `CheckForOverflowUnderflow`, geen `unsafe` code, analyzers op `latest-recommended`.

## Aanbevelingen voor uitrol

1. Onderteken de exe met een code-signing certificaat en rol uit via Intune/SCCM.
2. Voeg gebruikers via GPO toe aan *OpenVPN Administrators* in plaats van de UAC-knop te gebruiken.
3. Deel bundels alleen versleuteld en stuur de wachtwoordzin via een ander kanaal.
4. Overweeg per-gebruiker certificaten met versleutelde privésleutel; de app vraagt het sleutelwachtwoord en kan het DPAPI-versleuteld bewaren.

## Bekende afwegingen

- Wachtwoorden staan tijdens een verbinding kortstondig als `string` in het geheugen van de app (nodig om ze via de management-interface door te geven). Een memory dump van het gebruikersproces zou ze kunnen bevatten; dat is inherent aan elke OpenVPN-GUI.
- DPAPI beschermt tegen andere gebruikers en offline diefstal van de schijf, niet tegen malware die als dezelfde gebruiker draait.
