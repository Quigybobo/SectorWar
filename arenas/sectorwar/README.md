# SectorWar — reference arena configuration

These four `.conf` files are the canonical example arena configuration for
the SectorWar umbrella plugin. They mirror the live test zone exactly.

To use: copy this directory into your zone tree at
`<zone>/arenas/sectorwar/` (or rename it to whatever arena name you want;
the contents are arena-name-agnostic). Then add the SectorWar module DLL
to the zone's `bin/modules/SectorWar/` folder, and the arena will pick up
the umbrella when a player enters.

## File layout

| File | What it owns |
|---|---|
| [arena.conf](arena.conf) | Top-level entry point. Pulls in the other three via `#include`. Defines `[General]`, `[Modules]`, `[Misc]`, `[SectorWar]`, `[Spawn]`, `[Team]`, `[Brick]`. |
| [settings.conf](settings.conf) | Stock Continuum ship + weapon settings (`[Warbird]`, `[Bullet]`, `[Spawn]`, etc.). Read by SS.NET's `ClientSettings` module and shipped to the client in the settings packet. Originally a Devastation dump; tune to taste. |
| [floor.conf](floor.conf) | SectorWar's per-ship FLOOR values for the inventory upgrade model. Sections are `[Warbird.Floor]`, `[Javelin.Floor]`, etc. **Not** Continuum settings — the `ShipSettings` partial reads these as the per-player baseline that inventory items modify. |
| [structures.conf](structures.conf) | Static-turret type registry. `[SectorWar] StaticTurretTurret{N}` numbered list pointing at `[staticturret_<name>]` per-type sections. **Required** — without it, every `AddBot()` call returns `UnknownType` and HQ / pylon / warstation deployment fails silently. |

## Three pitfalls worth highlighting

1. **`[Spawn] Team0-X` is hyphenated.** SS.NET's `ClientSettings` reads
   `Team0-X` (with hyphen) for the client-side spawn picker. The legacy
   Subgame `Team0X` form (no hyphen) is **not** translated by
   `SubgameCompatibility`, so using it leaves spawn coords at `0,0` and
   players spawn at the map origin regardless of freq.

2. **`StaticTurretTurret{N}` registration must be sequential from 0.** The
   parser loops `0..99` and breaks on the first empty entry. A gap
   silently truncates the rest of the list. Values must also be unique
   per the parser's de-dup check.

3. **One module attach line covers all 28 subsystems.** The `[Modules]
   AttachModules` block lists `SS.SectorWar.Modules.SectorWar` once — the
   umbrella registers every subsystem (HQ, RoundManager, Tutorial, GunTurret,
   Pylon, Inventory, Market, …) internally via partial classes.

## Conf surface contract

The complete list of every `[SectorWar]` key the plugin reads, with type /
default / range, lives in [docs/ARENA_SETTINGS.md](../../docs/ARENA_SETTINGS.md).
That file is the authoritative contract for guardrailing values that admin
tooling (`?man`, `?quickfix`) exposes to users.
