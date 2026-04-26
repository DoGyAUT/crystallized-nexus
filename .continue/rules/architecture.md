# Project Architecture

This is the Crystallized Nexus (CN) OpenRA mod. A Tiberian Sun era real-time strategy game mod.

## Primary Working Directories

- **C# traits/projectiles**: `OpenRA.Mods.CN/Traits/` and `OpenRA.Mods.CN/Projectiles/`
- **YAML rules/weapons/sequences**: `.modsdk/mods/cn/`
- **Engine reference (read-only)**: `engine/` and `.modsdk/engine/` — only look here for API signatures, interfaces, or base class implementations

## File Discovery

When unsure which YAML files exist or where they are, read `.modsdk/mods/cn/mod.yaml` first — it lists all registered Rules, Weapons, Sequences, and Chrome files with their exact paths.

## Project Structure

- YAML actor definitions: `.modsdk/mods/cn/rules/`
- Weapon definitions: `.modsdk/mods/cn/weapons/`
- Sequence definitions: `.modsdk/mods/cn/sequences/`
- Map files: `.modsdk/mods/cn/maps/`
- Chrome/UI: `.modsdk/mods/cn/chrome/` and `.modsdk/mods/cn/metrics/`

## Scope Rules

- ALL file creation, editing, and analysis should focus on `OpenRA.Mods.CN/` and `.modsdk/mods/cn/`.
- Never modify or suggest restructuring engine files.
- Never browse `engine/OpenRA.Mods.Common/`, `engine/OpenRA.Mods.D2k/`, or `engine/OpenRA.Game/` unless you need to check how a specific interface, trait, or method works.
