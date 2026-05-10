# SectorWar — SubspaceServer.NET plugin for Nexus

A consolidated zone plugin for [SubspaceServer.NET](https://github.com/gigamon-dev/SubspaceServer)
that provides the **Sector War** game mode. RPG progression, market/economy,
deployable structures (pylons, outposts, war stations), turret AI, sector
claim, capture-the-flag, modular capital ship visuals, server-side bullet
collision, prestige, and more — wired through ONE module that attaches per
arena.

This plugin is what Nexus runs to host Sector War.

It is a **Cres-style standalone plugin** — it builds against gigamon's
SubspaceServer source via `ProjectReference` (with `Private=False` +
`ExcludeAssets=all`), producing a single drop-in `SS.SectorWar.dll`. No fork
of the host server is required.

---

## What is in the box

One assembly: `SS.SectorWar.dll`.

That assembly contains:

- **One umbrella module** — `SS.SectorWar.Modules.SectorWar`. A
  `partial class` aggregating 28 gameplay subsystems (Pylon, Damage,
  StaticTurret, Inventory, Market, Rpg, SectorClaim, CTF, ModularShip,
  CompositeHitbox, ...). Reads ONE `[SectorWar]` section in arena.conf,
  registers ONE entry in Modules.config, attaches per-arena via
  `IArenaAttachableModule`. This is what you attach in Nexus.

Everything ships in one DLL. The plugin is single-arena by design — pick
which arena it attaches to and SectorWar runs there.

---

## Build

The csproj uses `ProjectReference` against gigamon's SubspaceServer source.
Point it at your local clone via the `SubspaceServerSrc` MSBuild property
(or the `SUBSPACESERVER_SRC` environment variable). The default assumes
`SectorWar/` and `SubspaceServer/` are siblings under the same parent
directory — adjust if your layout differs.

```pwsh
# Default — looks for a sibling SubspaceServer\src clone
# (../SubspaceServer/src relative to this repo's root)
dotnet build src/SectorWar/SectorWar.csproj -c Release

# Or point at a specific SubspaceServer clone:
dotnet build src/SectorWar/SectorWar.csproj -c Release `
  -p:SubspaceServerSrc=c:\path\to\SubspaceServer\src

# Or via env var:
$env:SUBSPACESERVER_SRC = "c:\path\to\SubspaceServer\src"
dotnet build src/SectorWar/SectorWar.csproj -c Release
```

Output:

```
src/SectorWar/bin/Release/SectorWar/
  SS.SectorWar.dll
  SS.SectorWar.deps.json
  SS.SectorWar.pdb
  SS.SectorWar.runtimeconfig.json
```

To deploy the artefacts directly into a running zone's modules folder
after a successful build, set `DeployTo`:

```pwsh
dotnet build src/SectorWar/SectorWar.csproj -c Release `
  -p:DeployTo=c:\path\to\Zone\bin\modules\SectorWar
```

`DeployTo` is preferred over pointing `OutDir` at a live zone — `OutDir`
locks the plugin DLL while the zone is running.

---

## Install

1. Copy `bin/Release/SectorWar/SS.SectorWar.dll` (and its `.deps.json` /
   `.runtimeconfig.json`) into your zone's `bin/modules/SectorWar/` folder.

2. Register the module in `conf/Modules.config`:

   ```xml
   <module type="SS.SectorWar.Modules.SectorWar"
           path="bin/modules/SectorWar/SS.SectorWar.dll" />
   ```

3. Attach the module to whichever arena should run Sector War
   (`arenas/<arena>/arena.conf`):

   ```ini
   [Modules]
   AttachModules = \
       SS.Core.Modules.Scoring.KillPoints \
       SS.Core.Modules.Enforcers.ShipChange \
       SS.SectorWar.Modules.SectorWar
   ```

4. Add the `[SectorWar]` conf section in the same arena.conf — see
   [arenas/sectorwar/](arenas/sectorwar/) for a working reference set
   (arena.conf + settings.conf + structures.conf + floor.conf) and
   [docs/ARENA_SETTINGS.md](docs/ARENA_SETTINGS.md) for the complete
   `[SectorWar]` key reference.

5. Make sure capability entries exist in `conf/groupdef.dir/default` and
   `conf/groupdef.dir/sysop`. SectorWar registers the commands listed
   below; without a `cmd_<name>` line in the relevant group file, SS.NET
   silently denies the command with no log output.

6. Restart the zone or `?recyclezone`.

---

## Commands

Cap-file group is shown in parentheses (`default` = any player,
`sysop` = sysop-only).

### Player commands (default)

| Command | Subsystem |
|---|---|
| `?sectorwar` | RPG — show your level / xp / credits |
| `?level`, `?xp`, `?prestige` | RPG progression |
| `?bal`, `?balance`, `?pay`, `?baltop`, `?top` | RPG economy |
| `?shipinfo`, `?floor` | Ship settings |
| `?market`, `?invest`, `?divest`, `?portfolio` | Market |
| `?dice`, `?jackpot` | Money sinks |
| `?shop`, `?shopbuy`, `?shopsell`, `?inv`, `?inventory`, `?equip`, `?unequip`, `?menu` | Inventory |
| `?listturrets`, `?clearturrets` | Gun turret |
| `?motd` | Motd |
| `?buypylon`, `?buyoutpost`, `?buywarstation`, `?deployshop` | Deployable shop |
| `?claim` | Sector claim |

### Sysop commands

| Command | Subsystem |
|---|---|
| `?setmotd`, `?addmotd` | Motd |
| `?claimall`, `?sectorstatus` | Sector claim / sector war state |
| `?settest`, `?setshow`, `?setreset`, `?lvztest`, `?damtest`, `?damclear`, `?setship` | Dev commands |
| `?capitalon`, `?capitaloff`, `?capitaltest`, `?capitalstatus`, `?capitalclear` | Hull visuals + composite hitbox |
| `?modulebuild`, `?moduleclear` | Modular ship |
| `?deploypylon`, `?despawnpylons`, `?listpylons`, `?upgradepylon`, `?wipearena` | Pylon |
| `?deploystructure`, `?despawnstructures`, `?liststructures`, `?upgradestructure` | Station deployer |
| `?resetturrets`, `?addturret` | Gun turret |
| `?give` | RPG admin |

---

## Architecture summary

The umbrella is a single `partial class SectorWar : IAsyncModule,
IArenaAttachableModule, ...` whose code is split across 28 partial-class
files in `src/SectorWar/Modules/SectorWar.<Topic>.cs`. At runtime SS.NET
sees ONE type, ONE Modules.config entry, ONE arena.conf section.

| Subsystem cluster | Purpose |
|---|---|
| RPG | XP / levels / credits / prestige (`?level`, `?bal`, `?prestige`) |
| Market | Five GBM-simulated tickers, `?invest` / `?divest` / `?portfolio` |
| MoneySinks | Wealth-tax timer + `?dice` gambling, fund jackpot |
| Inventory | Item catalog + per-ship equipment slots + dialogs (`?menu`) |
| ShipSettings | Per-ship per-player floor / cap framework |
| Damage | Server-side bullet+bomb collision against registered fakes |
| StaticTurret | Stationary AI gun bots, ship-class registry |
| GunTurret | Player-attached fake-player turrets that fire when anchor fires |
| CompositeHitbox | Modular capital ship: invisible turret-fakes + shared HP |
| ModularShip | Big LVZ overlay tracking the real player at 100Hz |
| Pylon | Power source + claim point; `IPylon.IsPowered` gates structures |
| PowerGrid | Subscribes structures to pylon-power state changes |
| StationDeployer | `outpost` / `warstation` composite structures |
| DeployableShop | `?buypylon` / `?buyoutpost` / `?buywarstation` purchase flow |
| SectorClaim | Per-arena per-freq claim tracker, raises `ArenaOwnerChanged` |
| SectorClaimVisual | Mini-map LVZ tiles per linked arena |
| BossEncounter | One boss-fake per sector arena, kill opens the gate |
| CTF | N-team capture-the-flag on top of SS.NET CarryFlags |
| Motd | Sysop-set message-of-the-day on first arena entry |
| Promotion | Kill-streak crown reward |
| PerShipLvz | Show per-ship LVZ object on ship change |
| HullVisuals | Per-player Warbird-Capital LVZ overlay |
| AutoBrick | Periodically drops fixed brick walls |
| FreqChangeWarp | Warp prize on freq change |
| WarpIn | Reusable LVZ flash for "thing materializes" |
| State | Per-arena gate-state / lane tracker |
| ArenaDefenses | Auto-spawns hostile turrets in sector arenas |
| DevCommands | Sysop debug toolkit (`?settest`, `?damtest`, ...) |

The umbrella publishes 17 broker interfaces that other modules can
consume: `IPylon`, `IPowerGrid`, `IStationDeployer`, `IDeployableShop`,
`ISectorClaim`, `ISectorWar`, `IDamage`, `IStaticTurret`, `IGunTurret`,
`ICompositeHitbox`, `IInventory`, `IMarketReader`, `IMoneySinks`, `IRpg`,
`IEconomy`, `IShipSettings`, `IWarpInEffect`. See
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for load order and lifecycle.

---

## Status

Phase 1 (consolidation) complete. Phase 2 (local validation) in progress.
This repo is the extracted shippable artefact of the Sector War plugin,
ready to drop onto Nexus.

### Next steps

- Validate per-arena attach/detach against a live Nexus test arena.
- Migrate any zones still on the legacy multi-section conf layout to the
  single `[SectorWar]` umbrella section
  (see [docs/SECTORWAR_CONF.md](docs/SECTORWAR_CONF.md)).
- Tighten capability sets so player commands ship in `default` and admin
  commands stay in `sysop`.

---

## License

MIT — see [LICENSE](LICENSE).
