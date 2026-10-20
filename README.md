# ShipbreakerVR

VR-Mod für **Hardspace: Shipbreaker** (Unity 2020.3, HDRP) auf Basis von BepInEx 5 und OpenXR.
Getestet mit Meta Quest über Virtual Desktop. Frühe Testversion.

## Stand
- Stereo-Rendering (MultiPass), Kopf-Tracking, Figur und Werkzeuge folgen dem Kopf
- HUD und Overlays auf einer Leinwand im Raum, Helm folgt dem Kopf
- VR-Einstellungen im Spielmenü (Reiter „Spiel“, Zeilen mit „VR:“)
- Offen: Menüs fest im Raum, Pausenmenü in VR, Controller, Zeiger

Details und Erkenntnisse zum Spielcode: [docs/NOTES.md](docs/NOTES.md).

## Benutzung
- F3: VR an/aus, F4: Blick zentrieren
- Einstellungen: `BepInEx\config\de.marczahn.shipbreakervr.cfg` oder im Spielmenü

Installation des fertigen Pakets: [docs/LIESMICH.txt](docs/LIESMICH.txt).

## Aufbau
- `src/ShipbreakerVR/` – das BepInEx-Plugin (C#, net48)
- `unity/ShipbreakerVRUnity/` – Hilfsprojekt (Unity 2020.3.35f1), baut das AssetBundle `xrmanager` mit den
  XR-/OpenXR-Einstellungen und liefert die OpenXR-DLLs (Menü *ShipbreakerVR → Build für das Spiel*).
  Die XR-Einstellungen stammen aus [ShipbreakerVR von Raicuparta](unity/ShipbreakerVRUnity/LICENSE-ShipbreakerVr.txt), MIT-Lizenz.

## Bauen
```
dotnet build src/ShipbreakerVR -c Release -p:GameDir="F:\SteamLibrary\steamapps\common\Hardspace Shipbreaker"
```
Erwartet BepInEx und die Unity-XR-DLLs im Spielordner (`BepInEx\core`, `BepInEx\plugins\ShipbreakerVR`).
Das Ergebnis `ShipbreakerVR.dll` kommt nach `BepInEx\plugins\ShipbreakerVR\`.
