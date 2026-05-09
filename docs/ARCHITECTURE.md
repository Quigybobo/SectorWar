# SectorWar architecture

One-page overview of the consolidated plugin's internal layout.

---

## The umbrella class

```csharp
public sealed partial class SectorWar :
    IAsyncModule,
    IArenaAttachableModule,
    // …14 broker interfaces published by partial-class subsystems
    IWarpInEffect, IShipSettings, ISectorClaim, IDeployableShop,
    IMoneySinks, ISectorWar, IPowerGrid, IMarketReader,
    ICompositeHitbox, IDamage, IEconomy, IRpg,
    IGunTurret, IPylon, IStaticTurret, IInventory
{ … }
```

The class is split across ~30 files via C# `partial class` files. SS.NET sees
ONE module, the file system sees ~30 partial files (one per subsystem cluster
plus a small umbrella scaffold).

```
src/SectorWar/Modules/
  SectorWar.cs                       — umbrella scaffold: lifecycle, DI, ArenaData
  SectorWar.<Subsystem>.cs           — one file per subsystem (28 of them)
```

---

## Lifecycle

| Method | Phase | What runs |
|---|---|---|
| `IAsyncModule.LoadAsync(broker, ct)` | Process startup, once | Allocate per-arena `ArenaDataKey<ArenaData>`, then call each subsystem's `LoadFooAsync(broker, ct)` or sync `LoadFoo(broker)` in dependency order. Async because some subsystems await `IPersist.RegisterPersistentDataAsync`. |
| `IArenaAttachableModule.AttachModule(arena)` | Per-arena | Get the `ArenaData`, then call each subsystem's `AttachFoo(arena)`. |
| `IArenaAttachableModule.DetachModule(arena)` | Per-arena | Call each subsystem's `DetachFoo(arena)` in reverse-load order, each wrapped in try/catch (phong's no-crash requirement). Then null `ad.Arena`. |
| `IAsyncModule.UnloadAsync(broker, ct)` | Process shutdown | Call each subsystem's `UnloadFoo(broker)` in reverse-load order, awaiting persist-flush where applicable, then free the per-arena data key. |

---

## Load-order dependency map

```
Foundation (no inter-subsystem deps)
   ├ FreqChangeWarp, WarpInEffect, Motd, AutoBrick, PerShipLvz, Promotion,
   │  ShipSettings, HullVisuals, MoneySinks, SectorWarState, DevCommands,
   │  ModularShip, PowerGrid, CtfGame
   │
Damage stack
   ├ Damage          (publishes IDamage)
   ├ StaticTurret    (consumes IDamage via cast-on-self — same partial class)
   └ GunTurret
   │
Pylon stack
   ├ Pylon (async)   (publishes IPylon → SectorClaim subscribes at Load,
   │                  StationDeployer / DeployableShop / ArenaDefenses query)
   ├ SectorClaim
   ├ SectorClaimVisual
   ├ DeployableShop
   ├ ArenaDefenses
   ├ BossEncounter
   └ CompositeHitbox
   │
Async / persist subsystems
   ├ Market (async)
   ├ Rpg (async)             (publishes IEconomy, IRpg)
   └ StationDeployer (async) (needs IPylon + IStaticTurret + IPowerGrid)
   │
Inventory (last, async)
   └ Consumes IShipSettings, IRpg, IEconomy, IMoneySinks, IMarketReader,
     IGunTurret, IDeployableShop. All registered by sibling partials above.
```

---

## ArenaData

One per arena, stored in the SS.NET extra-data slot keyed by the umbrella's
`ArenaDataKey<ArenaData>`. Each subsystem extends it with its own fields via
the partial-class extension pattern:

```csharp
internal sealed partial class ArenaData : IResettable
{
    public Arena? Arena;                                  // umbrella
    public List<AutoBrickData> AutoBrickBricks = new();    // AutoBrick partial
    public List<DamageWeapon> DamageWeaponSet = new();     // Damage partial
    public List<StructureInstance> StationDeployerStructures = new();
    // … etc.
}
```

Each subsystem's fields are prefixed with the subsystem's name to avoid
collisions in the merged class.

---

## Build / runtime contract

The plugin is **dynamically loaded** by SS.NET's `ModuleLoader` from the path
in `Modules.config`. The csproj sets:

- `<EnableDynamicLoading>true</EnableDynamicLoading>`
- `<Private>False</Private>` + `<ExcludeAssets>all</ExcludeAssets>` on every
  ProjectReference to SS.NET assemblies, so they're NOT bundled in our output.
- `<OutDir>` defaults to a local `bin/Release/SectorWar/` so the build doesn't
  fight a running zone's locked DLL.
- `$(DeployTo)` MSBuild property: when set, the post-build target copies the
  plugin DLL + .deps.json + .pdb + .runtimeconfig.json into the host zone's
  modules folder.

The host server provides Core/Packets/Utilities at runtime via its own
ModuleLoader-managed AssemblyLoadContext.

---

## See also

- [README.md](../README.md) — install + command reference
- [SECTORWAR_CONF.md](SECTORWAR_CONF.md) — conf key reference
