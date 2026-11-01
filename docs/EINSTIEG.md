# Einstieg: an der Mod mitarbeiten, ohne Programmierer zu sein

Diese Anleitung ist für dich, wenn du an ShipbreakerVR mitarbeiten möchtest, aber keine Erfahrung mit
Softwareentwicklung hast. Das Programmieren übernimmt Claude. Du sagst, was die Mod tun soll, testest im Headset
und entscheidest. Claude prüft den Code für dich, bevor eine Änderung in die Mod kommt.

Du musst nichts davon auswendig können. Wenn du unsicher bist, frag Claude: „Wo stehen wir gerade?“ oder „Was ist
der nächste Schritt?“ funktioniert immer.

## Worum es geht, in drei Sätzen

Hardspace: Shipbreaker ist ein normales Spiel für den Bildschirm. Die Mod ist eine Zusatzdatei
(`ShipbreakerVR.dll`), die beim Spielstart mitgeladen wird und das Spiel ins VR-Headset bringt. Das Laden übernimmt
BepInEx, ein Werkzeug, das in den Spielordner installiert wird und Mods in Unity-Spiele einklinkt.

## Wer macht was

| Wer | Aufgabe |
|---|---|
| **Du** | Ideen und Fehler beschreiben, im Headset testen, sagen was du siehst, entscheiden was du willst, Freigaben geben |
| **Claude** | nachfragen, planen, programmieren, bauen, Testversionen bei dir installieren, Logs lesen, Code prüfen, Pull Requests öffnen und mergen |
| **Marc** | Ansprechpartner bei Grundsatzfragen; schaut sich die Änderungen später in Ruhe an und räumt mit Claude auf, wo nötig |

Du kannst eine Änderung also ganz allein bis in die Mod bringen, auch wenn Marc gerade nicht da ist. Bei großen
Umbauten wird Claude dir aber vorschlagen, vorher mit Marc zu sprechen.

## Einmal einrichten

Das machst du einmal. Marc zeigt dir die Grundlagen, und Claude hilft dir bei allem anderen.

1. **GitHub-Konto.** Mit deinem GitHub-Konto bekommst du von Marc Zugriff auf das Repository
   `marczahn/shipbreaker-vr`; die Einladung kommt per Mail und muss angenommen werden.
2. **Das Spiel** auf deinem PC über Steam, dazu dein VR-Headset (die Mod wird mit einer Quest über Virtual Desktop
   getestet).
3. **Die Mod selbst**: die neueste Version von der
   [Release-Seite](https://github.com/marczahn/shipbreaker-vr/releases/latest) installieren, wie im
   [README](../README.md#installing) beschrieben. Spiel einmal starten und prüfen, dass VR läuft.
4. **Claude**: mit deinem eigenen Claude-Abo, verbunden mit GitHub und dem Repository. Claude braucht außerdem
   Zugriff auf deinen Spielordner, damit es die Mod bauen, Testversionen installieren und die Logs lesen kann. Beim ersten Mal
   sag einfach: „Richte bitte alles ein, damit wir an der Mod arbeiten können.“ Claude prüft dann, was fehlt
   (zum Beispiel Git und das .NET SDK), und erklärt dir jeden Schritt.

## Wörter, die dir begegnen

- **Repository (Repo)**: der Ordner mit allem, was zur Mod gehört, gespeichert auf GitHub. Jede Änderung wird dort
  mit Datum und Beschreibung festgehalten und kann wieder rückgängig gemacht werden.
- **main**: die offizielle Fassung im Repo. Daraus werden die Versionen für Spieler gebaut. Dort wird nie direkt
  gearbeitet.
- **Branch** („Zweig“): eine Arbeitskopie für genau eine Aufgabe. Was dort passiert, stört niemanden, auch nicht
  Marc, der vielleicht gerade an etwas anderem arbeitet.
- **Commit**: ein gespeicherter Zwischenstand auf einem Branch, mit einem Satz, was sich geändert hat.
- **Pull Request (PR)**: die Bitte, einen Branch in `main` zu übernehmen. Auf GitHub sieht man dort alle Änderungen
  und kann sie kommentieren.
- **Code-Review**: das Durchsehen der Änderungen auf Fehler, bevor sie nach `main` kommen. Das macht Claude für dich.
- **Merge**: das Übernehmen eines PRs in `main`. Claude macht das, wenn du es freigibst und alle Prüfungen bestanden
  sind.
- **Bauen (Build)**: aus dem Programmcode die Datei `ShipbreakerVR.dll` erzeugen, die das Spiel lädt.
- **Testversion (Testbuild)**: eine gebaute DLL, die noch nicht in `main` ist und nur bei dir zum Ausprobieren liegt.
- **Backup**: Claude legt vor jeder Testversion eine Kopie der bisherigen DLL an. Damit kommst du immer zurück.
- **Log**: eine Textdatei, in die Spiel und Mod schreiben, was gerade passiert. Claude liest sie selbst, du musst
  nichts heraussuchen.
- **Config-Datei**: `BepInEx\config\de.marczahn.shipbreakervr.cfg` im Spielordner. Dort stehen alle Einstellungen der
  Mod, dieselben wie im Spiel im Tab „VR“.
- **Release**: eine fertige Version als ZIP für alle Spieler, mit Versionsnummer (zum Beispiel 0.22.0).
- **Issue**: ein Eintrag auf GitHub für eine Idee oder einen Fehler, den man später angehen will.

## Wo die Testversion landet

Die Mod liegt im Spielordner. Normal ist das `BepInEx\plugins\ShipbreakerVR\ShipbreakerVR.dll`. Zum Entwickeln gibt
es einen schnelleren Weg: Mit einem Zusatz namens ScriptEngine liegt die DLL in `BepInEx\scripts`, und eine neue
Testversion wird geladen, während das Spiel läuft. Du musst das Spiel dann nicht neu starten; mit **F6** lädst du die
Mod von Hand neu. Claude sagt dir jedes Mal, ob ein Neustart nötig ist. Einrichten kann Claude das für dich.

## So läuft eine Aufgabe ab

1. **Du beschreibst, was du willst.** So genau, wie es geht (siehe unten). Lieber eine Sache auf einmal. Claude
   schaut zuerst auf GitHub nach, ob gerade jemand anderes (zum Beispiel Marc) an derselben Stelle arbeitet.
2. **Claude fragt nach**, bis klar ist, was gemeint ist, und sagt in einem Satz, was es verstanden hat. Wenn eine Idee
   Probleme macht, sagt Claude das auch (siehe „Wenn Claude widerspricht“).
3. **Claude legt einen Branch an und programmiert.** Das kann ein paar Minuten dauern.
4. **Claude baut und prüft.** Vor jedem PR laufen automatische Prüfungen (`tools/check.sh`). Wenn die rot sind, geht es
   nicht weiter, bis sie grün sind.
5. **Claude installiert die Testversion bei dir**, mit Backup, und gibt dir eine kurze Testliste: was du tun sollst und
   woran du erkennst, ob es klappt.
6. **Du testest im Headset** und erzählst, was du siehst. Auch „fühlt sich komisch an“ ist eine gute Rückmeldung;
   Claude fragt dann nach.
7. **Schritte 3 bis 6 wiederholen sich**, bis es passt.
8. **Claude öffnet einen Pull Request** und beschreibt darin in einfachen Worten, was sich ändert und was du getestet
   hast, damit Marc es später nachvollziehen kann.
9. **Claude prüft den Code** (Code-Review) und behebt, was dabei auffällt. Manchmal braucht es danach nochmal einen
   kurzen Test von dir.
10. **Merge.** Wenn die Prüfungen grün sind, du getestet hast und nichts anderes auf GitHub dazwischenfunkt, sagt
    Claude dir, dass der PR fertig ist. Du gibst frei, Claude mergt.
11. **Release.** Wenn mehrere Änderungen zusammen eine neue Version ergeben sollen, baut Claude ein Release. Den
    letzten Schritt auf GitHub (Release-Seite anlegen) machst du selbst; Claude erklärt dir, wo du klicken musst.

## Gute Wünsche formulieren

Hilfreich ist immer: **wo** im Spiel (Werft, Station zwischen den Schichten, Menü), **was** du tust (welche Hand,
welcher Knopf, welches Werkzeug), **was passiert** und **was stattdessen passieren soll**.

| Weniger hilfreich | Besser |
|---|---|
| „Der Cutter ist kaputt.“ | „Wenn ich den Cutter in der linken Hand halte und den Trigger drücke, schneidet er nach oben statt dahin, wo ich hinzeige.“ |
| „Das Fliegen soll besser sein.“ | „Beim Bremsen mit Y stoppe ich zu abrupt, mir wird dabei leicht schlecht. Kann das sanfter werden?“ |
| „Mach das Menü schöner.“ | „Im Pausemenü ist die Schrift zu klein, ich muss mich vorbeugen, um sie zu lesen.“ |

Wenn etwas nur manchmal passiert, sag das dazu, und wann ungefähr (zum Beispiel „nach dem Laden einer neuen Schicht“).

## Wenn Claude widerspricht

Claude ist so eingestellt, dass es nicht einfach alles umsetzt. Es sagt dir Bescheid, wenn

- eine Idee etwas kaputt machen würde, das schon funktioniert,
- es das schon gibt (oft als Einstellung im Tab „VR“),
- eine Aufgabe zu groß ist und besser in kleinen Schritten geht,
- etwas die Leistung drückt oder in VR übel werden lassen kann,
- gerade auf GitHub an derselben Stelle gearbeitet wird,
- Marc das entscheiden sollte, weil es die Mod grundsätzlich verändert.

Dann hörst du den Grund und einen Vorschlag. Du kannst trotzdem sagen, dass du es so willst; bei den Regeln im
nächsten Abschnitt bleibt Claude aber hart.

## Was Claude nie tut

- direkt in `main` arbeiten (immer über Branch und Pull Request),
- mergen, ohne dass die Prüfungen grün sind, der Code geprüft ist und du getestet hast,
- einen Branch überschreiben, an dem jemand anderes arbeitet,
- eine Testversion installieren, ohne vorher ein Backup anzulegen,
- Dateien außerhalb des Spielordners und des Repos ändern oder löschen,
- Dateien aus dem Spiel in die Mod übernehmen, auch keine veränderten (Bilder, Modelle, Sounds, Code und alles
  andere). Das wäre eine Urheberrechtsverletzung; die Mod enthält nur eigene oder frei lizenzierte Dateien.

## Wenn etwas schiefgeht

- **VR spinnt oder das Bild ist kaputt**: **F3** schaltet VR aus und wieder ein, **F4** richtet die Ansicht neu aus.
- **Die Testversion ist schlechter als vorher**: sag Claude „Bitte die Testversion zurücknehmen“. Es spielt das Backup
  zurück.
- **Das Spiel startet nicht mehr**: Claude schaut in die Logs (`BepInEx\LogOutput.log` im Spielordner). Notfalls
  installierst du die neueste Version von der Release-Seite neu.
- **Du weißt nicht, wo du stehst**: frag Claude „Wo stehen wir gerade, und was ist offen?“

## Mehr lesen

- [README](../README.md): die Mod aus Spielersicht, mit allen Tasten und Einstellungen.
- [CONTRIBUTING.md](../CONTRIBUTING.md) und [docs/NOTES.md](NOTES.md): technische Details. Die brauchst du nicht zu
  lesen; Claude kennt sie.
