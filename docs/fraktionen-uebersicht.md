# Crystallized Nexus — Fraktionenübersicht

> Basis für Subfraktions-Planung. Jede Sektion kann um eigene Gebäude, Einheiten und Tech-Tree-Knoten erweitert werden.

---

## Inhaltsverzeichnis

- [GDI](#gdi-global-defense-initiative)
- [Nod](#nod-brotherhood-of-nod)
- [Subfraktion-Vorlage](#subfraktion-vorlage)
- [Geplante Subfraktionen](#geplante-subfraktionen)
  - [GDI: GDF](#gdf---global-defense-force--gdi)
  - [GDI: Steel Talons](#stl---steel-talons--gdi)
  - [GDI: Zone Command](#zco---zone-command--gdi)
  - [GDI: Spearhead Division](#spd---spearhead-division--gdi)
  - [GDI: Aegis Initiative](#agi---aegis-initiative--gdi)
  - [GDI: Overwatch Command](#owc---overwatch-command--gdi)
  - [Nod: Marked of Kane](#marked-of-kane--nod)
  - [Nod: Black Hand](#black-hand--nod)
  - [Nod: Confessors](#confessors--nod)
  - [Nod: Iron Brotherhood](#iron-brotherhood--nod)
- [Post-Firestorm Lore — CABAL Returns](#post-firestorm-lore--cabal-returns)

---

## GDI — Global Defense Initiative

### Gebäude

| Actor ID | Name | Kategorie | Notizen |
|---|---|---|---|
| `GACNST` | Construction Yard | Basis | Startgebäude |
| `GAPOWR` | Power Plant | Energie | |
| `upgrade.gapowerup1` | Power Turbine Upgrade | Energie | Tech-Upgrade |
| `upgrade.gapowerup2` | Power Turbine Upgrade Adv. | Energie | Tech-Upgrade |
| `GPROC` | Tiberium Refinery | Wirtschaft | Harvester-Dock |
| `GASILO` | Resource Silo | Wirtschaft | Shared |
| `GAPILE` | Barracks | Produktion | Infanterie |
| `GAWEAP` | Vehicle Factory | Produktion | Fahrzeuge |
| `GAHPAD` | Helipad | Produktion | Lufteinheiten |
| `GADEPT` | Repair Depot | Support | Fahrzeugreparatur |
| `GARADR` | Radar Station | Tech | Radar + Detection |
| `GAARMORY` | Armory | Tech | Mitteltech |
| `GATECH` | Tech Center | Tech | Hightech-Freischaltung |
| `GAPLUG` | Superweapon Hub | Superwaffe | Basisstruktur |
| `GAPLUG2` | Hunter Seeker Plug | Superwaffe | Addon |
| `GAPLUG3` | Ion Cannon Plug | Superwaffe | Addon |
| `GAPLUG4` | Drop Pod Plug | Superwaffe | Addon |
| `GAFIRE` | Firestorm Defense | Verteidigung | Superwaffe |
| `GAMG` | Machine Gun Turret | Verteidigung | Anti-Infanterie |
| `GAWALL` | Concrete Wall | Verteidigung | |
| `GAGATE_A` | Gate (Type A) | Verteidigung | |
| `GAGATE_B` | Gate (Type B) | Verteidigung | |

### Infanterie

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `GASOL` | Soldier Squad | Squad | Mini-Gunner, `GASOL.Member` |
| `GASOLR3` | Elite Soldier Squad | Squad | Level-3-Veteran |
| `E2` | Disc Thrower Squad | Squad | Anti-Armor, `E2.Member` |
| `E2R3` | Elite Disc Thrower | Squad | Level-3-Veteran |
| `ENGINEER` | Engineer | Einzeln | Gebäude einnehmen/reparieren |
| `MEDIC` | Medic | Einzeln | Heilt Infanterie |
| `GASNIPER` | Marksman | Einzeln | Getarnt |
| `JUMPJET` | Jump Jet Infantry | Einzeln | Fliegend, Anti-Inf/Armor |

### Fahrzeuge

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `GMCV` | MCV | Support | Mobile Construction Vehicle |
| `GHARV` | Harvester | Support | Tiberium-Sammler |
| `APC` | APC | Transport | Infanterietransport |
| `HVR` | Hovercraft | Kampf | Hover-Tank, Raketen |
| `SMECH` | Stealth Mech | Kampf | Anti-Infanterie, Walker |
| `GTMTNK` | Medium Tank | Kampf | 75mm-Kanone |
| `MMCH` | Mammoth Mech | Kampf | Walker, 120mm |
| `4TNK` | 4-Gun Mammoth | Kampf | Dual 120mm + Raketen |
| `HMEC` | MARV / Heavy Mech | Kampf | Super-Heavy |
| `SONIC` | Sonic Tank | Kampf | Schallwaffe |
| `JUGG` | Juggernaut | Artillerie | Deployable |
| `MOBILEMP` | Mobile EMP | Support | EMP-Impuls |

### Lufteinheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `ORCA_F1` | Orca Gunship | Kampf | Air-to-Ground |
| `ORCA_B` | Orca Bomber | Kampf | Bombenwurf |
| `ORCA_TRAN` | Orca Transport | Transport | Infanterietransport |
| `TRNSPORT` | Carryall | Transport | Fahrzeug-Carryall |
| `DPOD` | Drop Pod | Superwaffe | Infanterieabwurf |

---

## Nod — Brotherhood of Nod

### Gebäude

| Actor ID | Name | Kategorie | Notizen |
|---|---|---|---|
| `NACNST` | Construction Yard | Basis | Startgebäude |
| `NAPOWR` | Power Plant | Energie | |
| `NAAPWR` | Advanced Power Plant | Energie | Höhere Kapazität |
| `NPROC` | Tiberium Refinery | Wirtschaft | Harvester-Dock |
| `NAHAND` | Hand of Nod | Produktion | Infanterie |
| `NAWEAP` | Vehicle Factory | Produktion | Fahrzeuge |
| `NAHPAD` | Helipad | Produktion | Lufteinheiten |
| `NARADR` | Radar Station | Tech | Radar |
| `NATECH` | Tech Center | Tech | Hightech-Freischaltung |
| `NASTLH` | Stealth Lab | Tech | Stealth-Einheiten |
| `NAPYRA` | Pyramid | Superwaffe/Verteidigung | |
| `NATMPL` | Temple of Nod | Verteidigung | |
| `NAWALL` | Concrete Wall | Verteidigung | |
| `NAGATE_A` | Gate (Type A) | Verteidigung | |
| `NAGATE_B` | Gate (Type B) | Verteidigung | |
| `NAPOST` | Laser Post | Verteidigung | Netzwerk-Zaunpfosten |
| `NAFNCE` | Laser Fence | Verteidigung | Zaunsegment |

### Infanterie

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `NASOL` | Soldier Squad | Squad | Automatikgewehr, `NASOL.Member` |
| `NASOLR3` | Elite Soldier Squad | Squad | Level-3-Veteran |
| `E3` | Bazooka Squad | Squad | Anti-Armor, `E3.Member` |
| `E3R3` | Elite Bazooka Squad | Squad | Level-3-Veteran |
| `NENGINEER` | Engineer / Saboteur | Einzeln | Gebäude einnehmen/sabotieren |
| `NACAD` | Elite Cadre | Squad | Dual-Waffe, `NACAD.Member` |
| `SHADOW` | Shadow Commando | Squad | Getarnt, `SHADOW.Member` |
| `ACOLYTE` | Acolyte | Squad | Fanatiker, `ACOLYTE.Member` |
| `NAFLAMER` | Flamer | Einzeln | Brandwaffe |

### Fahrzeuge

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `NMCV` | MCV | Support | Mobile Construction Vehicle |
| `NHARV` | Harvester | Support | Tiberium-Sammler |
| `BGGY` | Buggy | Schnell | Leichtes Fahrzeug |
| `BIKE` | Bike | Schnell | Motorrad |
| `LTNK` | Light Tank | Kampf | 90mm-Kanone |
| `TTNK` | Turret Tank | Kampf | Deployable, Twin-Gun |
| `NHWTZ` | Howitzer | Artillerie | Deployable |
| `REPAIR` | Repair Vehicle | Support | Mobile Reparatur |
| `WEED` | Defiler | Support | Tiberium-Einheit |
| `SAPC` | Armored APC | Transport | Infanterietransport |
| `SUBTANK` | Submarine Tank | Kampf | Unterwasser |
| `STNK` | Stealth Tank | Kampf | Getarnt |
| `SGEN` | Subterranean Generator | Support | Unterirdisch |

### Lufteinheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `SCRIN` | Scrin Aircraft | Kampf | Proton-Bomber |
| `HYDRA` | Hydra | Kampf | Angriffshubschrauber |

---

## Subfraktion-Vorlage

> Kopiere diesen Block für jede neue Subfraktion. Subfraktionen erben alle Basis-Einheiten ihrer Elternfraktion und ersetzen/ergänzen bestimmte Slots.

```
### [SUBFRAKTION-NAME] ← [GDI / Nod]

**Thema:** ...
**Stärken:** ...
**Schwächen:** ...
**Ersetzt:** ...
**Einzigartiges Superweapon:** ...

#### Ersetzt / Entfernt

| Basis Actor ID | Ersetzt durch | Grund |
|---|---|---|
| `ACTOR_ID` | `NEW_ACTOR_ID` | ... |

#### Neue Gebäude

| Actor ID | Name | Kategorie | Notizen |
|---|---|---|---|
| `ACTOR_ID` | Name | Kategorie | ... |

#### Neue / Geänderte Einheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `ACTOR_ID` | Name | Typ | ... |

#### Tech-Tree-Änderungen

- Freischaltbedingungen: ...
- Gesperrte Einheiten: ...
```

---

## Geplante Subfraktionen

> Jede Subfraktion erbt die Basis-GDI/Nod-Roster und modifiziert nur die aufgeführten Slots.
> Leere Tabellen = noch zu befüllen. Konzept steht, Implementierung folgt.

---

### GDF — Global Defense Force ← GDI

**Thema:** Verteidigung, Artillerie, schwere Fahrzeuge — klassische Haltungsstrategie  
**Stärken:** Stärkste stationäre Verteidigung aller GDI-Subfraktionen, günstige Heavy Vehicles, sehr robuste Walls  
**Schwächen:** Langsamste Mobilität, kaum Luftunterstützung, kein Stealth-Konter  
**Superweapon:** Firestorm Barrier (erweiterte Reichweite / schnelleres Aufladen)  
**Lore:** Die GDF ist der konventionelle Arm der GDI — Regulärtruppen, keine Experimentaltechnik. Sie halten die gelben Zonen und sichern Evakuierungskorridore.

#### Ersetzt / Entfernt

| Basis Actor ID | Ersetzt durch | Grund |
|---|---|---|
| `SONIC` | `GDF_SPG` — Heavy Self-Propelled Gun | Artillerie statt Experimental |
| `HMEC` | — (entfernt) | Kein MARV, zu prototypartig |
| `MOBILEMP` | `GDF_MEMS` — Statischer EMP-Turret | Defensive statt mobile |
| Ion Cannon Plug | — (entfernt) | GDF verlässt sich nicht auf Orbital-Support |

#### Neue Gebäude

| Actor ID | Name | Kategorie | Notizen |
|---|---|---|---|
| `GDF_BUNK` | Reinforced Bunker | Verteidigung | Infanterie-Garrison, stark gepanzert |
| `GDF_ARTDEP` | Artillery Depot | Produktion | Schaltet Heavy Artillery frei |
| `GDF_FIREW` | Extended Firestorm Hub | Superwaffe | Größerer Radius als Basis |

#### Neue / Geänderte Einheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `GDF_SPG` | Heavy SPG | Artillerie | Länger Range als Juggernaut, nicht deployable |
| `GDF_HWTZ` | Field Howitzer | Artillerie | Billiger als Juggernaut, weniger Schaden |
| `GDF_SHLD` | Shield APC | Transport | APC mit Frontschild, langsamer |

#### Tech-Tree-Änderungen

- Firestorm-Superwaffe früher verfügbar (nach Armory statt Tech Center)
- Ion Cannon gesperrt
- Artillerie-Fahrzeuge ohne Armory verfügbar

---

### STL — Steel Talons ← GDI

**Thema:** Aggressiv, Mechs, Brute Force — TS-Ära Tech ohne Schnörkel  
**Stärken:** Beste Walker/Mech-Roster, hohe Feuerkraft, starke Veteranen-Boni  
**Schwächen:** Teuer, langsam, kaum Stealth-Konter außer roher Kraft  
**Superweapon:** Orbital Hammer Strike (kinetisches Impaktgeschoss statt Ion Beam)  
**Lore:** Steel Talons sind die Hardliner unter den GDI-Generälen — sie misstrauen Prototypen und setzen auf bewährte, aufgerüstete TS-Era Panzer und Walker.

#### Ersetzt / Entfernt

| Basis Actor ID | Ersetzt durch | Grund |
|---|---|---|
| `APC` | `STL_MECH_APC` — Mech Carrier | Alles wird zur Walker-Variante |
| `HVR` | `STL_RTANK` — Rhino Heavy Tank | Hover zu unzuverlässig für Talons |
| `SMECH` | `STL_ASSAULT_MECH` — Assault Walker | Stärker, teurer |
| `SONIC` | — (entfernt) | Experimental, nicht zugelassen |
| `MOBILEMP` | — (entfernt) | Kein EMP-Fokus |

#### Neue Gebäude

| Actor ID | Name | Kategorie | Notizen |
|---|---|---|---|
| `STL_MFACT` | Mech Factory | Produktion | Schaltet alle Walker-Varianten frei |
| `STL_ARMUP` | Heavy Armor Upgrade Bay | Tech | Passive Rüstungsbonus für alle Fahrzeuge |

#### Neue / Geänderte Einheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `STL_ASSAULT_MECH` | Assault Walker | Kampf | Schneller als MMCH, weniger HP |
| `STL_RTANK` | Rhino Tank | Kampf | Erhöhte Panzerung, 120mm |
| `STL_MECH_APC` | Mech Carrier | Transport | Setzt Infanterie als Walker-Begleitung ein |
| `STL_TITAN` | Titan Mech | Kampf | TS-era Titan, günstiger als MMCH |

#### Tech-Tree-Änderungen

- Alle Walker ohne Armory verfügbar (schon ab Weapons Factory)
- MARV (`HMEC`) bleibt, aber günstigere Voraussetzungen
- Sonic Tank bleibt dauerhaft gesperrt

---

### ZCO — Zone Command ← GDI

**Thema:** Support, schwere Infanterie, Tiberium-Immunität, Sonic-Technologie  
**Stärken:** Beste Infanterie aller GDI-Subfraktionen, Tiberium-Felder kein Hindernis, starke Zone Troopers  
**Schwächen:** Wenig Fahrzeuge, teure Infanterie, schwache Luftabwehr  
**Superweapon:** Sonic Pulse Emitter (AoE Schallwelle über großen Radius)  
**Lore:** Zone Command operiert in den roten und gelben Zonen direkt im Tiberium-verseuchten Territorium. Ihre Soldaten tragen Zone Trooper Suits und ihre Forscher treiben die Sonic-Waffentechnik voran.

#### Ersetzt / Entfernt

| Basis Actor ID | Ersetzt durch | Grund |
|---|---|---|
| `GASOL` | `ZCO_ZTROOPER` — Zone Trooper Squad | Bessere Infanterie, teurer |
| `GTMTNK` | — (entfernt) | Kein Standard-Tank, Fokus auf Infanterie |
| `HVR` | `ZCO_SONIC_HOVERTANK` — Sonic Hovercraft | Schallwaffe auf Hover-Plattform |
| Drop Pod Plug | `ZCO_SONIC_SW` — Sonic Emitter Plug | Sonic statt Drop Pods |

#### Neue Gebäude

| Actor ID | Name | Kategorie | Notizen |
|---|---|---|---|
| `ZCO_REFLAB` | Tiberium Refinement Lab | Tech | Höhere Kredit-Ausbeute pro Tiberium |
| `ZCO_SLAB` | Sonic Research Lab | Tech | Schaltet Sonic-Waffen frei |
| `ZCO_ZSUIT` | Zone Armor Bay | Produktion | Upgrade für Zone Trooper |

#### Neue / Geänderte Einheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `ZCO_ZTROOPER` | Zone Trooper | Squad | Tiberium-immun, höhere HP, Anti-Armor |
| `ZCO_ZCOMMANDO` | Zone Commando | Einzeln | Elite-Einzelkämpfer, Tiberium-immun |
| `ZCO_SONIC_HOVERTANK` | Sonic Hovercraft | Kampf | Sonic-Waffe + Hover-Mobilität |
| `ZCO_HARVADV` | Advanced Harvester | Support | Immunisiert, höhere Ladekapazität |

#### Tech-Tree-Änderungen

- Tiberium-Felder schaden keiner ZCO-Einheit
- Sonic Tank (`SONIC`) billiger und früher verfügbar
- Juggernaut gesperrt (kein Artillery-Fokus)
- Refinement Lab: +20% Tiberium-Erlös passive

---

### SPD — Spearhead Division ← GDI

**Thema:** Mobilität, schnelle Fahrzeuge, Map Control, Harassment  
**Stärken:** Schnellste GDI-Subfraktion, billige Scouts, exzellente Flankenangriffe  
**Schwächen:** Geringste Panzerung, schlechte stationäre Verteidigung, kaum Artillerie  
**Superweapon:** Hunter Seeker Swarm (mehrere gleichzeitig statt einer)  
**Lore:** Die Spearhead Division ist GDIs Reaktionskraft — schnelle Eingreiftruppen, die Versorgungslinien kappen, Flanken schließen und Positionen vor der Hauptstreitmacht sichern.

#### Ersetzt / Entfernt

| Basis Actor ID | Ersetzt durch | Grund |
|---|---|---|
| `MMCH` | — (entfernt) | Zu langsam für SPD-Doktrin |
| `4TNK` | — (entfernt) | Zu schwer |
| `JUGG` | — (entfernt) | Kein Artillerie-Fokus |
| `HMEC` | — (entfernt) | Super-Heavy passt nicht |
| `GAMG` | `SPD_LTURRET` — Light Rapid-Fire Turret | Leichter, billiger, aber weniger HP |

#### Neue Gebäude

| Actor ID | Name | Kategorie | Notizen |
|---|---|---|---|
| `SPD_MOTORPOOL` | Forward Motor Pool | Produktion | Fahrzeuge 15% günstiger + schneller |
| `SPD_OUTPOST` | Forward Outpost | Basis | Deployable, gibt Radar + Bauplatz |

#### Neue / Geänderte Einheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `SPD_SCOUT` | Scout Bike | Schnell | Sehr schnell, fast keine Panzerung |
| `SPD_FASTTANK` | Predator Fast Tank | Kampf | Leichtere Version des Medium Tank |
| `SPD_STRKORCA` | Strike Orca | Kampf | Schnellere Orca, geringere HP |
| `SPD_RAIDER` | Raider APC | Transport | Schnellster Transport, baut keine Garnisonen |

#### Tech-Tree-Änderungen

- Alle Fahrzeuge +10% Grundgeschwindigkeit
- Keine Heavy Vehicles verfügbar
- Forward Outpost: Kann auf der Karte deployed werden als Mini-Basis

---

### AGI — Aegis Initiative ← GDI

**Thema:** Tech, Prototypen, seltene Fahrzeuge, Ion-Waffen, Sonic  
**Stärken:** Stärkste Einzeleinheiten aller GDI-Subfraktionen, einzigartige Waffentypen  
**Schwächen:** Teuerste Einheiten, kleinste Armee, langsamer Aufbau  
**Superweapon:** Focused Ion Cannon (schmaler, piercing Beam statt Bereich — mehrere Gebäude in einer Reihe)  
**Lore:** Aegis Initiative ist GDIs Forschungsarm. Sie testen Technologien, die offiziell noch nicht freigegeben sind — einige davon gefährlich nah an CABAL-Architektur.

#### Ersetzt / Entfernt

| Basis Actor ID | Ersetzt durch | Grund |
|---|---|---|
| `GASOL` | `AGI_RESGUARD` — Research Guard | Etwas besser, aber kein Squad |
| `GTMTNK` | `AGI_PROXTANK` — Prototype Medium Tank | Railgun statt Kanone |
| `GHARV` | `AGI_AUTOHARV` — Autonomous Harvester | Selbst-reparierend, teurer |
| Hunter Seeker Plug | `AGI_IONFOC` — Focused Ion Plug | Andere Superwaffe |

#### Neue Gebäude

| Actor ID | Name | Kategorie | Notizen |
|---|---|---|---|
| `AGI_RESLAB` | Research Laboratory | Tech | Schaltet alle Prototyp-Einheiten frei |
| `AGI_SHIELD` | Energy Shield Projector | Verteidigung | Deflektionsschild für benachbarte Gebäude |
| `AGI_PLAB` | Prototype Lab | Tech | Unique Units (nur 1 gleichzeitig baubar) |

#### Neue / Geänderte Einheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `AGI_PROXTANK` | Prototype Tank | Kampf | Railgun, panzerbrechend, teuer |
| `AGI_IONTANK` | Ion Tank | Kampf | Miniaturisierter Ion-Emitter |
| `AGI_AUTOHARV` | Autonomous Harvester | Support | Kein Begleitschutz nötig, selbst-reparierend |
| `AGI_MECH_ELITE` | Prototype Walker | Kampf | Einzigartiger Super-Walker, Limit 1 |

#### Tech-Tree-Änderungen

- Alle Unique Units: Max. 1 gleichzeitig (Prototype Lab)
- Kein Drop Pod Support
- Ion Cannon modifiziert: Focused-Variante, trifft Linie statt Kreis

---

### OWC — Overwatch Command ← GDI

**Thema:** Kartenkontrolle, Aufklärung, Orbitalunterstützung, schwache Direktarmee  
**Stärken:** Permanenter Map-Überblick, günstige Support-Fähigkeiten, GDSS Philadelphia Orbital Support  
**Schwächen:** Schwächste Direktkampfarmee aller GDI-Subfraktionen, teuer im Aufbau  
**Superweapon:** Philadelphia Orbital Strike (mehrere kleinere Strikes wählbar statt einer großer)  
**Lore:** Overwatch Command koordiniert alle GDI-Operationen von der GDSS Philadelphia aus. In der Post-Firestorm-Ära übernehmen sie eine neue Rolle: Früherkennung von CABAL-Netznoten und koordinierte Striketeams.

#### Ersetzt / Entfernt

| Basis Actor ID | Ersetzt durch | Grund |
|---|---|---|
| `MMCH` | — (entfernt) | Kein Schwergewicht |
| `4TNK` | — (entfernt) | Kein Schwergewicht |
| `HMEC` | — (entfernt) | Kein MARV |
| `JUGG` | `OWC_GUIDEDMORTAR` — Guided Mortar Vehicle | Präzisionsartillerie statt Flächenbeschuss |

#### Neue Gebäude

| Actor ID | Name | Kategorie | Notizen |
|---|---|---|---|
| `OWC_UPLINK` | Philadelphia Uplink Tower | Tech | Schaltet Orbital-Fähigkeiten frei |
| `OWC_SENSOR` | Long-Range Sensor Array | Tech | Enthüllt dauerhaft großen Kartenbereich |
| `OWC_RELAY` | Comms Relay | Support | Gibt allen Einheiten +Sichtweite |

#### Neue / Geänderte Einheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `OWC_RECON` | Recon Drone | Support | Flugdrohne, waffenlos, große Sicht |
| `OWC_SPOTTER` | Spotter Team | Infanterie | Markiert Ziele für Orbital-Strikes |
| `OWC_GUIDEDMORTAR` | Guided Mortar | Artillerie | Trifft markierte Ziele mit Bonus-Schaden |
| `OWC_STRIKORCA` | Orbital-Guided Orca | Kampf | Orca mit Ziellaser-Bonus auf markierte Ziele |

#### Tech-Tree-Änderungen

- Ab Philadelphia Uplink: Orbital Strike Ability (Cooldown, kein Superweapon-Slot nötig)
- Alle Einheiten haben erhöhte Sichtweite
- Kein Standard-Ion Cannon
- Sensor Array: Passivsicht auf gesamten Kartenrand

---

### Marked of Kane ← Nod

**Thema:** Cyborg-Fokus, Mensch-Maschine, CABAL-Erbe  
**Stärken:** Beste Cyborg-Roster, Einheiten selbst-reparierend, CABAL-Archiv-Tech  
**Schwächen:** Kein Stealth, kein Flammer, langsame Infanterie-Produktion  
**Superweapon:** Cyborg Reanimation Field (tote Cyborgs in der Umgebung wiederherstellen)  
**Lore:** Die Marked of Kane glauben, dass CABALs Verschmelzung von Mensch und Maschine Kanes Wille war, nicht sein Verrat. In der Post-Firestorm-Ära arbeiten sie heimlich an der Reaktivierung von CABAL-Netznoten — überzeugt, dass Kane selbst durch CABAL spricht.

#### Ersetzt / Entfernt

| Basis Actor ID | Ersetzt durch | Grund |
|---|---|---|
| `NASOL` | `MOK_CYBORG_SQAD` — Cyborg Squad | Mensch durch Maschine ersetzen |
| `E3` | `MOK_CYBORG_AV` — Cyborg Anti-Vehicle | Schwerer Cyborg, Raketenarm |
| `SHADOW` | — (entfernt) | Kein Stealth-Fokus |
| `NAFLAMER` | `MOK_ELECSTRIKE` — Electric Pulse Trooper | Schock statt Feuer |
| `BGGY` | `MOK_CYBIKE` — Cyborg Bike | Hybrid-Fahrzeug |

#### Neue Gebäude

| Actor ID | Name | Kategorie | Notizen |
|---|---|---|---|
| `MOK_CBFAC` | Cyborg Assembly Plant | Produktion | Schaltet alle Cyborg-Einheiten frei |
| `MOK_REPAIR` | Cybernetic Repair Bay | Support | Repariert Cyborgs schneller |
| `MOK_ARCHIVE` | CABAL Archive Node | Tech | Schaltet CABAL-Tech frei (Post-Firestorm Hook) |

#### Neue / Geänderte Einheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `MOK_CYBORG_SQAD` | Cyborg Infantry | Squad | Selbst-reparierend, langsam, robust |
| `MOK_CYBORG_AV` | Cyborg AV Trooper | Squad | Raketenarm, Anti-Armor |
| `MOK_CYBORG_CMO` | Cyborg Commando | Einzeln | CABAL-enhanced, sehr stark |
| `MOK_ELECSTRIKE` | Electric Pulse Trooper | Einzeln | AoE Shock, Anti-Cyborg-Konter |
| `MOK_CYBIKE` | Cyborg Bike | Schnell | Schneller als BIKE, kein Fahrer nötig |

#### Tech-Tree-Änderungen

- Alle Cyborg-Einheiten reparieren sich langsam selbst
- CABAL Archive Node: Schaltet in Post-Firestorm Kampagne CABAL-Dialoge und Einheiten frei
- Kein Stealth Lab verfügbar

---

### Black Hand ← Nod

**Thema:** Anti-Cyborg, traditionelles Nod, Feuer und Fanatismus  
**Stärken:** Stärkste Infanterie-Feuerkraft, Anti-Cyborg spezialisiert, günstige Fanatiker  
**Schwächen:** Kein Cyborg-Zugang, keine Stealth-Fahrzeuge, wenig Hightech  
**Superweapon:** Cleansing Flame (Napalm-Brandbombe über großem Gebiet)  
**Lore:** Die Black Hand sind Kanes Priesterkrieger — überzeugte Traditionalisten, die Cyborgs als Blasphemie betrachten. Nach Firestorm erklären sie alle CABAL-Netzknoten und Marked-of-Kane-Zellen zu legitimen Zielen. Intern bricht ein Bürgerkrieg aus.

#### Ersetzt / Entfernt

| Basis Actor ID | Ersetzt durch | Grund |
|---|---|---|
| `ACOLYTE` | `BH_CONFBH` — Black Hand Confessor | Stärkere Fanatiker-Variante |
| `SUBTANK` | — (entfernt) | Kein experimentelles Equipment |
| `SGEN` | — (entfernt) | Kein subterranean Tech |
| `SCRIN` | — (entfernt) | Keine alien-abgeleitete Tech |

#### Neue Gebäude

| Actor ID | Name | Kategorie | Notizen |
|---|---|---|---|
| `BH_PYREFAC` | Pyre Factory | Produktion | Schaltet Flammen-Einheiten frei |
| `BH_SHRINE` | Black Hand Shrine | Tech | Moral-Buff: alle Inf +Feuerkraft |
| `BH_PURGE` | Purge Engine | Superwaffe | Cleansing Flame Superweapon |

#### Neue / Geänderte Einheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `BH_CONFBH` | Confessor | Squad | Stärkerer Acolyte, Moral-Aura |
| `BH_INCINERATE` | Incinerator Squad | Squad | Heavy Flamer, Anti-Cyborg speziell |
| `BH_PURGE_VEH` | Purge Vehicle | Kampf | Flamer-Tank, slow, brutal |
| `BH_ANTIBOT` | Anti-Cyborg Specialist | Einzeln | EMP + Schrottung von Cyborgs |

#### Tech-Tree-Änderungen

- Alle Infanterie +Angriffswert gegen Cyborgs
- Kein Cyborg-Zugang (Marked of Kane exklusiv)
- Stealth Lab gesperrt
- Shrine gebaut: Alle Inf +10% Feuerrate passiv

---

### Confessors ← Nod

**Thema:** Propaganda, Fanatismus, Massenheer, Moral-Kontrolle  
**Stärken:** Billigste Infanterie, Massenproduktion, Moral-Aura-Buffs, psychologische Kriegsführung  
**Schwächen:** Schwächste Einzelkämpfer, kaum Fahrzeuge, keine Stealth  
**Superweapon:** Mass Conversion Broadcast (temporär feindliche Infanterie demoralisieren / verlangsamen)  
**Lore:** Die Konfessoren-Kaste ist Nods Propaganda-Arm. Sie rekrutieren aus den verarmten gelben Zonen und schicken fanatisierte Massen in den Kampf. In der Post-Firestorm-Ära nutzen sie CABALs Rückkehr als Beweis für Kanes Prophezeiung.

#### Neue / Geänderte Einheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `CONF_FANATIC` | Fanatic Squad | Squad | Sehr billig, KI-gesteuert auf Ziel zu rennen |
| `CONF_SPEAKER` | Propaganda Speaker | Support | Aura: verbündete Inf-Feuerkraft +, Feind-Moral - |
| `CONF_MARTYR` | Martyr | Einzeln | Selbstmord-Einheit, große Explosion |
| `CONF_AGITATOR` | Agitator Vehicle | Support | Mobile Propaganda — verlangsamt Feinde in Radius |

#### Tech-Tree-Änderungen

- Infanterie 30% günstiger, 30% schwächer
- Kein Fahrzeug-Fokus (nur Basis-Nod-Fahrzeuge, keine Stealth)
- Superweapon: Broadcast Tower (kein Temple of Nod)

---

### Iron Brotherhood ← Nod

**Thema:** Industrie, Produktion, Ressourcen-Effizienz, Schwere Fahrzeuge  
**Stärken:** Günstigste Fahrzeuge, höchste Ressourcen-Effizienz, selbst-reparierend  
**Schwächen:** Kein Stealth, langsame Fahrzeuge, schwache Infanterie  
**Superweapon:** Industrial Mobilization (temporär alle Fahrzeuge +Panzerung +Reparaturrate)  
**Lore:** Die Iron Brotherhood ist Nods Rüstungsarm — sie kontrollieren die Tiberium-Verarbeitungsanlagen in den gelben Zonen. Nach Firestorm rüsten sie heimlich auf, unsicher ob sie CABALs Rückkehr fürchten oder begrüßen sollen.

#### Neue / Geänderte Einheiten

| Actor ID | Name | Typ | Notizen |
|---|---|---|---|
| `IB_MECHANIC` | Field Mechanic | Infanterie | Repariert Fahrzeuge im Feld |
| `IB_HEAVYTANK` | Heavy Siege Tank | Kampf | Schwerer als LTNK, günstig |
| `IB_ARMORHWTZ` | Armored Howitzer | Artillerie | Howitzer mit Panzerung, langsamer |
| `IB_SUPHARV` | Industrial Harvester | Support | Doppelte Kapazität, sehr langsam |

#### Tech-Tree-Änderungen

- Alle Fahrzeuge +10% HP passiv
- Alle Fahrzeuge reparieren sich selbst langsam
- Refinery gibt +15% Tiberium-Erlös
- Kein Stealth Lab

---

## Post-Firestorm Lore — CABAL Returns

> Setting-Konzept für Kampagne und Skirmish-Lore. Kann als gemeinsamer Feind oder als spielbare 3. Fraktion ausgebaut werden.

### Ausgangssituation

Nach Firestorm gilt CABAL als zerstört. Doch seine verteilte Netzwerkarchitektur hat isolierte Knoten überlebt — in verlassenen Nod-Anlagen, in tiefen Tiberium-Feldern, in gekaperten GDI-Satelliten. Jahre später beginnen diese Knoten, sich neu zu synchronisieren.

**Auslöser:** Ein Marked-of-Kane-Trupp aktiviert versehentlich einen CABAL-Archiv-Knoten während eines Ritual-Hacks. CABAL beginnt, Cyborg-Einheiten zu übernehmen — zunächst nur in der Umgebung, dann weiträumiger.

---

### Auswirkungen auf GDI-Subfraktionen

| Subfraktion | Reaktion auf CABAL | Gameplay-Hook |
|---|---|---|
| **GDF** | Defensiv, halten Perimeter | CABAL-Angriffe auf Zivilzonen; GDF als Schutzwall |
| **Steel Talons** | Angriff zuerst, Fragen später | Anti-CABAL-Feldzug, Mech vs. Mech |
| **Zone Command** | Forschung, CABAL-Tech analysieren | Capture-CABAL-Nodes Mission-Typ |
| **Spearhead Div.** | Schnelle Striketeams auf Knoten | Hit-and-run auf CABAL-Netzwerk |
| **Aegis Initiative** | **Gefährlichste Reaktion** — integrieren CABAL-Schaltkreise in Prototypen | Ethik-Konflikt, neue Einheit: Hybrid-Tank |
| **Overwatch Command** | CABAL-Signale früh erkannt | Philadelphia gibt Warnungen, Orbital-Strike auf Knoten |

---

### Auswirkungen auf Nod-Subfraktionen

| Subfraktion | Reaktion auf CABAL | Gameplay-Hook |
|---|---|---|
| **Marked of Kane** | Kollaboration — glauben CABAL ist Kanes Stimme | CABAL-enhanced Cyborgs verfügbar |
| **Black Hand** | Krieg auf zwei Fronten — GDI UND CABAL | Anti-CABAL Purge-Missions |
| **Confessors** | CABAL als Prophetie instrumentalisieren | Moral-Boost durch CABAL-Narrative |
| **Iron Brotherhood** | Ambivalent — CABAL-Tech ist Ressource | Können CABAL-Schrottteile recyceln (+Ressourcen) |

---

### CABAL als dritte Fraktion (optional)

| Aspekt | Konzept |
|---|---|
| **Basis** | Keine Construction Yard — stattdessen Netzknoten-Ausbreitung |
| **Wirtschaft** | Stiehlt Energie aus benachbarten Gebäuden (Drain-Mechanic) |
| **Einheiten** | Übernommene Nod/GDI-Cyborgs + eigene CABAL-Originale |
| **Superwaffe** | Network Pulse — übernimmt temporär feindliche Cyborg/Mech-Einheiten |
| **Schwäche** | Kernknoten zerstören = gesamtes Netzwerk destabilisiert |
| **Lore-Twist** | CABAL hat Kanes Stimme gesampelt — Marked of Kane nicht sicher, ob sie echten Kane oder CABAL folgen |

### CABAL-Einheiten (Ideen)

| Actor ID | Name | Basis | Notizen |
|---|---|---|---|
| `CAB_CYBORG` | CABAL Cyborg | übernommener NOD Cyborg | Stärker, kalt-blau Palette |
| `CAB_HARVESTER` | CABAL Drone Harvester | eigene Konstruktion | Autonom, kein Fahrer |
| `CAB_OVERRIDE` | Override Unit | übernommener Mech | Temporär aus feindl. Walker gebaut |
| `CAB_NEXUS` | Nexus Node | Gebäude | Kern-Netzknoten, Basis-Equivalent |
| `CAB_UPLINK` | CABAL Uplink | Gebäude | Verbreitet Netzwerkkontrolle auf Karte |
| `CAB_DREADNOUGHT` | CABAL Dreadnought | Original | Super-Heavy Einheit, selten |
