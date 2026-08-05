# Konzept: Capability-basierte Squad-Zusammenstellung

Status: **Entwurf zur Freigabe** — noch keine Implementierung.
Stand: 2026-08-05. Basis: Branch `ai/bot-activity`.

---

## 1. Zielbild

Squads entstehen heute aus 208 handgepflegten Templates (2981 Zeilen YAML über 5 Profile),
in denen jeder Slot eine feste `AllowedTypes`-Liste trägt. Dieselben ~40 Einheiten sind
damit fünfmal beschrieben.

Zielbild: ein Slot beschreibt **wofür** er da ist, nicht **womit**. Die Einheitenauswahl
entsteht zur Laufzeit aus den `BotCapabilities` der eigenen Einheiten, gewichtet nach
Kampfwert und tatsächlicher Bewährung. Der Bot nimmt früh, was da ist; sobald bessere
Einheiten verfügbar sind, verdrängen sie die alten von selbst, weil der Wert-Term sie
höher rankt und der Tech-Baum die Verfügbarkeit ohnehin gatet.

Das alte System läuft parallel weiter: ein Slot ohne Capability-Felder verhält sich
bitweise wie heute.

### Was das Konzept ausdrücklich nicht ändert

Die gesamte Bedarfs- und Bewertungsebene bleibt unangetastet, weil `CNTeamTemplateInfo`
nur erweitert und nicht ersetzt wird:

`Role`, `Tags`, `Bias`, `RepeatPenalty`, `MaxInstances`, `MinSlotsToActivate`,
`AttachToRole`, `PriorityTargetCapabilities`, `Factions`, `Poachable`, `StayInBase`,
`CaptureAndSell`, `ScaleWithBuilding`/`SquadsPerBuilding`, `IgnoresCashReserve` —
sowie modulseitig `NeedRules`, `TagWeights`, `RoleWeights`, `UpdateThreatTags()`,
`TaggedEffectiveScore()`, `tagPerformance` und das komplette Wellen-System.

Greift der Gegner mit vielen Fahrzeugen an, steigt weiterhin über `NeedRules.AntiArmor`
der Score aller Templates mit dem Tag `AntiArmor`; neu ist nur, dass ein solches Template
seine Einheiten nicht mehr aus einer Namensliste zieht.

---

## 2. Befunde aus dem Ist-Zustand

Drei Dinge aus der Code-Recherche prägen das Konzept stärker als die ursprüngliche Skizze.

### 2.1 Das Capability-Vokabular ist zu grob, um innerhalb einer Klasse zu ranken

Ausgezählt aus `rules/{gdi,nod,shared}-{vehicles,infantry,aircraft}.yaml`:

| Capabilities | Einheiten |
|---|---|
| `Vehicle, AntiArmor` | GTMTNK, MMCH, LTNK, TTNK (+ CYCLOPS mit `Hover`) |
| `Vehicle, AntiArmor, AntiAir` | WARDEN, 4TNK, STNK (+ `Cloaked`) |
| `Infantry, AntiInfantry` | GASOL, NASOL |
| `Infantry, AntiInfantry, AntiArmor` | ZTROOPER, NACAD, ACOLYTE |

Ein reiner Überdeckungs-Score erzeugt also für halbe Tech-Bäume Gleichstand. Der
Tie-Break muss von außerhalb der Tags kommen.

### 2.2 Ein negativer Kostenterm kippt das Ziel

Weil die Tags kaum differenzieren, wäre `- Kosten * Gewicht` das dominante Kriterium und
nicht die Feinkorrektur. Ergebnis wäre dauerhafter Billig-Spam; „bessere Einheiten
verdrängen die alten" träte nie ein.

**Entschieden:** Kosten zählen als **Wert** (teurer = stärker), begrenzt durch ein
**Budget pro Slot/Squad**. Der Tech-Baum gatet die Verfügbarkeit, das Budget die Stückzahl.

### 2.3 `AllowedTypes` hat sechs Konsumenten, nicht nur die Produktion

| Ort | Zweck |
|---|---|
| `CNSquadManagerBotModule.cs:899` → `AddPreferredDemand` (`:988`) | Produktionsbedarf pro Typ |
| `CNSquadManagerBotModule.cs:942` `GetTemplateUnitCap` | Hardcap pro Typ für die Produktion |
| `CNSquadManagerBotModule.cs:975` `GetTypesIgnoringCashReserve` | Cash-Reserve-Ausnahme |
| `CNSquadManagerBotModule.cs:1267` `TemplateAcceptsUnit` | Salvage-Pass für idle Einheiten |
| `CNSquadManagerBotModule.cs:1393` `TakeAvailableUnits` | tatsächliches Slot-Füllen |
| `CNUnitBuilderBotModule.cs:901` `SelectBuildableType` | Reservierungs-Pfad der Produktion |

Alle sechs brauchen dieselbe Abstraktion. `GetTemplateUnitCap` ist die gefährlichste
Stelle: sie summiert `slot.Count * MaxInstances` über *jedes* Template, in dem ein Typ
vorkommt — mit breiten dynamischen Kandidatenlisten wird dieser Cap bedeutungslos hoch.

---

## 3. YAML-Format

Ein dynamischer Slot ist ein `CNSlotInfo` mit Capability-Feldern statt `AllowedTypes`.
Kein zweiter Slot-Typ, kein zweites Assignment — dieselbe Klasse, ein Feld mehr.

```
GDI_Assault_AntiArmor_Vehicle:
	Role: Assault
	Tags: AntiArmor, Frontline, Vehicle
	MaxInstances: 5
	ScaleWithBuilding: gaweap
	SquadsPerBuilding: 1
	MinSlotsToActivate: 1
	PriorityTargetCapabilities: Harvester, Economy, Power, Vehicle
	MaxSquadCost: 6000
	Slots:
		1:
			Wants: Vehicle, AntiArmor
			MinCount: 3
			MaxCount: 5
			MaxSlotCost: 4500
```

Gelesen: *„Panzerabwehr-Fahrzeuge, drei bis fünf davon, für höchstens 4500 Credits."*
Kein Ausschluss nötig — Artillerie, Stealth Tanks, Subtanks und Einzelstücke hält
`ReservedCapabilities` automatisch draußen (3.1).

### Neue Felder an `CNSlotInfo`

| Feld | Default | Bedeutung |
|---|---|---|
| `Wants` | `[]` | die Capabilities, die eine Einheit **alle** tragen muss, um in diesen Slot zu dürfen |
| `Prefers` | `[]` | Bonus-Capabilities: jede getroffene erhöht den Score, keine ist Pflicht |
| `Excludes` | `[]` | Notnagel für Sonderfälle. Im Normalfall leer — dafür gibt es `ReservedCapabilities` (siehe 3.1) |
| `MinCount` | `= Count` | Mindestbesetzung für „Slot erfüllt" |
| `MaxCount` | `= Count` | Zielbesetzung für Nachschub/Produktion |
| `MaxSlotCost` | `0` | Kostenbudget des Slots, 0 = unbegrenzt |

`Count: 4` bleibt der Normalfall und heißt schlicht `MinCount = MaxCount = 4`.
`MinCount`/`MaxCount` schreibt man nur, wenn man eine Spanne will.

### 3.1 `ReservedCapabilities`: Rollenabgrenzung einmal statt 200-mal

Ohne weitere Maßnahme müsste **jeder** Kampf-Slot dieselbe Ausschlussliste tragen —
`Artillery, Cloaked, Subterranean, Harvester, Expansion, Transporter, Medic` — nur damit
die Artillerie nicht in der Panzerlinie landet statt in ihrem Hang-Back-Squad. Das ist
Boilerplate, und es ist ableitbar: diese Capabilities *gehören* bestimmten Rollen.

Also einmal am Modul deklariert statt an jedem Slot wiederholt:

```
	ReservedCapabilities:
		Artillery: ArtilleryAssault
		Cloaked: Stealth
		Subterranean: SubterraneanAssault
		Harvester:
		Expansion:
		Transporter:
		Medic:
		Unique:
```

Regel: eine reservierte Capability wird von dynamischen Slots **nur** in Templates der
zugeordneten Rolle akzeptiert; ohne Rolle (leerer Wert) nie. Ein `Assault`-Slot mit
`Wants: Vehicle, AntiArmor` lässt damit Juggernaut, Stealth Tank und Subtank
automatisch draußen, ohne ein Wort dazu zu schreiben.

### 3.2 `Unique`: Einzelstücke schützen sich selbst

`HMEC` (Mammut Mk II) trägt `Vehicle, AntiArmor, AntiAir, AntiInfantry` — er unterscheidet
sich von `4TNK` durch **keine** Capability, nur durch den Preis (3000 vs. 1400). Über den
Wert-Term würde er in jede Panzerlinie gezogen und verlöre die Sonderbehandlung
(`Bias: 20`, `IgnoresCashReserve`, `Count: 1`), für die sein Template existiert.

Statt ihn in jedem Panzer-Template über alle fünf Profile per Name auszuschließen, bekommt
die Einheit **ein Wort**:

```
HMEC:
	BotCapabilities:
		Capabilities: Vehicle, AntiArmor, AntiAir, AntiInfantry, Unique
```

`Unique` ist reserviert und keiner Rolle zugeordnet — dynamische Slots nehmen die Einheit
nie, ein statisches Template darf sie weiter beim Namen nennen. Nebeneffekt ohne
Zusatzaufwand: `PriorityTargetCapabilities: Unique` funktioniert damit sofort als
„schieß zuerst auf das Superding".

### Neues Feld an `CNTeamTemplateInfo`

| Feld | Default | Bedeutung |
|---|---|---|
| `MaxSquadCost` | `0` | Gesamtbudget über alle Slots, 0 = unbegrenzt |

Ein Slot gilt als **dynamisch**, sobald `RequireCapabilities` oder `PreferCapabilities`
gesetzt ist. Dann ist `AllowedTypes` optional (das heutige `[FieldLoader.Require]` muss
gelockert und durch eine Prüfung in `RulesetLoaded` ersetzt werden: entweder
`AllowedTypes` oder Capability-Felder, nie beides leer, nie beides gesetzt).

### Warum `RequireCapabilities` und nicht die Template-Tags

Template-Tags und Einheiten-Capabilities sind zwei Vokabulare mit Teilüberschneidung, und
sie sollen getrennt bleiben:

* Template-Tags sind **präskriptiv** („wofür ist dieser Trupp da"): `Frontline`, `Harass`,
  `Support`, `Transport` haben bewusst keine Entsprechung an Einheiten.
* Capabilities sind **deskriptiv** („was ist diese Einheit") und werden **dual** gelesen:
  `UpdateThreatTags()` zählt sie an *feindlichen* Aktoren, `PriorityTargetCapabilities`
  wählt Ziele danach aus. Rollen-Tags an Einheiten zu hängen würde damit die
  Bedrohungserfassung und die Zielwahl verfälschen — deshalb tun wir das nicht.

**Jede Ebene hat genau eine Aufgabe**, und darin liegt die eigentliche Vereinfachung:

| Ebene | Feld | Beantwortet |
|---|---|---|
| Template | `Tags` | *Wann* soll dieser Trupp überhaupt entstehen? (NeedRules, Produktionspriorität, Wellen) |
| Slot | `Wants` | *Wer* darf hinein? |

Die Template-Tags fließen bewusst **nicht** mehr in die Einheitenauswahl ein. Sonst würde
dieselbe Absicht zweimal zählen — im Entwurf davor bekam `gmisinf` seinen `AntiAir`-Bonus
einmal über die Template-Tags und einmal über `Prefers`. Die beiden Vokabulare
konkurrieren jetzt nicht mehr, weil sie auf verschiedenen Ebenen arbeiten.

---

## 4. Scoring

### 4.1 Score pro Einheitentyp

**Alles wird in einer einzigen Einheit gerechnet: 1 Punkt = 100 Credits.** Damit hat jede
Zahl im Scoring eine Bedeutung, die man aussprechen kann, statt eine willkürliche zu sein.

```
Score(t) = Cost(t) / 100                          // was die Einheit wert ist
         + PreferMatchWeight * |Prefers(s) ∩ Caps(t)|   // Bonus für Rollenpassung
         + Performance(t)                          // Bewährung, Stufe 2
```

`Wants` und die reservierten Capabilities sind Filter, keine Summanden — was den Filter
nicht passiert, taucht im Scoring gar nicht erst auf.

Defaults als Modul-Felder: `PreferMatchWeight: 25` (ein Treffer ist so viel wert wie
2500 Credits mehr Gerät), `CostPerValuePoint: 100`, `MaxPerformanceScore: 30`.

Der Wert-Term hat bewusst **keine** Obergrenze im Score. Die Obergrenze entsteht
strukturell: das Budget muss `MinCount` Einheiten tragen können. Bei `MinCount: 3` und
`MaxSlotCost: 4500` sind Einheiten über 1500 Credits nur beimischbar, nicht als
Vollbesetzung. Damit beantwortet sich die Frage nach einer Kostenobergrenze pro Squad
ohne zusätzlichen Regler — „vier Mammuts" scheitert am Budget, nicht an einer Sonderregel.

### 4.2 Kein Lock-in

Die Typenauswahl darf **kein `argmax`** sein. Genau diese Falle ist im Unit-Builder schon
einmal zugeschnappt (`CNUnitBuilderBotModule.cs:381-391`: gmisinf 16540 vs. e2 16539 —
ein Punkt Vorsprung hat die Queue dauerhaft an einen Typ vergeben). Die Auswahl benutzt
denselben Mechanismus wie die Template-Auswahl: `WeightedTemplateOrder(candidates, score)`
mit `TemplateSelectionSharpness` (Default 2). Das erfüllt die Randbedingung „Bots sollen
alle Einheitentypen durchgehend nutzen, Bedarf nur als leichter Bias".

**Aber: gewichtet wird die Differenz, nicht der Absolutwert.** Ein konstanter Sockel im
Score (im Vorentwurf die 200 Punkte aus den Tag-Treffern) verwässert alle Unterschiede zur
Gleichverteilung. Deshalb:

```
Gewicht(t) = (Score(t) - min(Score) + CandidateScoreOffset) ^ TemplateSelectionSharpness
```

`CandidateScoreOffset` (Default 20) verhindert das andere Extrem, in dem der Beste alles
bekommt. Am Pilot durchgerechnet, Slot `GDI_Armor_Frontline` 1 (`Wants: Vehicle, AntiArmor`,
kein `Prefers` — es entscheidet also allein der Wert):

| Typ | Kosten | Score | Anteil |
|---|---|---|---|
| gtmtnk | 700 | 7 | 12,6 % |
| mmch | 800 | 8 | 13,9 % |
| warden | 800 | 8 | 13,9 % |
| cyclops | 900 | 9 | 15,3 % |
| sonic | 1300 | 13 | 21,3 % |
| 4tnk | 1400 | 14 | 23,0 % |

Alles bleibt in Benutzung, das bessere Gerät ist klar bevorzugt. Mit Offset `0` kippt es
auf 4tnk 53 % / gtmtnk 1 % — zu hart.

Zweites Beispiel mit Bonus, Slot `GDI_Infantry_AntiArmor` (`Wants: Infantry, AntiArmor`,
`Prefers: AntiAir`): e2 (200) = 2 Punkte, ztrooper (1200) = 12, gmisinf (250) = 2 + 25 = 27.
Ergibt e2 12 % · ztrooper 27 % · gmisinf 61 %. Die Raketeninfanterie führt deutlich, wenn
Flugabwehr gefragt ist, ohne dass die anderen verschwinden.

### 4.3 Zwei Auflösungen, nicht eine

**A — Formation** (aus dem Live-Pool, beim Squad-Aufbau).
Kandidaten sind die tatsächlich vorhandenen idle Aktoren. Es gibt keine
Buildability-Frage: was da ist, ist da. Greedy über die gewichtete Reihenfolge, bis
`MaxCount` oder `MaxSlotCost` erreicht ist. Mischtrupps entstehen dabei von selbst.

Das Ergebnis wird **eingefroren**: der Slot-Assignment bekommt ein zur Laufzeit erzeugtes
`CNSlotInfo` mit `AllowedTypes` = den gewählten Typen in Score-Reihenfolge.
Das löst die Nachschub-Identität (kein Buggy als Ersatz im Titan-Trupp) und lässt
sämtliche States, `MissingCount`, `IsFulfilled` und den Reinforcement-Pfad unverändert.

### Stückzahl: drei Momente, drei Regeln

`MinCount` ist die **Losmarschstärke**, `MaxCount` die **Sollstärke**, das Budget bindet
dazwischen. Die Stückzahl wird **nicht** bei der Aufstellung eingefroren — sonst bliebe
ein Trupp, der früh mit drei billigen Panzern loszieht, für immer bei drei.

| Moment | Regel |
|---|---|
| **Aktivierung** | Slot gilt als erfüllt ab `MinCount`. Zusammen mit `MinSlotsToActivate` entscheidet das, wann der Trupp losmarschiert. |
| **Aufstellung** | Gierig aus dem Pool nehmen, bis `MaxCount` erreicht ist oder das Budget nicht mehr für eine weitere Einheit reicht. |
| **Nachschub** | `MissingCount > 0`, solange `aktuelle Kosten + billigster eingefrorener Typ ≤ MaxSlotCost` **und** `Anzahl < MaxCount`. |

**Unterhalb `MinCount` gilt das Budget nicht.** Sonst entstünde bei teuren Einheiten ein
Trupp, der die Mindestbesetzung nie erreicht, dauerhaft `MissingCount > 0` meldet und
endlos Produktionsbedarf erzeugt.

Durchgerechnet für `Armor_Line` Slot 2 (`MinCount: 3`, `MaxCount: 5`, `MaxSlotCost: 4000`):

| Eingefrorene Typen | Verlauf | Ende |
|---|---|---|
| gtmtnk 700, mmch 800 | los mit 3 (2200), wächst auf 5 (3800) | 5 Einheiten |
| 4tnk 1400, gtmtnk 700 | los mit 3 (3500), +700 wäre 4200 > 4000 | 3 Einheiten |
| nur 4tnk 1400 | 3 × 1400 = 4200 — unter `MinCount` zählt das Budget nicht | 3 Einheiten |

Damit ergibt sich die Truppgröße aus dem, was verfügbar und bezahlbar ist, statt aus einer
Zahl im YAML: billige Einheiten kommen in Fünfergruppen, teure in Dreiergruppen — ohne
dass jemand das irgendwo hinschreibt.

### Produktionsnachfrage: erst losmarschieren, dann aufstocken

Für eine **noch nicht existierende** Taskforce meldet der Slot Bedarf über `MinCount`,
nicht über `MaxCount`. Für eine **bestehende** zieht `MissingCount` bis `MaxCount` nach.

Grund: sonst hortet der Bot Einheiten für den perfekten Trupp, der nie zustande kommt,
während die fertigen Einheiten im Pool stehen. Genau dieses Muster ist der Grund, warum
Bots „idle" wirken.

**B — Bedarf** (aus dem Regelsatz, für noch nicht existierende Squads).
Kandidaten sind alle Aktortypen mit `BotCapabilities`, die den Filter passieren.
Ergebnis ist eine **Top-K-Liste** (`DemandCandidateCount`, Default 3), gecacht pro
(Template, Slot), invalidiert auf der Kadenz von `ThreatScanInterval` und bei Änderung
des eigenen Gebäudebestands (neue Tech = neue Kandidaten). Produzierbarkeit prüft
weiterhin der Unit-Builder (`IsBuildable`); nicht baubare Typen fallen dort heraus, und
bei K ≥ 3 bleibt fast immer ein baubarer übrig.

---

## 5. Bewährung: Kill-Tracking

Entschieden: **direkt mitbauen**, weil eine reine Verlustbilanz im Auswahl-Loop aktiv
schädlich wäre — eine Einheit, die nie kämpft, hätte die beste Bilanz, und der Bot würde
zu genau den Einheiten driften, die er nicht einsetzt.

Die Datenquelle fehlt heute. `CombatAnalysisBotModule` misst nur **eingehenden** Schaden
(`IBotRespondToAttack`, klassifiziert den Angreifer für die Verteidigungsplanung) — es
weiß nichts darüber, wie gut die eigenen Einheiten austeilen.

### 5.1 Erhebung: `BotCapabilities` erweitern statt neuen Trait bauen

`BotCapabilities` ist heute ein reiner Datenhalter (`BotCapabilities.cs:30-34`) und hängt
bereits an genau den ~50 Aktoren, die den Bot interessieren (198 Deklarationen im
Regelsatz). Er wird zum Rekorder erweitert — **null YAML-Aufwand**:

```csharp
public class BotCapabilities : INotifyAppliedDamage, INotifyKilled
```

Beide Interfaces existieren im Fork (`engine/OpenRA.Mods.Common/TraitsInterfaces.cs:127`
und `:129`).

* `AppliedDamage(self, damaged, e)` — bucht `e.Damage.Value` auf `self.Info.Name`,
  sofern der Besitzer ein Bot ist und das Ziel feindlich.
* `Killed(self, e)` — bucht den Verlust (inkl. `Valued.Cost`) auf `self.Info.Name` und den
  Kill-Wert auf `e.Attacker.Info.Name`.

### 5.2 Ablage: `CNCombatRecordBotModule`

Neues Player-Modul, Aufbau analog zu `CombatAnalysisBotModule`:
`Dictionary<string, Record { DamageDealt, KillValue, LossValue, Samples }>` mit Decay
über `IBotTick` (Muster: `DecayTagPerformance()`).

```
Performance(t) = clamp( (DamageDealt(t) + KillValue(t) - LossValue(t)) / Cost(t)
                        * PerformanceScale,
                        -MaxPerformanceScore, +MaxPerformanceScore )
```

Normierung **pro Kosten** ist zwingend, sonst gewinnt die teure Einheit doppelt: einmal
über den Wert-Term, einmal über absoluten Schaden. Unter `MinSamples` liefert der Term 0,
damit ein einzelnes Gefecht keinen Typ dauerhaft aus- oder einsortiert.

### 5.3 Randbedingung Determinismus

Bot-Entscheidungen sind in OpenRA sync-relevant (jeder Client simuliert den Bot mit).
Alle neuen Werte müssen daher aus der Simulation stammen und dürfen nicht von
Sichtbarkeit oder Client-Zustand abhängen. `INotifyAppliedDamage`/`INotifyKilled` laufen
simulationsseitig, `Damage.Value` ist deterministisch — die Bedingung ist erfüllt, muss
bei der Umsetzung aber bewusst gehalten werden. `float` im Bot-Pfad ist im Codebase
bereits Präzedenz (`tagPerformance`, `WeightedTemplateOrder`).

---

## 6. Produktion

Ein Resolver ersetzt alle direkten `AllowedTypes`-Zugriffe:

```csharp
public IReadOnlyList<string> ResolveDemandTypes(CNTeamTemplateInfo template, CNSlotInfo slot)
```

Statischer Slot → `slot.AllowedTypes` unverändert zurück. Dynamischer Slot → gecachte
Top-K-Rangliste (4.3 B).

| Call-Site | heute | neu |
|---|---|---|
| `AddPreferredDemand` | `slot.AllowedTypes` | `ResolveDemandTypes`, Scarcity-Spread bleibt |
| `GetTemplateUnitCap` | `Count * MaxInstances` je gelistetem Typ | dynamisch: nur Top-K, `MaxCount * MaxInstances` |
| `GetTypesIgnoringCashReserve` | `AllowedTypes` | Top-K |
| `TemplateAcceptsUnit` | Namensvergleich | Capability-Filter direkt gegen den Aktor |
| `TakeAvailableUnits` | Rotation über `AllowedTypes` | Formation-Resolver über den Live-Pool |
| `SelectBuildableType` (Builder) | `AllowedTypes` | `ResolveDemandTypes` (public API des Managers) |

### Zwei Fallen, die die Umsetzung explizit adressieren muss

**Scarcity-Spread.** `AddPreferredDemand` (`:992-1012`) lenkt Bedarf bevorzugt auf den
*am wenigsten* vorhandenen Kandidatentyp. Über eine breite Kandidatenliste würde der Bot
von allem eins bauen und nie kritische Masse erreichen — und weil billige Einheiten
schneller fertig sind, kippt der Bestand zusätzlich nach unten. Gegenmittel: kleines K
und score-sortierte Liste.

**Cap-Inflation.** `GetTemplateUnitCap` begrenzt heute wirksam, weil jeder Typ nur in
wenigen Templates auftaucht. Dynamisch aufgelöst über alle Kandidaten wäre der Cap
faktisch abgeschaltet. Deshalb zählen dynamische Slots nur mit ihren Top-K-Typen.

---

## 7. Was statisch bleiben muss

Transport-Templates beschreiben eine **Struktur**, keine Menge:
`IsCarrier`, `IsAircraftCarrier`, `IsPassenger`, `IsEscort`, `IsParadrop`,
`ReturnAfterUnload`. Diese Slots bleiben unverändert bei `AllowedTypes`.

Ein Template darf mischen: Carrier-Slot statisch, Eskorten-Slot dynamisch. Rollen
`Transport`, `SubterraneanTransport` und `AirTransport` bleiben in Stufe 1–4 vollständig
statisch.

---

## 8. Umsetzung in Stufen

### Stufe 1 — nur Engine, YAML unverändert (entschieden)

1. `CNSlotInfo` um die sechs Felder erweitern; `[FieldLoader.Require]` an `AllowedTypes`
   lockern; Validierung nach `RulesetLoaded` (dort liegen bereits die Tag-Prüfungen,
   `CNSquadManagerBotModule.cs:394-429`). Achtung: `CNSlotInfo` braucht dann einen
   expliziten parameterlosen Konstruktor, sobald ein Konstruktor für aufgelöste Slots
   hinzukommt — sonst bricht `FieldLoader.Load<CNSlotInfo>`.
2. `ResolveDemandTypes` + Cache; alle sechs Call-Sites umstellen. Für statische Slots
   muss das Verhalten **bitweise identisch** bleiben.
3. `MinCount` in `CNSlotAssignment.IsFulfilled` und `CNSquad.OperationalSlotCount()`
   berücksichtigen; Default `MinCount = Count` hält das Verhalten unverändert.
4. Formation-Resolver + Scoring implementieren, aber ohne dynamische Templates ist er
   unbenutzt — deshalb:
5. **Debug-Ausgabe** (Chat-Befehl analog `cntopo`, siehe `CNTacticalMapOverlay.cs`, oder
   `AIUtils.BotDebug`-Dump): pro Template und Slot die aufgelöste Rangliste mit
   Score-Aufschlüsselung (Prefer-Treffer / Tag-Treffer / Wert / Bewährung).

> Ehrlicher Hinweis zur gewählten Option: Stufe 1 ändert per Konstruktion **kein**
> Spielverhalten. Ohne Punkt 5 wäre sie schlicht nicht verifizierbar — der Debug-Dump ist
> hier kein Nice-to-have, sondern die einzige Beobachtung, die es zu machen gibt.

Verifikation: `make all` grün · `make test` gegen die bestehende rote Baseline
(2706 Warnungen / 6980 Fehler) unverändert · ein Spiel gestartet, Bot-Verhalten
unverändert · Debug-Dump zeigt plausible Ranglisten.

### Stufe 2 — Bewährung

`BotCapabilities` als Rekorder, `CNCombatRecordBotModule`, zunächst **ohne** Einfluss auf
die Auswahl (`PerformanceWeight: 0`), nur im Debug-Dump sichtbar. Erst wenn die Zahlen
über mehrere Spiele plausibel aussehen, den Term einschalten.

### Stufe 3 — Pilot-YAML

`squads-rush.yaml`, nur Rolle `Assault`: ~6 dynamische Templates ersetzen 18 statische.
Über eine eigene `RequiresCondition` gegen das bestehende Profil umschaltbar, damit sich
altes und neues Verhalten im selben Build vergleichen lassen.

### Stufe 4 — Ausrollen

Übrige Profile, danach `Raider`, `Stealth`, `ArtilleryAssault`. Transport bleibt statisch.

### Realistische Ersetzungsquote

Am Pilot durchgerechnet (`docs/dynamic-squads-rush-pilot.yaml`, Profil `rush`):
**18 Assault-Templates → 7 dynamische + 2 statisch gehaltene = 9.** Die Achsen sind
`AntiInfantry` / `AntiArmor` / `AntiAir` × `Infantry` / `Vehicle`, je Fraktion getrennt,
weil `ScaleWithBuilding` fraktionsspezifisch ist (`gaweap` vs. `naweap`) und genau dadurch
die Fraktionstrennung schon leistet.

Statisch bleiben die Einzelstück-Templates (`GDI_MammothMkII_Tech`, `Nod_Sting_Epic`):
`Bias 20` / `IgnoresCashReserve` / `Count: 1` — dort **ist** die Typliste die Absicht.

Hochgerechnet über alle fünf Profile: **~45 statt 90** Assault-Templates, Gesamtbestand
grob 160 statt 208.

Weiter kürzen ließe sich das nur über profilübergreifende Wiederverwendung — `Teams` ist
aber ein Trait-Dictionary-Feld, das ginge nur über einen gemeinsamen abstrakten
Player-Actor, und die Profile unterscheiden sich gerade in ihren Gewichten. Nicht Teil
dieses Konzepts.

---

## 9. Offene Risiken

1. **Gleichstand bleibt teilweise bestehen.** Der Wert-Term ordnet TTNK vs. LTNK korrekt,
   entscheidet GTMTNK vs. MMCH aber nur über den Preis. Gegenmittel, falls es im Spiel
   auffällt: das Capability-Vokabular gezielt schärfen (es fehlt eine Reichweiten- bzw.
   Rollen-Achse), **nicht** das Scoring verkomplizieren.
2. **Cache-Kadenz.** Zu selten invalidiert → neue Tech wird spät genutzt; zu oft →
   unnötige Last bei ~40 Kandidaten × ~200 Templates. Start: Kadenz von
   `ThreatScanInterval`, plus Invalidierung bei Gebäudebestandsänderung.
3. **Budget-Tuning.** `MaxSlotCost` ist der einzige Regler gegen Trupps aus vier Mammuts.
   Zu eng gesetzt sperrt er Spät-Tech komplett aus. Startwerte pro Rolle konservativ, im
   Spiel nachziehen.
4. **`make test` ist vorbestehend rot.** Ein YAML-Fehler in Stufe 3 wäre in diesem Rauschen
   schwer zu sehen; die YAML-Prüfung (`--check-yaml`) muss vor und nach der Änderung
   verglichen werden, nicht nur nachher gelesen.
