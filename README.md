# OpenVPN Client Manager

Een Windows-desktoptool (C# / .NET 8 / WPF) om OpenVPN-clientconfiguraties per **klant → site** te beheren en er als **standaard gebruiker** (zonder beheerdersrechten) mee te verbinden. Fuzzy zoeken, donker thema in SYSO-huisstijl, en volledige import/export van de complete configuratie inclusief de .ovpn-bestanden.

## Hoe het werkt

De tool gebruikt exact hetzelfde mechanisme als OpenVPN GUI:

1. De app praat met de **OpenVPN Interactive Service** via de named pipe `\\.\pipe\openvpn\service`. Die service start `openvpn.exe` **met de rechten van de gebruiker**; alleen de netwerkconfiguratie (routes, DNS, adapter) doet de service zelf.
2. `openvpn.exe` wordt gestart met een **management-interface op 127.0.0.1** op een willekeurige vrije poort, beveiligd met een 64-teken random wachtwoord dat via stdin wordt doorgegeven (dus niet zichtbaar in de proceslijst).
3. Via die management-interface volgt de app de status (`>STATE`, `>LOG`, `>BYTECOUNT`), beantwoordt ze wachtwoordvragen (`>PASSWORD`) en stopt ze de verbinding (`signal SIGTERM`).
4. Sluit de app (of crasht hij), dan sluit de pipe en **beëindigt de service openvpn.exe automatisch**. Er blijven nooit weesverbindingen hangen.

## Vereisten

- Windows 10/11, .NET 8 Desktop Runtime (of gebruik de self-contained publish).
- OpenVPN community edition 2.5+ (getest met 2.6.17) met de service **OpenVPNServiceInteractive** actief (standaard bij installatie).
- De gebruiker moet lid zijn van de lokale groep **OpenVPN Administrators**. Dat is een eis van de Interactive Service voor configs buiten `C:\Program Files\OpenVPN\config`. De app detecteert dit en biedt een knop die via UAC `net localgroup` uitvoert (de enige actie die ooit beheerdersrechten vraagt). Uitrollen via GPO/Intune is de nette manier:

  ```
  net localgroup "OpenVPN Administrators" "DOMEIN\gebruiker" /add
  ```

## Functies

- **Klanten en sites**: sites (VPN-configs) hangen altijd onder een klant. Sorteren, bewerken, verplaatsen, verwijderen.
- **Importeren van .ovpn**: losse `ca`/`cert`/`key`/`tls-auth`/`tls-crypt`/`pkcs12`-bestanden naast de config worden automatisch ingesloten zodat elke site één zelfstandig bestand is. Sleep een .ovpn naar het venster of gebruik **Site** (Ctrl+N).
- **Overnemen uit OpenVPN GUI**: scant `%USERPROFILE%\OpenVPN\config` (en de globale configmap) en importeert alles in één keer. Klant en site worden voorgesteld uit de bestandsnaam (pfSense-export `KLANT[-LOCATIE]-RT01-UDP4-poort-gebruiker-config`) en zijn per regel aan te passen. Originelen blijven ongewijzigd; al geïmporteerde configs worden herkend.
- **Fuzzy zoeken**: begin gewoon te typen. Enter verbindt (of verbreekt) het bovenste resultaat, pijltjes navigeren, Esc wist.
- **Meerdere verbindingen tegelijk**, met live status, VPN-IP, verkeer en duur. Log per verbinding.
- **Inloggegevens**: worden gevraagd via een dialoog; optioneel onthouden met Windows DPAPI (alleen leesbaar door dit gebruikersaccount op deze machine). 2FA/static challenge wordt ondersteund; OTP-codes worden nooit opgeslagen.
- **Export/import**: één bundel (`.ovpnbundle`) met alle klanten, sites en configs. Standaard versleuteld met een wachtwoordzin (AES-256-GCM, PBKDF2-HMAC-SHA512 600k iteraties). Opgeslagen inloggegevens kunnen alleen in een versleutelde bundel mee. Een onversleutelde `.zip` kan ook, met waarschuwing.
- **Donker/licht thema** (SYSO-kleuren: nagenoeg zwart, wit, gedempt grijs en oranje `#F26A1B`), systeemvak-icoon met meldingen, optioneel starten bij aanmelden.

## Bouwen

```
dotnet build OpenVpnClientManager.sln -c Release
```

Uitrollen als één bestand (vereist .NET 8 Desktop Runtime op de doelmachine):

```
dotnet publish src/OpenVpnClientManager -c Release -o publish
```

Self-contained (geen runtime nodig, ~70 MB):

```
dotnet publish src/OpenVpnClientManager -c Release -o publish --self-contained true
```

Onderteken de exe voor productie met een code-signing certificaat (`signtool sign /fd SHA256 /tr http://timestamp... /td SHA256 ...`).

## Opslag

Alles staat in `%LOCALAPPDATA%\OpenVpnClientManager\`:

| Pad | Inhoud |
| --- | --- |
| `config.json` | klanten, sites, instellingen (geen geheimen) |
| `configs\<site-id>.ovpn` | de genormaliseerde OpenVPN-configs |
| `credentials\<site-id>.bin` | DPAPI-versleutelde inloggegevens (alleen als "onthouden" is gekozen) |
| `logs\<site-id>.log` | openvpn-log van de laatste verbinding |
| `logs\app.log` | applicatielog (geen geheimen) |

De map krijgt bij aanmaak een ACL met uitsluitend de gebruiker zelf en SYSTEM.

## Beperkingen

- Alleen Windows en alleen OpenVPN 2.x (community). OpenVPN Connect / ovpn3 wordt niet ondersteund.
- Dynamic challenge/response (`CRV1`) wordt niet ondersteund; static challenge (`SC:`) wel.
- Configs met `up`/`down`/`plugin`-directives worden standaard ontdaan van die regels; zie SECURITY.md.
