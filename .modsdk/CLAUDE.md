# CLAUDE.md — Crystallized Nexus (CN)

## Project Overview

Crystallized Nexus is a Tiberian Sun-era real-time strategy mod built on the **OpenRA engine** (custom CN fork). It extends Tiberian Sun with new units, weather systems, voxel dynamics, mob/squad mechanics, and a full custom asset pipeline.

- **Repository**: `D:\GitHub\crystallized-nexus`
- **Namespace**: `OpenRA.Mods.CN`
- **Mod ID**: `cn`
- **YAML root**: `.modsdk/mods/cn/`
- **Engine root**: `../engine` (relative to the `.csproj`)
- **Assemblies**: `OpenRA.Mods.Common.dll`, `OpenRA.Mods.Cnc.dll`, `OpenRA.Mods.CN.dll`
- **Map grid**: RectangularIsometric, max terrain height 16, depth buffer enabled

## Repository Structure

```
crystallized-nexus/
├── OpenRA_Mods_CN.csproj          # C# mod project, references engine at ../engine
├── Traits/                         # C# trait source files
│   ├── CNWithVoxelBody.cs          # Drop-in WithVoxelBody + VoxelDynamics tilt
│   ├── CNWithVoxelTurret.cs        # Drop-in WithVoxelTurret + VoxelDynamics tilt
│   ├── CNWithVoxelBarrel.cs        # Drop-in WithVoxelBarrel + VoxelDynamics tilt
│   ├── CNWithVoxelWalkerBody.cs    # Drop-in WithVoxelWalkerBody + VoxelDynamics tilt + Offset
│   ├── CNWithVoxelUnloadBody.cs    # Drop-in WithVoxelUnloadBody + VoxelDynamics tilt
│   ├── VoxelDynamics.cs            # Impact-tilt, acceleration-tilt, recoil for voxel units
│   ├── WeatherController.cs        # Ion storm system (tint, radar jam, conditions, music)
│   ├── IonStormDamage.cs           # Lightning strikes during ion storms
│   ├── CloudSpawner.cs             # Cloud shadows & godray sprites (from OpenHV)
│   ├── Cloud.cs                    # Cloud effect rendering
│   ├── MobSpawnerMaster.cs         # Mob/squad system (C&C Generals-style aggregate HP)
│   ├── MobSpawnerSlave.cs          # Slave actor for mob system
│   ├── BaseSpawnerMaster.cs        # Base class for spawner mechanics
│   ├── BaseSpawnerSlave.cs         # Base class for slave actors
│   ├── ResourceAnimationOverlay.cs # Tiberium sparkle / vein idle animations
│   ├── TerrainAnimationOverlay.cs  # Water waves, terrain effects
│   ├── FormationMove.cs            # Grid formation movement for unit groups
│   ├── Scatterer.cs                # Auto-dodge for units vs approaching crushers
│   ├── FadeOut.cs                  # Gradual alpha fade then dispose
│   ├── BotModules/
│   │   ├── CNBaseBuilderBotModule.cs   # AI base construction with layout strategies
│   │   ├── CNBaseBuilderQueueManager.cs# AI build queue decision logic
│   │   └── DeployBotModule.cs      # AI deploy behavior
│   ├── PlacesPavement.cs           # Pavement placement trait
│   ├── AlphaGradientPalette.cs     # Gradient palette for additive effects
│   └── MobSquadSelectionDecoration.cs
├── rules/
│   ├── features/                   # Shared gameplay features
│   │   ├── armortypes.yaml
│   │   ├── autotarget.yaml
│   │   ├── baseactors.yaml         # ^VoxelActor, ^VoxelVehicle, ^VoxelTank, etc.
│   │   ├── buildings.yaml
│   │   ├── crates.yaml
│   │   ├── deathtypes.yaml
│   │   ├── decorations.yaml
│   │   ├── experience.yaml
│   │   ├── terrain.yaml
│   │   ├── shapes.yaml
│   │   ├── unittypes.yaml
│   │   ├── traits.yaml
│   │   └── handycap.yaml
│   ├── world.yaml                  # World actor: weather, clouds, overlays, locomotors
│   ├── player.yaml
│   ├── ai.yaml
│   ├── gdi-*.yaml / nod-*.yaml     # Faction-specific units, structures, tech
│   ├── shared-*.yaml               # Shared between factions
│   ├── civilian*.yaml
│   └── voxel-dynamics.yaml         # VoxelDynamics YAML configs per unit
├── weapons/
│   ├── ballisticweapons.yaml
│   ├── energyweapons.yaml
│   ├── explosions.yaml
│   ├── missiles.yaml
│   ├── smallguns.yaml
│   ├── superweapons.yaml
│   ├── healweapons.yaml
│   └── otherweapons.yaml
├── sequences/
│   ├── structures.yaml, vehicles.yaml, infantry.yaml, aircraft.yaml
│   ├── voxels.yaml (ModelSequences)
│   ├── misc.yaml, projectiles.yaml, trees.yaml, bridges.yaml
│   └── critters.yaml, civilian.yaml
├── tilesets/
│   ├── temperate.yaml
│   └── snow.yaml
├── bits/                           # Custom art assets
│   ├── art/{gdi,nod}/{aircraft,infantry,structures,vehicles}
│   ├── anims/{explosions,muzzle,particles,trails,weapons}
│   ├── palletes/
│   ├── cameos/
│   └── terrain/
├── audio/data/                     # Custom audio
│   ├── eva/, explosions/, themes/, voice/, weapons/
├── maps/                           # Bundled maps
└── mod.yaml                        # Master mod definition
```

## Build & Run

```bash
# From crystallized-nexus directory
dotnet build OpenRA_Mods_CN.csproj

# Run the mod via OpenRA engine
cd ../engine
dotnet run --project OpenRA.Launcher -- Game.Mod=cn
```

The `.csproj` references `../engine` for `OpenRA.Game`, `OpenRA.Mods.Common`, and `OpenRA.Mods.Cnc`.

## Custom Traits (C#)

### Voxel Rendering (`OpenRA.Mods.CN.Traits`)

All CN voxel traits are drop-in replacements for their vanilla counterparts, adding optional `VoxelDynamics` pitch/roll tilt:

| CN Trait | Replaces | Notes |
|---|---|---|
| `CNWithVoxelBody` | `WithVoxelBody` | Applies VoxelDynamics tilt to body orientation |
| `CNWithVoxelTurret` | `WithVoxelTurret` | Tilt-aware turret rendering |
| `CNWithVoxelBarrel` | `WithVoxelBarrel` | Tilt-aware barrel with recoil |
| `CNWithVoxelWalkerBody` | `WithVoxelWalkerBody` | Walker legs + tilt + configurable `Offset` for multi-cell footprints |
| `CNWithVoxelUnloadBody` | `WithVoxelUnloadBody` | Idle/unload sequences + tilt |

**`VoxelDynamics`** provides spring-based tilt from:
- Impact damage (configurable impulse, hold ticks)
- Firing recoil (pitch + roll)
- Acceleration/deceleration (optional pitch scale)
- Turning (optional roll scale)
- Constant offsets (PitchOffset, RollOffset)

### Weather System

- **`WeatherController`** — Ion storm lifecycle (Clear/Warning/Storm/Clearing). Grants `ionstorm` condition to all actors with a matching `ExternalCondition`, plays storm music. Exposes `State`, `Intensity`, `IsIonStormActive` for other traits.
- **`WeatherTintEffect`** — Lerps `TintPostProcessEffect` tint values based on `WeatherController.Intensity`. Configured separately in YAML so tint colors are independent of the storm controller.
- **`AnnounceOnCondition`** — General-purpose trait: plays faction-specific speech and/or text notification when a condition is granted (`EnabledNotification`) or revoked (`DisabledNotification`). Usable on World or Player actors. Used for ion storm warnings via `RequiresCondition: ionstorm`.
- **`IonStormDamage`** — Lightning strikes during storms using weapon warheads. Configurable intensity threshold, actor targeting chance.

### Mob/Squad System

- **`BaseSpawnerMaster`/`BaseSpawnerSlave`** — Base spawner framework (from OP Mod).
- **`MobSpawnerMaster`** — C&C Generals-style aggregate health. Master can be invisible nexus or visible squad leader (`IncludeMasterInAggregate`). Damage redirect from master to slaves via undo-trick (`-damageValue` on master, positive on slave). Supports `ProtectedBySlavesCondition`.
- **`MobSpawnerSlave`** — Slave actor that follows master orders.

### World Overlays

- **`ResourceAnimationOverlay`** — Sparkle/poison effects on Tiberium cells. Supports primary + additional image with chance-based selection.
- **`TerrainAnimationOverlay`** — Effects on terrain types (water waves, etc). Same primary/additional pattern.
- **`CloudSpawner`** / **`Cloud`** — Cloud shadows and godrays. Pre-spawns to cover map. Configurable blend mode, Z-offset, speed, wind direction.

### Gameplay Traits

- **`FormationMove`** — Rectangular grid formation for multi-unit move orders.
- **`Scatterer`** — Auto-dodge perpendicular to approaching crusher units.
- **`FadeOut`** — Alpha fade over time then dispose (for decals, corpses).
- **`PlacesPavement`** — Lays pavement under structures.
- **`AlphaGradientPalette`** — Runtime gradient palette for additive effects.

### AI Bot Modules

- **`CNBaseBuilderBotModule`** — AI base construction with layout strategies (Random, Grid, Clustered, Compact), building fractions, delays, limits, wall placement.
- **`CNBaseBuilderQueueManager`** — Build queue decision-making: power priority, refineries, naval production, silos, defense.
- **`DeployBotModule`** — AI deploy behavior for deployable units.

## Critical Engine Knowledge

### API Differences from Mainline OpenRA

This engine fork is **missing** several standard OpenRA utility methods. Always verify against the actual engine source before using:

- ❌ `Util.ApplyPercentageModifiers` — not available
- ❌ `world.FindActorsOnLine` — not available
- ❌ `Util.GetVerticalAngle` — not available
- ❌ `INotifyConditionChanged` — not available
- ❌ `ConditionManagerInfo` — not available

### Voxel/Condition Timing

- `CNWithVoxelBody` disables rendering as soon as `submerged` condition is granted. Any tilt animations must use a **deferred condition** (e.g., `fully-submerged`) granted only after the animation completes.

### DayNightCycle / TerrainLighting Conflict

- `TintPostProcessEffect` implements `ITerrainLighting` in this engine version, causing **duplicate trait errors** if `TerrainLighting` is also present.
- Workaround: Reflection-based write to `TerrainLighting`'s private `globalTint` field.

### MobSpawner Damage Redirect

- `IDamageModifier` returning 0% causes `e.Damage.Value` to be 0 in `INotifyDamage`.
- Fix: Remove `IDamageModifier`, use undo-trick — inflict `-damageValue` on master, redirect positive damage to slave.

### OpenGL Depth/Alpha Fix (Additive Blending)

Additive sprites punching through units requires engine patches:
- `glBlendFuncSeparate` with `GL_ZERO, GL_ONE` for alpha channel
- `glDepthMask(false)` for additive/subtractive blend modes
- Patched in `OpenGL.cs` and `Sdl2GraphicsContext.cs`

## YAML Conventions

### Warhead Syntax
```yaml
Warhead@1Dam: SpreadDamage    # NOT SpreadDamageWarhead
Warhead@2Eff: CreateEffect    # NOT CreateEffectWarhead
```

### Armament Binding
`Armaments:` must be explicitly listed on attack traits or weapons may be silently excluded.

### Deploy Sounds
```yaml
DeploySounds: deploy.aud      # PLURAL form
UndeploySounds: undeploy.aud  # PLURAL form
```

### Contrail Parameters
```yaml
ContrailStartColor:           # NOT ContrailColor
ContrailStartWidth:           # Correct naming
ContrailEndWidth:
```

### Sequence Definitions
- `idle` is a **required fallback** sequence name for `WithSpriteBody`.
- PNG-based actor sequence definitions must be **top-level image blocks**.
- `Length: *` works better than explicit frame counts for PNG sequences after `--png-sheet-import`.
- SmudgeLayer picks randomly from **named sub-sequences** (e.g., `nb1`–`nb4`), not frame indices.

### Multiple Trait Instances
Use `@` suffix notation extensively:
```yaml
ExternalCondition@IONSTORM:
    Condition: ionstorm
CloudSpawner@Clouds:
CloudSpawner@Godrays:
```

### Facing Conventions
- Facing `0` = **SW**
- `InitialFacing: 512` = **NE**

### Base Actor Hierarchy
```
^VoxelActor          — generic voxel actor (CNWithVoxelBody, RenderVoxels)
^VoxelVehicle        — voxel vehicle with cdvoxels palette, RA2normals
^VoxelTank           — voxel tank (inherits ^Tank)
^VoxelWalker         — voxel walker (inherits ^VoxelTank with walker speeds)
^IonStormVulnerable  — ExternalCondition + RevealsShroudMultiplier for ionstorm
^IonStormBuilding    — same for buildings (75% shroud vs 50%)
```

### Palettes
- `unittem.pal` — unit sprites
- `cdvoxels` — voxel player palette
- `RA2normals` — voxel normals palette
- `anim.pal` (`effect`) — effect/explosion sprites
- `terrain` — terrain sprites
- `sparkle` — tiberium sparkle palette
- `apolcyan` — specific unit palette

## Asset Pipeline

### PNG Spritesheet Format
- Embed `FrameSize` and `FrameAmount` as **PNG tEXt chunks** directly in output files (not separate YAML).
- Tools: Python + Pillow for processing.

### Additive Blending Assets (explosions, muzzle, godrays)
- Alpha = raw luminance from RGB
- RGB boost: ×1.8
- Saturation boost: ×1.5
- **No** gamma, no minimum — these crush contrast on additive sprites
- Color-sensitive assets (orange fire): minimal flat boost (×1.2) to avoid hue shift toward yellow

### Normal Alpha Assets (smoke, blood, decals)
- Gamma + multiplier + minimum treatment on alpha channel

### Model Formats
- **SHP**: For gameplay-critical units with many instances/facings
- **PNG**: Preferred for alpha quality on new effects
- **VXL**: Edited with VXLSE III (Voxel Section Editor III)

## Multi-Cell Footprint Units

Implemented by **patching `Mobile.cs` directly** (mirroring `Building.cs` pattern). Multi-cell mobile units bypass HPA* pathfinding in favor of local A*.

## Map Conversion

Maps converted from `.mpr` to `.oramap` using:
- `ImportCNMapCommand` (engine command)
- `ConvertMaps.ps1` (PowerShell batch script)

## Development Environment

- **IDE**: VS Code + Continue v1.2.22 (provider: lmstudio)
- **Local LLM**: LM Studio 0.3.8 with Qwen3 Coder 30B
- **Hardware**: AMD 9800X3D, RTX 5070 Ti, 32 GB RAM
- **SHP Tool**: SHP Builder with `unittem.pal` / `cdvoxels.pal`
- **Voxel Tool**: VXLSE III

## Known TODOs

- Restore `TiberiumSparkleOverlay` to `world.yaml` if missing after weather system changes.
- Potential revisit of `SubterraneanBurrowTilt` animation feature (was designed but dropped).
- Ongoing `.mpr` → `.oramap` map conversion.
