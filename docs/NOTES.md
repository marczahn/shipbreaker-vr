# Shipbreaker VR: Arbeitsstand

## Spiel (Stand 19.10.2026)
- Installiert: `F:\SteamLibrary\steamapps\common\Hardspace Shipbreaker`
- Spielversion 1.3.0.1263554, Unity 2020.3.35f1, Mono, HDRP, Build `JocularReactor` (27.01.2026)
- PC: RTX 4070 Ti (12 GB), Monitor 3840x2160
- Spiellogik: `BBI.Unity.Game.dll` (Assembly-CSharp ist fast leer)
- Eingabe: InControl (`GameplayActions`, `LynxControls`)
- Kamera: `LynxCameraController` (statisch `MainCamera`, `ScreenCenter`), gesteuert über Cinemachine (`MainBrain`, `VirtualCamera`)
- Headset: Quest über Virtual Desktop (OpenXR über VDXR, Ausweichweg SteamVR)

## Zielen der Werkzeuge
Alle Werkzeuge zielen mit normalen `Physics.Raycast` über `LynxCameraController.MainCamera.ScreenPointToRay(...)`:
- `CuttingController.cs` (595, 602, 621), `CuttingFXController.cs`
- `TetherController.cs` (991)
- `Scanner.cs` (237)
Idee für Phase 4: eine eigene Ziel-Kamera an der Hand, die diese Aufrufe bekommt.

## Plan
1. Bestandsaufnahme: erledigt (ShipbreakerVR-Test übersprungen, wir bauen selbst)
2. Fundament: erledigt. BepInEx 5.4.23.3 und Plugin laden, Spielkamera `Main Camera` (FOV 72) gefunden. Beim Beenden wirft das Spiel selbst eine NullReferenceException in `Player.OnDestroy` (nicht von uns).
3. VR sehen: läuft (Plugin 0.2.1, 20.10.2026). F3 = VR an/aus, F4 = Kopf zentrieren.
   - OpenXR 1.2.8 aus dem Unity-Hilfsprojekt, Multi-Pass (Single Pass Instanced zeigte nur links ein verzerrtes Bild: dem Spiel fehlen die Shader-Varianten)
   - Offen: Maus dreht die Sicht weiter mit; Entfernungen wirken ab einigen Metern gestaucht (Weltmaßstab prüfen, Config-Wert zum Ausprobieren)
4. VR spielen: Controller, Werkzeuge an die Hand, Tasten
5. VR bedienen: HUD als Visier, Menüs mit Pointer
6. VR aushalten: Komfort, Performance

## Bauen
`src/ShipbreakerVR`: `dotnet build -c Release` (Pfade über `GameDir` bzw. `ManagedDir`/`BepInExDir`).
Dekompilieren: `ilspycmd -p -o out -r <Managed> <Managed>/BBI.Unity.Game.dll`

## 0.3.0: Figur folgt dem Kopf (zu testen)
- VR-Rig ist jetzt eigenständig (kein Kind der Spielkamera), folgt nur deren Position; Rig-Drehung = "Spielraum" P.
- Transpiler auf OrientationController.HandleAxisRotation leitet Rigidbody.MoveRotation auf VrBody.MoveRotation um:
  Mausdrehung m dreht P, Figur = P * Kopf (* inverse Kamera-Offset). Fremde Drehung (Tod, Respawn) => Abgleich.
- HandleReorient in VR übersprungen; "Aufrichten" richtet P auf.
- Config [Steuerung] KoerperFolgtKopf (Standard an). Aus = altes Verhalten (Kopf frei).
- Offen: Kamera sitzt evtl. nicht im Drehpunkt der Figur -> Kopfnicken verschiebt den Blickpunkt leicht.

## 0.4.0: UI im Raum, Helm am Kopf (zu testen)
- Spiel-UI: Kamera mit Tag "UI_Camera", gerendert in Custom Passes HUDCustomPass / DrawUICameraCustomPass (bildschirmfüllend).
  VrUi fängt beide Pässe für die VR-Kamera ab: UI-Kamera -> eigene RenderTexture (Bildschirmgröße), pro Auge auf ein Quad
  gezeichnet (UI/Default, ZTest Always, Matrizen per Reflection aus HDCamera.xr GetViewMatrix/GetProjMatrix).
  Gameplay: Visier vor dem Kopf, Blickwinkel = FOV der Spielkamera. Sonst: feste Leinwand im Rig, F4 setzt sie neu.
- Helm: HelmetController.m_HelmetContainer folgt dem VR-Kopf (Lage relativ zur Spielkamera beibehalten). Config Helm=Kopf/Aus/Spiel.
- Glitch-Effekte des HUD-Compositings fallen in VR weg. Mauszeiger in Menüs fehlt noch (Phase 5).

## 0.4.1 / 0.4.2
- 0.4.0-UI-Patches scheiterten: Execute mehrdeutig (CustomPass hat alte Überladung) -> Parametertyp CustomPassContext angeben.
- Cutter zielte nach oben: LynxCameraController.UpdateScreenDimensions nimmt mit XR die Augentextur (2688x2880) als
  ScreenWidth/Center, Werkzeuge zielen aber über die flache Kamera (2560x1440). ScreenSizePatch nimmt MainCamera.pixelWidth/Height.
- TAA in VR (MultiPass): verwaschen, Objekte zittern -> Standard jetzt SMAA ([Bild] Kantenglaettung). Spiel selbst: None.
- 0.4.2: Overlay-Canvases (ScreenSpaceOverlay) landen in VR im linken Auge -> VrCanvases hängt sie an die UI-Kamera.
  UiFlaecheZeigen (blau getönte Leinwand) als Diagnose, Standard an.
