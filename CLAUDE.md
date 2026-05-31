# Crystallized Nexus

A reimagining of the *Crystallized Doom* mod, built on **OpenRA** (Tiberian Sun base). C# engine traits + YAML rules. Factions: GDI and Nod.

## Repository layout

This repo contains **two separate engine trees** plus the mod itself:

- `.modsdk/` — the **OpenRA Mod SDK**: where the mod is built and run from.
  - `.modsdk/OpenRA.Mods.CN/` — the mod's **custom C# traits** (`OpenRA.Mods.CN.dll`). This is the main code you edit. Subfolders: `Traits/` (World, Render, Movement, Air, Player, MobSpawner, **BotModules**), etc.
  - `.modsdk/mods/cn/` — the **mod data**: YAML `rules/`, `weapons/`, `sequences/`, `audio/`, `fluent/` (localization), plus art under `bits/`, `maps/`, `tilesets/`.
  - `.modsdk/engine/` — the **built/run engine**, fetched as a read-only zip (`AUTOMATIC_ENGINE_MANAGEMENT=True`, version pinned in `.modsdk/mod.config` → `ENGINE_VERSION`). Do **not** hand-edit; it gets overwritten on fetch.
- `engine/` — the **git source** of the custom CN engine fork (`OpenRA.Game`, `OpenRA.Mods.Common`, etc.). This is where engine-level patches are authored.

⚠️ `engine/` (source) and `.modsdk/engine` (built artifact from a release zip) **can be out of sync** — they are not the same checkout. Don't blindly copy files between them. To pick up new engine source changes in the running game, bump `ENGINE_VERSION` and re-fetch.

## Engine fork strategy

The CN engine is a **custom fork**, not stock OpenRA. Pull OpenRA `bleed` PRs **selectively** (cherry-pick); never blindly sync upstream. Known engine patches live in `FEATURES.txt` under "ENGINE PATCHES" (slope-speed system, OpenGL depth/alpha fix, multi-cell mobile, TSMapGenerator extensions).

## Build & run

All commands run from inside `.modsdk/`:

```powershell
.\make.cmd all      # build OpenRA.Mods.CN (dotnet build -c Release, win-x64)
.\make.cmd clean    # clean build artifacts
.\make.cmd check    # StyleCop / code checks
.\make.cmd test     # run tests
.\launch-game.cmd   # launch the mod (fetches engine if missing)
```

`make all` requires `.sln` present and fetches the engine on first run. After editing any C# in `OpenRA.Mods.CN`, rebuild before launching. Pure YAML/art changes don't need a rebuild.

## Coordinate systems (common pitfall)

OpenRA TS maps use a **RectangularIsometric** grid. `CPos` (cell x,y), `MPos`/`PPos` (projected u,v), and `WPos` (world) are **different** coordinate spaces. `Map.Bounds` is in **projected** (u,v) space — do **not** construct a `CPos` directly from it. To pick a random valid cell use `Map.ChooseRandomCell(rand)` (it unprojects correctly); see `CloudSpawner.cs` for the canonical usage.

## Conventions

- Custom trait classes are prefixed `CN…` (e.g. `CNHarvester`, `CNHealth`, `CNSquadManagerBotModule`).
- Match the surrounding code's style; mod files carry the CN copyright header.
- `FEATURES.txt` is the up-to-date catalog of all custom features and engine patches — consult it to find where a mechanic lives.

## Notes

- The user often communicates in German; respond in kind when they do.
- Bot behavior intent: bots should use all unit templates continuously (only a light demand bias, weighted-random + `TemplateSelectionSharpness`) and must **not** sell buildings prematurely (only once the base is fully established).
