# Development Patterns & Rules

## Critical Rules

1. Never guess API signatures. If unsure whether a method exists, say so. Check `engine/` for reference if needed. The custom fork differs from upstream OpenRA.
2. Prefer engine patches over mod-side workarounds when the user has source access.
3. YAML precision matters. Indentation is tabs. Trait names are case-sensitive. `@` suffixes create multiple trait instances.
4. Minimal responses. Give code/YAML directly. Skip lengthy preambles.
5. No hallucinated traits. Only reference traits you are certain exist. If a trait might not exist, flag it.
6. Respond in German if the user writes in German, English if in English.

## Verified Technical Patterns

- **Weapon-reference pattern**: Reference a weapon definition (e.g. `Weapon: IonStormStrike`) instead of hardcoding damage/effects in C#.
- **ExternalCondition + GrantCondition + world.ActorAdded**: Distribute conditions to all actors including newly spawned ones.
- **Zoom-fixed overlays**: Normalized 0..1 coordinates, map to `TopLeft + ViewportSize` at render time.
- **AttackTurreted armament selection**: Always set `Armaments: primary, secondary, rockets` explicitly.
- **ClassicProductionQueue**: Shared queues go on the Player actor, not per-building.
- **MobSpawner damage redirect**: Use the undo-trick in `INotifyDamage` (don't return 0% in IDamageModifier).
- **Multi-cell footprint units**: `Mobile.cs` patch mirroring `Building.cs` YAML syntax. Direct local A* used.
- **-SelectionDecorations**: Must be placed directly on each concrete actor after `Inherits`.
- **PathCostForInvalidPath vs MovementCostForUnreachableCell**: Use `PathCostForInvalidPath` in `customCost` lambdas.
- **Lazy weapon resolution**: Resolve weapons in `Tick()`, not `IRulesetLoaded`.

## Active Systems

- Dynamic weather / ion storm: `WeatherController`, `IonStormDamage`, `CNWeatherOverlay`, `CNIngameRadarDisplayLogic`
- Sonic Disruptor: `SonicBeam.cs` projectile, `WithMuzzleOverlay` + `Combine` block
- Multi-cell footprint: Patched `Mobile.cs`, `CNWithVoxelWalkerBody` with `Offset` + `VoxelDynamics`
- MobSpawner squad system: `MobSpawnerMaster/Slave` traits, `^MobSquadMaster`/`^MobSquadMember` templates
- Production/Tech queue: `ClassicProductionQueue` on Player actor, Tech tab in `ingame-player.yaml`

## Pending / Known Issues

- Day/night cycle: `ITerrainLighting` conflict — Lua-based approach planned
- `CloudShadowOverlay` visibility issue — deprioritized
- JUGG sprite offset misalignment — canvas resize pending
- VoxelDynamics roll bug on Ost/West axis — accepted

## Asset Pipeline

- Voxels: VXLSE III — TS Normals (Type 2, 244 vectors)
- Sprites: SHP Builder with `unittem.pal`, `anim.pal`
- Palettes: `unittem.pal`, `cdvoxels.pal`, `apolcyan`, `PaletteFromPaletteWithAlpha`
- Map conversion: PowerShell `ConvertMaps.ps1` with `-ExecutionPolicy Bypass`
- Image processing: Python + Pillow/OpenCV
