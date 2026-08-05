# Capability-Vokabular: was den Einheiten noch fehlt

Ergänzung zu `dynamic-squads-concept.md`. Sobald die Templates wegfallen, sind die
`BotCapabilities` der Einheiten die **einzige** Beschreibung der Armee. Sie müssen dann
tragen, was heute in 208 Templates von Hand steht.

## Grundsatz

Nicht jede Einheit muss unterscheidbar sein. Archon (700) und Titan (800) tragen beide
`Vehicle, AntiArmor` und sollen austauschbar sein — der Wert-Term sortiert sie. Getrennt
gehören nur Einheiten, die **anders eingesetzt** werden.

Deshalb: **nur die Ausnahmen bekommen ein Wort.** Wer nichts trägt, ist Linieninfanterie
bzw. Linienfahrzeug. Das betrifft rund 20 von ~50 Einheiten.

## Zwei neue Wörter

| Wort | Bedeutung | Ersetzt heute |
|---|---|---|
| `Skirmisher` | schnell, fragil, Hit-and-Run — hält keine Linie | die Rolle `Raider` und den Template-Tag `Harass`, beides bisher von Hand vergeben |
| `Heavy` | zäh und langsam, führt den Stoß an | den Template-Tag `Frontline` und die Einzeltemplates für schwere Einheiten |

Ohne `Skirmisher` gibt es keine Ableitung mehr, dass ein Buggy ein Raider ist und ein
Scorpion nicht — heute steht das nur in `Nod_Buggy_Raid: Role: Raider`.

## Ein bestehendes Wort konsequent vergeben

`Siege` existiert bereits, trägt es aber nur `STING`. Gleichzeitig gibt es die NeedRule
`Siege: EnemyCapabilities: Defense, Production, Tech, Superweapon, Artillery` in **jedem**
Profil. Das funktioniert heute nur, weil die Templates den Tag tragen. Ohne Templates
liefe die Regel ins Leere. `Siege` gehört an alles, was Gebäude knackt.

## Zuordnung

Nur die Einheiten, die etwas dazubekommen. Alles Übrige bleibt unverändert.

### `Skirmisher`

| Einheit | Kosten | heute |
|---|---|---|
| HVR (Hover MLRS) | 900 | `GDI_HoverMLRS_Raid`, Role Raider |
| JUMPJET | 600 | `GDI_JumpjetInfantry`, Role Raider |
| GASNIPER | 500 | `GDI_SniperInfantry`, Role Stealth |
| BGGY | 500 | `Nod_Buggy_Raid`, Role Raider |
| BIKE | 600 | `Nod_Bike_Raid`, Role Raider |
| SHADOW | 800 | `Nod_SniperInfantry`, Role Stealth |
| STNK | 1100 | `Nod_StealthTank_Strike`, Role Stealth |
| SUBTANK | 750 | `Nod_Subtank_Raid`, Role SubterraneanAssault |
| ORCA_F1 | 1000 | `GDI_Orca_Raid`, Role AircraftRaider |
| HORNET | 1500 | `GDI_Hornet_Strike`, Role AircraftRaider |
| HYDRA | 1000 | `Nod_Hydra_Raid`, Role AircraftRaider |

### `Heavy`

| Einheit | Kosten | heute |
|---|---|---|
| 4TNK (Mammoth) | 1400 | `GDI_Mammoth_Attack`, Bias 10 |
| ZTROOPER | 1200 | `GDI_ZoneTrooper_Tech`, Bias 8 |
| HMEC (Mammoth Mk II) | 3000 | `GDI_MammothMkII_Tech`, Bias 20 |
| ACOLYTE | 1000 | `Nod_Acolyte_Tech`, Bias 8 |
| STING | 3000 | `Nod_Sting_Epic`, Bias 14 |

### `Siege` (nachtragen)

| Einheit | Kosten | heute |
|---|---|---|
| JUGG | 950 | `GDI_Juggernaut_Siege`, Tag Siege |
| SONIC | 1300 | `GDI_Sonic_Breaker`, Tag Siege |
| ORCA_B | 1600 | `GDI_Orca_Bomber`, Tag Siege |
| HMEC | 3000 | `GDI_MammothMkII_Tech`, Tag Siege |
| NHWTZ | 975 | `Nod_Artillery_Siege`, Tag Siege |
| SCRIN | 1500 | `Nod_Scrin_Strike`, Tag Siege |
| STING | 3000 | trägt `Siege` bereits |

### `Unique` (siehe Konzept 3.2)

`HMEC`, `STING` — für dynamische Slots gesperrt, bleiben ihren eigenen Templates
vorbehalten.

## Beispiel: eine Einheit vorher / nachher

```
	4TNK:
		BotCapabilities:
			Capabilities: Vehicle, AntiArmor, AntiAir            # vorher
			Capabilities: Vehicle, AntiArmor, AntiAir, Heavy     # nachher
```

Ein Wort. Damit weiß der Bot, dass der Mammut den Stoß anführt statt hinterherzufahren —
eine Information, die heute in `GDI_Mammoth_Attack: Bias: 10` über fünf Profildateien
verteilt steht.

## Was damit möglich wird

### Keine Rollenableitung

Ein früherer Entwurf wollte die Rolle (`Raider`, `Assault`, `Stealth`, …) aus den
Capabilities ableiten. **Verworfen.** Die Ableitung beschreibt nur die *natürliche* Rolle
einer Einheit — was ein Profil ausmacht, ist aber gerade die Abweichung davon: der
Rush-Bot soll den Scorpion auch als Raider losschicken dürfen, der Steamroller nicht.

Die Rolle bleibt deshalb an der Taskforce, wie heute. Die Aktor-Tags beantworten nur,
**wer hinein darf** — und weil `Wants` hart und `Prefers` weich filtert, entscheidet ein
einziger Profil-Regler (`PreferMatchWeight`), wie streng die Rollentrennung genommen wird.

### Die drei Ebenen

| Ebene | Wo | Beantwortet | Umfang |
|---|---|---|---|
| Profil | `squads-*.yaml`, Modul-Felder | *Wie* kämpft dieser Bot? (`RoleWeights`, `TagWeights`, `NeedRules`, `PreferMatchWeight`) | ~15 Zeilen je Profil |
| Taskforce | Templates, für alle Profile gemeinsam | *Wann* entsteht dieser Trupp (`Tags`), wie sieht er aus (`Role`, Slots, Budget)? | ~12 Formen, einmal |
| Einheit | `BotCapabilities` | *Was ist* diese Einheit? | 1 Zeile je Einheit |

Der Ablauf: `NeedRules` sieht die Capabilities der **feindlichen** Aktoren → hebt den Score
der Taskforces, deren `Tags` dazu passen → die gewinnende Taskforce füllt ihre Slots über
die Capabilities der **eigenen** Aktoren.

Jede Ebene weiß genau das, was sie braucht, und nichts darüber hinaus: das Profil nennt
keine Einheiten, die Taskforce nennt keine Einheitennamen, die Einheit kennt keine Rollen.

### Was damit trotzdem gewonnen ist

`Skirmisher`, `Heavy` und ein konsequentes `Siege` bleiben nötig — nicht um Rollen
abzuleiten, sondern damit `Prefers` überhaupt etwas zu ranken hat und die NeedRule `Siege`
nicht ins Leere läuft. Trupp-Formen werden damit aussprechbar:

Und Trupp-Formen, die man aussprechen kann:

```
	Slots:
		1:
			Wants: Vehicle, Heavy         # die Spitze
			MaxCount: 2
		2:
			Wants: Vehicle, AntiArmor     # die Linie
			MinCount: 3
			MaxCount: 5
```

## Zwei Lücken, die der Abgleich aufgedeckt hat

**REPAIR (1000)** trägt heute nur `Capabilities: Vehicle` — nichts unterscheidet den
Reparaturfahrzeug-Trupp von einem Kampftrupp. `Nod_Repair` ist von Hand auf
`Role: Support, StayInBase: True` gesetzt. Braucht eine eigene Capability `Repair`,
analog zu `Medic`.

**SGEN (1600)**, der mobile Tarngenerator, trägt `Vehicle, Cloaked` und würde damit über
Regel 4 als `Stealth`-Angriffstrupp eingeordnet — falsch, er ist ein Unterstützer, der bei
der Armee bleibt. Er taucht in keinem heutigen Template auf, das Problem ist also latent.
Sauberste Lösung: `Support` als eigene Capability, die vor `Cloaked` greift.

Randnotiz: `LPST` (mobiler Sensor) und `MOBILEMP` (EMP-Fahrzeug) haben dasselbe Problem in
klein — sie tragen keine Capability, die eine sinnvolle Rolle ergäbe, und landen bei
`Assault`. Heute stehen beide in keinem Template. Vor dem Ausrollen entscheiden, nicht
vorher.
