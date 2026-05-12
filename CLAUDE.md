# SectorWar — repo notes for Claude Code

This file is loaded into Claude Code's context on every session in this
repo. Keep it short and load-bearing.

## The arena settings law

`docs/ARENA_SETTINGS.md` is the contract with zone admins (phong, etc.) for
guardrailing arena conf values they accept from users. It must not drift
from the code.

**Rule.** Any PR that adds, removes, renames, or changes the type / default
/ range of an arena (or global) conf value SectorWar reads MUST update
`docs/ARENA_SETTINGS.md` in the same PR.

This applies to:
- `_configManager.GetInt/GetStr/GetBool/GetDouble/GetEnum` calls under
  `src/SectorWar/Modules/*.cs` (and anywhere else inside `src/SectorWar/`).
- Any `[ConfigHelp<T>]` attribute additions or edits in those files.

Renaming an internal C# variable but keeping the conf key/section/type/
default/range identical does **not** require a doc update.

When `[ConfigHelp<T>]` attributes and `ARENA_SETTINGS.md` disagree, the
attributes win — fix the doc.

## ConfigHelp attributes

SectorWar uses `[ConfigHelp<T>("Section", "Key", ConfigScope.Arena,
Default = …, Min = …, Max = …, Description = "…")]` (and `ConfigScope.Global`
for the two `BossesEnabled` / `LinkedArenas` keys) on the partial-class
declaration that owns each conf read. The Min/Max values mirror the
guardrails in `docs/ARENA_SETTINGS.md`.

**Min/Max is display-only.** The framework surfaces ConfigHelp metadata to
admin tooling (Quickfix) but does NOT enforce Min/Max at conf-load time —
the consumer is responsible for clamping. If a guardrail must be enforced
at runtime, wrap the read site with `Math.Clamp(...)` or similar.

**Attributes MUST be on a field/property/event — not on the class.** The
framework's scanner (`Help.cs:AddConfigHelpAttributes`) walks
`type.GetRuntimeFields() / GetRuntimeProperties() / GetRuntimeEvents()` and
silently ignores class-level attributes. The convention in SectorWar is to
pin the attribute block to the first private const/static field in the
partial-class file; mirror this when adding new conf reads. Symptom of
getting it wrong: `?man <section>:<key>` shows only the framework's
declaration (no Min/Max line).

Indexed keys (e.g. `AutoBrickBrick{N}`, `Team{N}-X`, `StaticTurretTurret{N}`)
do not get attributes — the framework doesn't support indexed-key
ConfigHelp. They are documented in `ARENA_SETTINGS.md` only.

Per-ship-section keys (`[Warbird] BulletSpeed`, etc.) get one attribute
per ship section. There are eight ship sections (Warbird, Javelin, Spider,
Leviathan, Terrier, Weasel, Lancaster, Shark), plus `[Spectator]` for the
`ShowLvz` key only.

## Static-turret type registration

The `[SectorWar] StaticTurretTurret{N}` indexed list registers turret types,
each pointing at its own `[staticturret_<value>]` section. The convention
phong wants enforced (and that the parser requires):

- **Named keys, not numeric.** `StaticTurretTurret0 = pylon` →
  `[staticturret_pylon]`. Never `StaticTurretTurret0 = 0` →
  `[staticturret_0]`.
- **Values must be unique.** Duplicate values trigger a Warn and the
  second registration is dropped on the floor.
- **Indices must be sequential from 0.** The parser loops 0..99 and breaks
  on the first empty entry, so a gap in numbering silently truncates the
  list.

When adding a new turret type: append to the next free `StaticTurretTurretN`
index, define `[staticturret_<name>]` in the same file, never reuse a name.

## StaticTurretSpawn{N} — auto-spawn format

`[SectorWar] StaticTurretSpawn{N}` auto-spawns a registered turret at arena
StartGame. Format is **comma-separated** (per the parser at
[SectorWar.StaticTurret.cs:817](src/SectorWar/Modules/SectorWar.StaticTurret.cs#L817)):

```
StaticTurretSpawn{N} = <tile-x>, <tile-y>, <freq>, <typeKey>
```

- `tile-x` / `tile-y` are **tile coords (0..1023)**, not pixels — parser
  converts to pixel center via `(tile << 4) + 8`.
- `freq` is in 0..9999.
- `typeKey` must be one of the registered `StaticTurretTurret{N}` names.
- Same sequential-from-0 rule as the type list — first empty entry breaks
  the read loop.

Not pipe-separated, not space-separated. Comma.

## Per-ship floor settings

`ShipSettings` reads only **8 keys** from each `[<Ship>.Floor]` section
(hardcoded in
[SectorWar.ShipSettings.cs:94-104](src/SectorWar/Modules/SectorWar.ShipSettings.cs#L94-L104)):

```
MaximumEnergy, MaximumRecharge, MaximumThrust, MaximumSpeed,
MaximumRotation, BulletSpeed, BombSpeed, Radius
```

Anything else in the `.Floor` section is silently ignored. Adding a new
tracked key requires editing that array AND adding a matching
`ItemModifier` site in `ItemCatalog` so inventory items can drive it.

## HQ subsystem turret-key bindings

`Hq` calls `IStaticTurret.AddBot` with **specific name strings**, defined
as `const` in
[SectorWar.Hq.cs:114-118](src/SectorWar/Modules/SectorWar.Hq.cs#L114-L118):

```
hq_perimeter_gun, hq_command, hq_capital,
hq_capital_gun, hq_capital_bomb
```

These names MUST match `[staticturret_<key>]` section names in
structures.conf exactly. Renaming a turret type means updating BOTH the
conf section AND the C# const. There is no tag/category system — just
literal name lookup.

## Project shape

- One umbrella `IArenaAttachableModule` (`SectorWar`) split across many
  `partial class` files in `src/SectorWar/Modules/SectorWar.<Subsystem>.cs`.
  This is intentional — Nexus only allows one module attach per arena.
- The framework (`SubspaceServer/`) is a sibling clone, not a submodule.
  Refer to `SubspaceServer/src/Core/` for ConfigHelp and IConfigManager
  signatures.

## Common commands

- **Build the plugin DLL** (this clone has SubspaceServer nested rather than
  sibling, so the csproj default doesn't apply — pass the override):

  ```powershell
  dotnet build src/SectorWar/SectorWar.csproj `
    -p:SubspaceServerSrc=$PWD/SubspaceServer/src
  ```

  Or set `$env:SUBSPACESERVER_SRC` once per shell. The csproj falls back to
  `..\..\..\SubspaceServer\src` (sibling) when neither is set, which fails
  here with a wave of `CS0234: type or namespace 'Core' does not exist` —
  that's the layout mismatch, not an actual code error.

- Run the test server (Zone is the working directory):
  `dotnet run --project SubspaceServer/src/SubspaceServer/SubspaceServer.csproj`
