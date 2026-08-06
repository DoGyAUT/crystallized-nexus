# Konzept: Koordination zwischen Trupps

Status: **Entwurf zur Freigabe** — noch keine Implementierung.
Stand: 2026-08-05. Eigener Arbeitsstrang, unabhängig vom Taskforce-Umbau
(`dynamic-squads-concept.md`): dieser hier sitzt in den States plus einem kleinen
Register im Manager, jener in Slot-Auflösung und Produktion.

---

## 1. Ist-Zustand

Die `Squads`-Liste des Managers wird an genau sieben Stellen abgefragt — **keine davon
ist Zielsuche**:

| Stelle | Wozu |
|---|---|
| `CNSquadManagerBotModule.cs:1172` | Artillerie/Support hängt sich an einen Trupp |
| `:755` | ein Protection-Trupp reagiert auf einen Basisangriff |
| `:1531`, `:1572`, `:1646` | Zählen für die Score-Berechnung bei der Aufstellung |
| `:2150`, `:2354` | Aufräumen und Poaching |

Zielsuche läuft überall über `FindClosestEnemy(leader, …)` — jeder Trupp scannt von seinem
eigenen Anführer aus. Es gibt kein Register darüber, wer bereits worauf schießt.

### Was es an Intelligenz gibt

* `SquadEngageFraction` — Anteil des Trupps, der das Ziel **überhaupt treffen** kann.
  Beantwortet nicht „sind wir gut dagegen": ein Raketensoldat gegen Infanterie liefert 1,0.
  <br>*Korrektur zum ersten Entwurf:* die binäre Variante (`CanSquadEngage`) wird sehr wohl
  überall angewandt — `FindClosestEnemyUnit`, `FindClosestEnemyBuilding` und
  `FindPriorityTarget` filtern damit. Nur der **Bruchteil** war auf `ScoreRushTarget` und
  die Flugzeugzustände beschränkt. **Erledigt** (Commit `3d9ce64`): fließt jetzt in
  `FindPriorityTarget` ein und gilt damit für alle Rollen.
* `CounterFraction` — **neu, erledigt** (Commit `3d9ce64`), siehe Abschnitt 3.
* `WaveTargetValueScore` (`:2044`) — Gebäudewert über Capabilities: Superwaffe 50,
  Produktion 30, Tech 25, Wirtschaft 20, Strom 10, Verteidigung −15. Nur für das Wellenziel.
* `PriorityTargetCapabilities` — Zielvorzug, aber nur für `Raider`, `AircraftRaider`,
  `Stealth`, `SubAssault`: vier von fünfzehn Rollen.

### Was strukturell fehlt

**Jeder Befehl im ganzen System ist `AttackMove`** (`CNGroundStates.cs:233, 358, 376, 499,
656`). Nirgends ein `Attack` auf einen bestimmten Aktor. Der Trupp wählt also einen
**Wegpunkt**, nie ein **Opfer**. Was im Gefecht beschossen wird, entscheidet das
`AutoTarget` jeder einzelnen Einheit — in der Regel das Nächstliegende.

Daraus folgen die drei Lücken:

1. **Zwischen Trupps** — fünf Trupps können denselben Harvester jagen, während die
   Waffenfabrik unbehelligt bleibt. Oder in fünf Richtungen zerfasern und einzeln fallen.
2. **Im Trupp** — kein Fokusfeuer. Fünf Panzer verteilen Schaden auf fünf Ziele, statt
   eines nach dem anderen zu töten.
3. **In der Welle** — siehe Abschnitt 5.

### Ein konkreter Fehler nebenbei

`ScoreRushTarget` erkennt Wirtschaftsziele über Namensfragmente:

```csharp
// CNGroundStates.cs:160
if (target.Info.Name.Contains("harv") || target.Info.Name.Contains("proc") ||
    target.Info.Name.Contains("ref"))
    score -= 220;
```

Obwohl `BotCapabilities` dafür `Harvester` und `Economy` kennt. Der Namensvergleich war in
beide Richtungen falsch: er verfehlte Nods `WEED` (trägt `Harvester`, heißt aber weder harv
noch proc noch ref) und das Tiberiumsilo, und traf dafür `GHARV.Husk`/`NHARV.Husk` (Wracks),
den reinen Tooltip-Platzhalter `PROC` und `gharv.colorpicker`.
**Erledigt** (Commit `f0b7c53`).

**Korrektur zum ersten Entwurf zu `AttachedTo`:** die States bewerten sehr wohl neu —
`ArtilleryIdleState` bevorzugt sogar einen unbeanspruchten Trupp und unter gleichen den
nächsten. Der echte Defekt lag woanders: **beide States verdrahteten ihre Kandidatenrollen
hart** (`Assault`/`Rush`, für Support zusätzlich `Protection`) und ignorierten damit das
`AttachToRole` des Templates — konfiguriert in `GDI_Juggernaut_Siege` und
`Nod_Artillery_Siege`, gelesen nur von der Erstzuweisung im Manager, die es anschließend
überschrieben bekam. Tote Konfiguration. Dazu nahm die Erstzuweisung den erstbesten statt
den nächsten Trupp, und die Artillerie behält ihren Gastgeber, solange er gültig ist.
**Erledigt** (Commit `9cfefb7`).

---

## 2. Eine Datenstruktur für drei Probleme

Fokusfeuer, Overkill-Schutz und Zielverteilung sind **dieselbe Mechanik aus drei
Blickwinkeln**. Alle drei brauchen genau eine Zahl: *wie viel Schaden ist diesem Ziel
bereits zugesagt?*

```
zielregister: Actor → { ZugesagterSchaden, Beanspruchende Trupps, Eignung }
```

Zentral im Manager, neu aufgebaut auf der bestehenden Kadenz (`AssignRolesInterval`;
Trupps im Gefecht werden ohnehin fünfmal so oft aktualisiert, Commit `881244f`).

Daraus fallen alle drei Verhalten heraus:

| Verhalten | Regel |
|---|---|
| **Fokusfeuer** | Einheiten einem Ziel zuweisen, **bis** `ZugesagterSchaden ≥ Restleben` |
| **Overkill-Schutz** | Ist die Schwelle erreicht, geht die nächste Einheit ans nächste Ziel |
| **Zielverteilung** | Bei gleichem Wert gewinnt der Trupp mit der höheren Eignung |

Das Beispiel mit den Fliegern löst sich damit von selbst: zehn Orcas, jede ~200 wirksamer
Schaden pro Anflug, Gebäude mit 1000 Leben → sechs werden zugewiesen (inkl. Sicherheits-
aufschlag), die übrigen vier bekommen das nächste Ziel.

### Sicherheitsaufschlag

Ein bewegliches Ziel weicht aus oder stirbt, bevor alle Schüsse ankommen; ein Gebäude
nicht. Vorschlag: Faktor **1,15 für Gebäude**, **1,4 für bewegliche Ziele**.

Wichtiger noch: die Zusage muss **nachbewertet** werden. Sterben zwei der zugewiesenen
Einheiten, überlebt das Ziel mit Restleben und niemand fühlt sich zuständig. Deshalb
Neuaufbau des Registers auf der Kadenz, nicht einmalig.

### Schadensschätzung

Es gibt im Fork **keinen** fertigen Helfer. Die Zutaten liegen aber bereit:

* `DamageWarhead.Damage` und `DamageWarhead.Versus` (`engine/OpenRA.Mods.Common/Warheads/
  DamageWarhead.cs:24, 30`) — Rohschaden und Prozentwert je Panzerungstyp.
* `Health.HP` für das Restleben.

`DamageVersus()` selbst ist `protected` und braucht ein lebendes Opfer; die Bot-Seite
liest stattdessen statisch `Versus[Panzerungstyp]`. Ergebnis pro
(Waffe, Panzerungstyp) einmal berechnen und cachen — danach ist die Abfrage ein
Dictionary-Zugriff.

---

## 3. Counter-Targeting: die Versus-Liste rückwärts gelesen

Für „sind wir *gut* dagegen" braucht es keine neuen Daten. Die Matrix steht schon in jedem
Profil:

```
			NeedRules:
				AntiInfantry:  EnemyCapabilities: Infantry
				AntiArmor:     EnemyCapabilities: Vehicle, Tank
				AntiAir:       EnemyCapabilities: Aircraft
```

Vorwärts: *„Gegner hat Fahrzeuge → ich brauche AntiArmor."*
Rückwärts: *„Meine AntiArmor-Einheiten sind gut gegen alles mit `Vehicle`."*

Daraus `CounterFraction(squad, target)` als Gegenstück zu `SquadEngageFraction`: Anteil der
Trupp-Einheiten, deren Capabilities das Ziel laut Matrix kontern. Das ist die „Eignung" im
Zielregister.

**Perfektion ist hier ausdrücklich nicht das Ziel.** Fokusfeuer allein bringt den größten
Teil des Gewinns; Counter-Zuweisung ist die Kür.

---

## 4. Leinen

Ohne Bremsen macht Koordination Bots **schlechter**: Trupps laufen aus laufenden Gefechten
weg, um ihr ideales Ziel zu suchen, und werden einzeln aufgerieben.

* **Hysterese** — ein bereits engagierter Trupp wechselt nur, wenn das neue Ziel deutlich
  besser ist (Schwelle als Modul-Feld).
* **Reichweitengrenze** — nie aus einem aktiven Gefecht heraus umschwenken.
* **Verfolgungsleine** — ein `Attack`-Befehl lässt Einheiten verfolgen, `AttackMove` nicht.
  Ohne Grenze rennt der Trupp einem fliehenden Buggy hinterher und zerfasert. Jenseits von
  N Zellen zurück auf `AttackMove`.

**Engine-Grenze, die bleibt:** der Befehl steuert, wohin der Trupp geht und worauf er
fokussiert. Ob ein einzelner Raketensoldat zurückschießt, wenn Infanterie ihn angreift,
entscheidet weiterhin sein eigenes `AutoTarget`. Koordination verteilt die Armee klug, sie
verhindert keine schlechten Einzelduelle.

---

## 5. Wellen-Zusammenhalt

Die Welle ist heute eine **Startbedingung, kein Zusammenhalt**:

```csharp
// CNSquadManagerBotModule.cs:1997
WaveParticipants.RemoveWhere(s =>
    !s.FuzzyStateMachine.IsInAnyState<CNWaveHoldState, CNWaveMoveToRallyState>());
```

Sobald ein Trupp den Sammelpunkt verlässt und in seinen Angriffszustand wechselt, fliegt er
aus der Welle. Dort scannt er wieder selbst (`CNGroundStates.cs:365`): kommt irgendein
Gegner in `AttackScanRadius` — beim Rush-Profil 12 Zellen —, schwenkt er darauf um. Nach
dem ersten Feindkontakt zerfällt die Welle in Einzelkämpfer.

Das gemeinsame Ziel wird beim Start korrekt durchgereicht
(`CNWaveStates.cs:184`) — es überlebt nur die Freigabe nicht.

### Was „zusammen agieren" konkret heißt

1. **Teilnahme überlebt die Freigabe.** `WaveParticipants` wird nicht mehr beim
   Zustandswechsel geleert, sondern erst, wenn das Wellenziel gefallen oder die Welle
   geschlagen ist.
2. **Ziel ist eine Achse, kein einzelner Aktor.** Stirbt das Zielgebäude, löst sich die
   Welle nicht auf — sie rückt zum nächsten Ziel in derselben Richtung vor. Lokales
   Umschwenken nur innerhalb eines Korridors um diese Achse.
3. **Kohäsion unterwegs.** `CNWaveHoldState` hat bereits Drift-Korrektur (`HasDrifted`,
   `IssueHoldMove`) gegen eine feste Halteposition. Dieselbe Logik gegen den **Schwerpunkt
   der Welle**: wer zu weit vorprescht, hält; wer zurückfällt, wird eingeholt.
4. **Gemeinsamer Rückzug.** Verliert die Welle, ziehen sich alle Teilnehmer zusammen
   zurück, statt einzeln zerrieben zu werden.
5. **Artillerie und Support hängen an der Welle**, nicht an einem willkürlich gewählten
   Einzeltrupp (siehe `AttachedTo`-Fehler oben).

---

## 6. Umsetzung in Stufen

| Stufe | Inhalt | Umfang |
|---|---|---|
| ~~1~~ | ~~`AttachToRole` wird respektiert, Erstzuweisung nimmt den nächsten Trupp. `ScoreRushTarget` auf Capabilities.~~ **erledigt** (`9cfefb7`, `f0b7c53`) | klein |
| ~~2~~ | ~~`SquadEngageFraction` in die Zielbewertung aller Rollen. `CounterFraction` ergänzt.~~ **erledigt** (`3d9ce64`) | klein |
| ~~3a~~ | ~~Schadensschätzung + Zielregister mit zugesagtem Schaden → Overkill-Schutz.~~ **erledigt** (`07b8e4e`) | mittel |
| ~~3b~~ | ~~Fokusfeuer: explizite `Attack`-Befehle mit Verfolgungsleine.~~ **erledigt** (`4ab4f3a`) | mittel |
| ~~4~~ | ~~Wellen-Zusammenhalt: Teilnahme überlebt die Freigabe, Ziel rückt nach, Kohäsion gegen den Wellenschwerpunkt.~~ **erledigt** (`4dbf983`) | mittel |
| ~~5~~ | ~~Counter-Zuweisung: ein Trupp lässt ein Ziel liegen, wenn ein deutlich besser geeigneter es bereits beansprucht hat.~~ **erledigt** (`811a7a4`) | mittel |

Der Weg dahin ist bewusst dezentral: es gibt **keinen** zentralen Zuteilungsdurchlauf, der
Trupps Ziele vorschreibt. Die Zuteilung entsteht daraus, dass jeder Trupp sieht, wer schon
was beansprucht hat und wie gut. Das fügt sich in die bestehende Architektur ein, in der
jeder Trupp in seinem eigenen Tick entscheidet.

Beim Twin-Check zu Stufe 5 fiel auf, dass die **Flugzeuge einen eigenen Zielbewerter**
haben (`ScoreAircraftTarget`), den die Stufen 3a und 5 zunächst nicht erreicht hatten —
ausgerechnet der Fall, der diese Arbeit ausgelöst hat. Mit `811a7a4` behoben.

Was von Stufe 4 **nicht** umgesetzt ist: der gemeinsame Rückzug einer geschlagenen Welle,
und dass Artillerie/Support sich an die Welle hängen statt an einen Einzeltrupp.

### Was jetzt auf dem Feld anders ist

Fokusfeuer ist der sichtbarste Unterschied zwischen einem Bot und einem Spieler, der von
Hand führt. Ein Trupp tötet jetzt einen Gegner nach dem anderen, statt Schaden über alles
in Reichweite zu streuen — und zehn Flieger hören auf, ein Gebäude zu bombardieren, das
drei von ihnen bereits erledigt haben.

**Ungetestet.** Nichts davon ist gespielt worden, und es liegt auf 33 ebenfalls ungespielten
Commits obendrauf.

---

## 7. Profil-Regler

Fokusfeuer macht Bots deutlich stärker — das braucht eine Bremse für leichtere Profile.
Passt in dasselbe Bild wie beim Taskforce-Umbau: das Profil dreht an Zahlen.

Umgesetzt, mit diesen Vorgabewerten im Code — noch in **keiner** Profildatei überschrieben,
das gehört nach der ersten Partie eingestellt:

```
			TargetClaimingEnabled: True  # Hauptschalter für das Zielregister
			OverkillFactorBuilding: 115  # Prozent des Restlebens, ab dem ein Ziel "gedeckt" ist
			OverkillFactorMobile: 140    # höher: bewegliche Ziele weichen aus und werden repariert
			CounterDeferenceMargin: 34   # Eignungsvorsprung, ab dem einem anderen Trupp der Vortritt gelassen wird
			FocusFireStrictness: 100     # Prozent der freien Einheiten, die direkt angreifen
			PursuitLeashCells: 8         # darüber hinaus wird nicht verfolgt
			WaveCohesionCells: 12        # Vorsprung, ab dem ein Trupp auf die Welle wartet
```

Für leichtere Profile ist vor allem `FocusFireStrictness` der Regler: deutlich unter 100
gesetzt mikromanagt der Bot spürbar schlechter, ohne dass sonst etwas geändert werden muss.

Noch nicht umgesetzt: `TargetSwitchHysteresis` (Mindestvorsprung für einen Zielwechsel).
Bisher bremst nur die Verfolgungsleine; ein Trupp kann sein Ziel noch jederzeit wechseln,
wenn die Bewertung kippt.

---

## 8. Offene Punkte

* **Aufwand der Schadensschätzung** ist ungeprüft. Mehrere Sprengköpfe je Waffe, mehrere
  Bewaffnungen je Einheit, Nachladezeiten — die erste Fassung sollte grob schätzen
  (stärkster Sprengkopf, Panzerung des Ziels) und erst verfeinert werden, wenn sie sich
  im Spiel als zu ungenau erweist.
* **Wechselwirkung mit dem Taskforce-Umbau:** gemischte Trupps (dynamische Slots) haben
  kein einheitliches Counter-Profil mehr. `CounterFraction` als *Anteil* statt als
  Ja/Nein ist genau deshalb so formuliert.
* **Messbarkeit.** Ohne eine Anzeige, welcher Trupp welches Ziel beansprucht und mit
  wie viel zugesagtem Schaden, ist das nicht zu tunen. Dieselbe Debug-Ausgabe wie beim
  Taskforce-Umbau, um ein Zielregister erweitert.
