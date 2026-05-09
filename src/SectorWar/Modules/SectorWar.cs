using Microsoft.Extensions.ObjectPool;  // IResettable (per-arena slot recycling)
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — SectorWar consolidated zone plugin (umbrella partial class).
// =============================================================================
//
// PURPOSE
// -------
// One module registered with SS.NET, one section in arena.conf (`[SectorWar]`),
// one toggle in the Nexus arena admin UI. Internally this class aggregates the
// behaviour of ~29 SectorWar-specific subsystems (Pylon, Stations, Claim,
// Defenses, Damage, ModularShip, Inventory, Market, Rpg, …). The aggregation
// happens via C# `partial class` files: every subsystem lives in its own
// `SectorWar.<Topic>.cs` companion file but contributes to ONE class at runtime.
//
// WHY ONE CLASS
// -------------
// phong (Nexus zone admin) explicitly asked for:
//   1. ONE module registered (not 33).
//   2. `IArenaAttachableModule` so Nexus can attach per-arena, not zone-global.
//   3. ONE arena.conf section.
//   4. Built as a separate solution alongside SubspaceServer source (no fork).
//   5. Won't crash the zone — graceful degradation, no NREs on detach.
//
// A single `partial class SectorWar` satisfies (1)+(2)+(3) directly: SS.NET sees
// one type; arena.conf points at one section; AttachModule/DetachModule run
// once per arena. (4) is met by the existing Cres-style csproj output path.
// (5) is preserved by carrying forward the Wave-1..13 correctness fixes from
// the standalone modules.
//
// LIFECYCLE
// ---------
//   IModule.Load(broker)             — once per process. Allocate the per-arena
//                                       data key, register interfaces (IPylon,
//                                       IDamage, ISectorClaim, …) so partial
//                                       files can find each other across files.
//                                       NO per-arena state spun up here.
//
//   IArenaAttachableModule.AttachModule(arena)
//                                    — once per arena that opts in via its
//                                       arena.conf [Modules] AttachModules.
//                                       Read [SectorWar] conf, init per-arena
//                                       registries (pylons, stations, fakes,
//                                       weapons, …), subscribe ArenaActionCallback
//                                       / KillCallback / etc. for THIS arena.
//
//   IArenaAttachableModule.DetachModule(arena)
//                                    — opposite of attach. Drain every fake,
//                                       cancel every timer, persist final
//                                       snapshot, unsubscribe callbacks.
//
//   IModule.Unload(broker)           — once per process at shutdown. Awaits the
//                                       race-free flush path (as in Pylon's
//                                       FlushAllArenasAsync), then unregisters
//                                       all interfaces.
//
// SUBSYSTEM LAYOUT (partial files in this same folder)
// ---------------------------------------------------
//   SectorWar.cs                ← THIS FILE: lifecycle plumbing + ArenaData key
//   SectorWar.Conf.cs           ← single [SectorWar] conf reader, all keys
//   SectorWar.Persist.cs        ← consolidated PersistKeys 200..220
//   SectorWar.Damage.cs         ← asss-damage Phase-1 bullet port
//   SectorWar.StaticTurret.cs   ← D1st0rt's static-turret AI port
//   SectorWar.GunTurret.cs      ← player-attached turret system
//   SectorWar.Pylon.cs          ← pylon deploy/lifecycle/persistence
//   SectorWar.PowerGrid.cs      ← power network sub/unsub
//   SectorWar.Stations.cs       ← StationDeployer + DeployableShop
//   SectorWar.Claim.cs          ← SectorClaim + SectorClaimVisual
//   SectorWar.Defenses.cs       ← ArenaDefenses + WarStationMinions
//   SectorWar.WarpIn.cs         ← WarpInEffect
//   SectorWar.State.cs          ← per-arena state tracker (ex-`SectorWar`,
//                                  internally `SectorWarState`)
//   SectorWar.Inventory.cs      ← Inventory + dialogs
//   SectorWar.Market.cs         ← Market + MoneySinks
//   SectorWar.Rpg.cs            ← Rpg + Promotion + ShipSettings
//   SectorWar.Ctf.cs            ← CtfGame
//   SectorWar.AutoBrick.cs      ← AutoBrick
//   SectorWar.PerShipLvz.cs     ← PerShipLvz + HullVisuals
//   SectorWar.ModularShip.cs    ← ModularShip + CompositeHitbox + BossEncounter
//   SectorWar.Misc.cs           ← Motd + FreqChangeWarp + DevCommands
//
// During Phase 1 of the consolidation each `<Topic>.cs` file is added one at a
// time, building clean before the next is touched. Until a subsystem has been
// merged in, this scaffold class compiles as an empty no-op module (no
// subsystems, no callbacks, no persist work) — that's intentional. It exists
// so the partial-class TARGET is in place before subsystems are folded in.
//
// RELATIONSHIP TO THE STANDALONE MODULES
// --------------------------------------
// The 33 standalone modules under `Modules/*.cs` STAY in this csproj as a
// reusable library. Phase 1 COPIES code into the partial files (it does NOT
// delete the originals). After Phase 1, `Modules.config` loads only this
// `SectorWar` umbrella, but the standalone module sources remain available
// for other projects that want to mix-and-match individual subsystems.
//
// The originals and the umbrella will share interface types (ISectorClaim,
// IPylon, IPowerGrid, …) — those interfaces are the contract surface that
// stayed stable across the consolidation.
//
// AUTHORING NOTES
// ---------------
// - Every partial file MUST start with `namespace SS.SectorWar.Modules;`
//   and declare `public sealed partial class SectorWar`. Mismatched namespaces
//   silently produce a separate class and the partial doesn't merge.
//
// - Field DI is consolidated here in the umbrella file. Subsystems that need
//   `IChat` etc. read from these private fields, they don't add their own
//   constructor parameters. (One constructor only — that's a hard partial-class
//   rule.)
//
// - Each subsystem owns its own private state inside the per-arena
//   `ArenaData` (see SectorWar.ArenaData.cs once we add subsystems).
//   Cross-subsystem reads happen through interface lookups on the broker so
//   the partial files behave like loosely-coupled modules even though they
//   compile to one class.
// =============================================================================

[ModuleInfo(
    "SectorWar SectorWar — consolidated zone plugin (Phase 1 scaffold). " +
    "Aggregates ~29 SectorWar subsystems behind one IArenaAttachableModule for " +
    "Nexus compatibility. Currently an empty scaffold; subsystems land via " +
    "partial-class files in Phase 1.")]
public sealed partial class SectorWar : IAsyncModule, IArenaAttachableModule
{
    // -------------------------------------------------------------------------
    // CONSTANTS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Log category for every <see cref="ILogManager"/> call from this module
    /// (and all of its partial files). Reviewers grepping logs for plugin-side
    /// activity should `?logfile <SectorWar>` to filter.
    /// </summary>
    private const string LogCategory = nameof(SectorWar);

    /// <summary>
    /// Single arena.conf section name. Every subsystem reads its keys from
    /// this section — no `[Pylon]`, `[StationDeployer]`, etc. (Phong's spec.)
    /// </summary>
    internal const string ConfSection = nameof(SectorWar);

    // -------------------------------------------------------------------------
    // INJECTED SERVICES
    //
    // SS.NET resolves all of these at construction time. We hold them in
    // readonly fields and partial files reference them directly. Adding a new
    // service means: add field, add constructor parameter, add ?? null-guard
    // throw — anywhere. Don't introduce per-subsystem constructors; partial
    // classes only get ONE constructor.
    // -------------------------------------------------------------------------

    /// <summary>Arena lifecycle, lookup-by-name, lock-protected enumeration.</summary>
    private readonly IArenaManager _arenaManager;

    /// <summary>Drops bricks (used by AutoBrick subsystem to refresh wall
    /// segments on a timer).</summary>
    private readonly IBrickManager _brickManager;

    /// <summary>Public/private/team chat dispatch. Used by Motd to send the
    /// MOTD line-by-line, and by every command handler that responds to the
    /// invoker.</summary>
    private readonly IChat _chat;

    /// <summary>Client settings (per-ship per-player setting overrides).
    /// Used by ShipSettings to apply floor/cap framework, and by future
    /// subsystems that need per-player Radius/Energy/etc. overrides.</summary>
    private readonly IClientSettings _clientSettings;

    /// <summary>?command registration + dispatch. Used by Motd for
    /// ?motd/?setmotd/?addmotd, and by every other subsystem that exposes
    /// commands.</summary>
    private readonly ICommandManager _commandManager;

    /// <summary>King-of-the-Hill crown toggle dispatch. Used by Promotion's
    /// kill-streak crown reward.</summary>
    private readonly ICrowns _crowns;

    /// <summary>Reads `[SectorWar]` conf keys per-arena via <c>arena.Cfg</c>.</summary>
    private readonly IConfigManager _configManager;

    /// <summary>Spawns fake (NPC) players. Used by DevCommands ?damtest, by
    /// turret subsystems, by BossEncounter, etc.</summary>
    private readonly IFake _fake;

    /// <summary>Used by the FreqChangeWarp subsystem (and others later) to give
    /// players prizes / fake-kill / fake-position.</summary>
    private readonly IGame _game;

    /// <summary>Server log dispatch. Use <c>LogA</c> for arena-scoped events,
    /// <c>LogM</c> for module-scoped (no arena).</summary>
    private readonly ILogManager _logManager;

    /// <summary>LVZ object toggle/position dispatch. Used by PerShipLvz,
    /// SectorClaimVisual, ModularShip, WarpInEffect (Phase 2), etc.</summary>
    private readonly ILvzObjects _lvzObjects;

    /// <summary>Mainloop work-item queue. Used by StationDeployer + Pylon to
    /// schedule replay-from-persist on the mainloop (Persist_SetData runs on
    /// a worker thread).</summary>
    private readonly IMainloop _mainloop;

    /// <summary>Mainloop-bound timer (different from IServerTimer which runs
    /// on the thread pool). Used by ArenaDefenses to schedule IStaticTurret
    /// bot spawning, which is mainloop-only.</summary>
    private readonly IMainloopTimer _mainloopTimer;

    /// <summary>Map data — region lookups, tile classification. Used by
    /// CtfGame for per-team home regions.</summary>
    private readonly IMapData _mapData;

    /// <summary>Per-player extra-data slot allocator. Used by Motd's
    /// HasSeenMotd flag and by other subsystems with per-player state.</summary>
    private readonly IPlayerData _playerData;

    /// <summary>Mainloop timer dispatch. Used by AutoBrick to schedule
    /// per-arena brick refresh, and by other subsystems with periodic
    /// maintenance work.</summary>
    private readonly IServerTimer _serverTimer;

    // -------------------------------------------------------------------------
    // PER-ARENA DATA
    //
    // Allocated in IModule.Load, freed in IModule.Unload. Subsystems extend
    // ArenaData (see SectorWar.ArenaData.cs once we have subsystems) with their
    // own state — registries, locks, snapshots. Each arena that AttachModules
    // gets its own ArenaData instance; everything is per-arena scoped.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Allocated key into the per-arena extra-data slot. Look up an arena's
    /// state with <c>arena.TryGetExtraData(_adKey, out ArenaData? ad)</c>.
    /// </summary>
    private ArenaDataKey<ArenaData> _adKey;

    // -------------------------------------------------------------------------
    // CONSTRUCTOR
    // -------------------------------------------------------------------------

    /// <summary>
    /// SS.NET DI entry-point. Throws on null services so a misconfigured zone
    /// fails at construct-time rather than mysteriously at first arena attach.
    /// </summary>
    /// <remarks>
    /// Adding a new dependency: add a parameter here, add the readonly field
    /// above, plus a <c>?? throw new ArgumentNullException(nameof(...))</c>
    /// line. Don't add per-subsystem constructors; partial classes are required
    /// to declare exactly one constructor signature shared across all files.
    /// </remarks>
    public SectorWar(
        IArenaManager arenaManager,
        IBrickManager brickManager,
        IChat chat,
        IClientSettings clientSettings,
        ICommandManager commandManager,
        IConfigManager configManager,
        ICrowns crowns,
        IFake fake,
        IGame game,
        ILogManager logManager,
        ILvzObjects lvzObjects,
        IMainloop mainloop,
        IMainloopTimer mainloopTimer,
        IMapData mapData,
        IPlayerData playerData,
        IServerTimer serverTimer)
    {
        _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        _brickManager = brickManager ?? throw new ArgumentNullException(nameof(brickManager));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
        _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _crowns = crowns ?? throw new ArgumentNullException(nameof(crowns));
        _fake = fake ?? throw new ArgumentNullException(nameof(fake));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _lvzObjects = lvzObjects ?? throw new ArgumentNullException(nameof(lvzObjects));
        _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
        _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
        _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        _serverTimer = serverTimer ?? throw new ArgumentNullException(nameof(serverTimer));
    }

    // -------------------------------------------------------------------------
    // IModule
    // -------------------------------------------------------------------------

    /// <summary>
    /// Process-wide initialisation. Allocates the per-arena data key (so any
    /// arena that later attaches has a slot), registers broker interfaces, and
    /// hooks any zone-wide callbacks. Per-arena state is NOT spun up here —
    /// that lives in <see cref="IArenaAttachableModule.AttachModule"/>.
    /// </summary>
    /// <remarks>
    /// Threading: called once on the mainloop during zone startup. Any
    /// callback subscriptions made here fire on the mainloop too unless an
    /// SS.NET subsystem says otherwise (a few — like <see cref="IPersist"/>'s
    /// PutData/GetData — run on a worker thread; those subsystems get noted
    /// when they appear in their respective partial files).
    /// </remarks>
    async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
    {
        _adKey = _arenaManager.AllocateArenaData<ArenaData>();

        // Subsystem load hooks. Each subsystem's `LoadFoo(broker)` lives in
        // SectorWar.<Foo>.cs and contributes its own zone-wide setup. Order
        // matters when subsystems depend on each other (e.g. Damage must load
        // before consumers that call AddFake). For now, FreqChangeWarp is the
        // only subsystem folded in and it has no zone-wide state.
        // Order matters: SectorClaim must load BEFORE SectorClaimVisual so
        // the visual subsystem can resolve ISectorClaim from the broker. In
        // the parallel-coexistence period this still works because the
        // standalone SectorClaim provides the interface; once the umbrella's
        // SectorClaim runs LoadSectorClaim first, the registration happens
        // in-class, before LoadSectorClaimVisual asks for it.
        // Foundation utilities (no inter-subsystem deps).
        LoadFreqChangeWarp(broker);
        LoadWarpInEffect(broker);
        LoadMotd(broker);
        LoadAutoBrick(broker);
        LoadPerShipLvz(broker);
        LoadPromotion(broker);
        LoadShipSettings(broker);
        LoadHullVisuals(broker);
        LoadMoneySinks(broker);
        LoadSectorWarState(broker);
        LoadDevCommands(broker);
        LoadModularShip(broker);
        LoadPowerGrid(broker);
        LoadCtf(broker);

        // Damage stack: Damage publishes IDamage; StaticTurret consumes it
        // (cast-on-self because it's the same partial class). GunTurret
        // doesn't need IDamage but conceptually pairs with the damage layer.
        LoadDamage(broker);
        LoadStaticTurret(broker);
        LoadGunTurret(broker);

        // Pylon stack: Pylon publishes IPylon. SectorClaim subscribes to its
        // events at Load — so Pylon MUST load first. SectorClaimVisual then
        // resolves ISectorClaim. Deployable layers (DeployableShop,
        // ArenaDefenses, BossEncounter, CompositeHitbox) follow.
        await LoadPylonAsync(broker, cancellationToken);
        LoadSectorClaim(broker);
        LoadSectorClaimVisual(broker);
        LoadDeployableShop(broker);
        LoadArenaDefenses(broker);
        LoadBossEncounter(broker);
        LoadCompositeHitbox(broker);

        // Async / persist subsystems. Market + Rpg publish IMarketReader/
        // IEconomy/IRpg; StationDeployer needs IPylon + IStaticTurret (both
        // already loaded above).
        await LoadMarketAsync(broker, cancellationToken);
        await LoadRpgAsync(broker, cancellationToken);
        await LoadStationDeployerAsync(broker, cancellationToken);

        // Inventory loads LAST — its menus consume nearly everything: IEconomy
        // (Rpg), IShipSettings, IRpg, IMoneySinks, IMarketReader, IGunTurret,
        // IDeployableShop. All of those are now registered by sibling partials
        // earlier in this Load chain.
        await LoadInventoryAsync(broker, cancellationToken);

        _logManager.LogM(LogLevel.Info, LogCategory,
            "SectorWar umbrella loaded (Phase 1 — 28 subsystems folded in).");
        return true;
    }

    /// <summary>
    /// Process-wide teardown. Reverse of Load: unhook callbacks, unregister
    /// interfaces, free the per-arena data key. Async to allow awaited
    /// persist-flush paths.
    /// </summary>
    async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
    {
        // Subsystem unload hooks — strict reverse of LoadAsync order.
        await UnloadInventoryAsync(broker, cancellationToken);
        await UnloadStationDeployerAsync(broker, cancellationToken);
        await UnloadRpgAsync(broker, cancellationToken);
        await UnloadMarketAsync(broker, cancellationToken);

        UnloadCompositeHitbox(broker);
        UnloadBossEncounter(broker);
        UnloadArenaDefenses(broker);
        UnloadDeployableShop(broker);
        UnloadSectorClaimVisual(broker);
        UnloadSectorClaim(broker);
        await UnloadPylonAsync(broker, cancellationToken);

        UnloadGunTurret(broker);
        UnloadStaticTurret(broker);
        UnloadDamage(broker);

        UnloadCtf(broker);
        UnloadPowerGrid(broker);
        UnloadModularShip(broker);
        UnloadDevCommands(broker);
        UnloadSectorWarState(broker);
        UnloadMoneySinks(broker);
        UnloadHullVisuals(broker);
        UnloadShipSettings(broker);
        UnloadPromotion(broker);
        UnloadPerShipLvz(broker);
        UnloadAutoBrick(broker);
        UnloadMotd(broker);
        UnloadWarpInEffect(broker);
        UnloadFreqChangeWarp(broker);

        _arenaManager.FreeArenaData(ref _adKey);

        _logManager.LogM(LogLevel.Info, LogCategory, "SectorWar umbrella unloaded.");
        return true;
    }

    // -------------------------------------------------------------------------
    // IArenaAttachableModule
    //
    // Nexus calls these per-arena based on each arena's
    // [Modules] AttachModules = SS.SectorWar.Modules.SectorWar
    // line. Phong wants per-arena attach (not zone-global) so a Nexus admin
    // can toggle the entire SectorWar plugin on a single arena without bringing
    // it up zone-wide.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Per-arena initialisation. Reads `[SectorWar]` conf for THIS arena, hooks
    /// per-arena callbacks, spins up per-arena registries (pylons, stations,
    /// fakes, weapons), and announces presence in the log.
    /// </summary>
    /// <param name="arena">The arena attaching this module.</param>
    /// <returns>
    /// <c>true</c> on successful attach. <c>false</c> if conf read fails or
    /// any subsystem rejects the attach — the arena will run without SectorWar
    /// in that case (graceful degradation per phong's no-crash requirement).
    /// </returns>
    /// <remarks>
    /// Threading: mainloop only. SS.NET serialises arena lifecycle calls.
    /// </remarks>
    bool IArenaAttachableModule.AttachModule(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return false;
        ad.Arena = arena;

        // [SectorWar] conf reader lands in SectorWar.Conf.cs alongside the
        // first subsystem that actually needs conf keys (FreqChangeWarp reads
        // none).

        // Per-subsystem AttachModule hooks. Each subscribes its own callbacks
        // for THIS arena only. Order doesn't matter when subsystems are
        // independent (FreqChangeWarp is independent — pure ShipFreqChange
        // observer).
        AttachFreqChangeWarp(arena);
        AttachWarpInEffect(arena);
        AttachMotd(arena);
        AttachAutoBrick(arena);
        AttachPerShipLvz(arena);
        AttachPromotion(arena);
        AttachSectorClaim(arena);
        AttachSectorClaimVisual(arena);
        AttachShipSettings(arena);
        AttachHullVisuals(arena);
        AttachDeployableShop(arena);
        AttachArenaDefenses(arena);
        AttachMoneySinks(arena);
        AttachSectorWarState(arena);
        AttachDevCommands(arena);
        AttachModularShip(arena);
        AttachPowerGrid(arena);
        AttachBossEncounter(arena);
        AttachMarket(arena);
        AttachCtf(arena);
        AttachCompositeHitbox(arena);
        AttachDamage(arena);
        AttachStaticTurret(arena);
        AttachGunTurret(arena);
        AttachRpg(arena);
        AttachPylon(arena);
        AttachStationDeployer(arena);
        AttachInventory(arena);

        _logManager.LogA(LogLevel.Info, LogCategory, arena,
            "SectorWar attached (Phase 1 — 28 subsystems active).");
        return true;
    }

    /// <summary>
    /// Per-arena teardown. Drains every fake, cancels every timer, persists
    /// final snapshot for each subsystem that owns persistent state, then
    /// unsubscribes per-arena callbacks.
    /// </summary>
    /// <remarks>
    /// MUST NOT throw. Phong's no-crash requirement: even if a subsystem's
    /// detach fails, this method should log + continue so the arena's lifecycle
    /// finishes cleanly. Per-subsystem detach calls are wrapped in try/catch
    /// once they exist.
    /// </remarks>
    bool IArenaAttachableModule.DetachModule(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return true;

        // Per-subsystem detach hooks. Wrapped in try/catch so a single
        // misbehaving subsystem can't take down the arena's lifecycle.
        // Phong's no-crash requirement: even if a subsystem detach fails,
        // log + continue so the rest of teardown completes.
        try { DetachInventory(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Inventory detach failed: {ex.Message}");
        }
        try { DetachStationDeployer(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"StationDeployer detach failed: {ex.Message}");
        }
        try { DetachPylon(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Pylon detach failed: {ex.Message}");
        }
        try { DetachRpg(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Rpg detach failed: {ex.Message}");
        }
        try { DetachGunTurret(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"GunTurret detach failed: {ex.Message}");
        }
        try { DetachStaticTurret(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"StaticTurret detach failed: {ex.Message}");
        }
        try { DetachDamage(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Damage detach failed: {ex.Message}");
        }
        try { DetachCompositeHitbox(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"CompositeHitbox detach failed: {ex.Message}");
        }
        try { DetachCtf(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Ctf detach failed: {ex.Message}");
        }
        try { DetachMarket(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Market detach failed: {ex.Message}");
        }
        try { DetachBossEncounter(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"BossEncounter detach failed: {ex.Message}");
        }
        try { DetachPowerGrid(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"PowerGrid detach failed: {ex.Message}");
        }
        try { DetachModularShip(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"ModularShip detach failed: {ex.Message}");
        }
        try { DetachDevCommands(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"DevCommands detach failed: {ex.Message}");
        }
        try { DetachSectorWarState(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"SectorWarState detach failed: {ex.Message}");
        }
        try { DetachMoneySinks(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"MoneySinks detach failed: {ex.Message}");
        }
        try { DetachArenaDefenses(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"ArenaDefenses detach failed: {ex.Message}");
        }
        try { DetachDeployableShop(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"DeployableShop detach failed: {ex.Message}");
        }
        try { DetachHullVisuals(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"HullVisuals detach failed: {ex.Message}");
        }
        try { DetachShipSettings(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"ShipSettings detach failed: {ex.Message}");
        }
        try { DetachSectorClaimVisual(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"SectorClaimVisual detach failed: {ex.Message}");
        }
        try { DetachSectorClaim(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"SectorClaim detach failed: {ex.Message}");
        }
        try { DetachPromotion(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Promotion detach failed: {ex.Message}");
        }
        try { DetachPerShipLvz(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"PerShipLvz detach failed: {ex.Message}");
        }
        try { DetachAutoBrick(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"AutoBrick detach failed: {ex.Message}");
        }
        try { DetachMotd(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Motd detach failed: {ex.Message}");
        }
        try { DetachWarpInEffect(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"WarpInEffect detach failed: {ex.Message}");
        }
        try { DetachFreqChangeWarp(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"FreqChangeWarp detach failed: {ex.Message}");
        }

        ad.Arena = null;

        _logManager.LogA(LogLevel.Info, LogCategory, arena, "SectorWar detached.");
        return true;
    }

    // -------------------------------------------------------------------------
    // PER-ARENA STATE
    //
    // Each subsystem extends this class via a partial-class file at
    // SectorWar.ArenaData.<Topic>.cs (or by adding fields directly to ArenaData
    // in this file). We start empty; fields land alongside the subsystems
    // that need them.
    //
    // IResettable.TryReset is called by SS.NET when the per-arena slot is
    // recycled (e.g. arena destroy). Each subsystem's owning fields must be
    // reset to a known-empty state here so a recycled slot doesn't leak prior
    // arena state.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Per-arena state container. Lives in the arena's extra-data slot,
    /// keyed by <see cref="_adKey"/>. Subsystems add their own fields to
    /// this class as they're folded in.
    /// </summary>
    internal sealed partial class ArenaData : IResettable
    {
        /// <summary>The arena this data belongs to. Set in AttachModule,
        /// cleared in DetachModule. Never null between those two events.</summary>
        public Arena? Arena;

        bool IResettable.TryReset()
        {
            // Reset every subsystem's state to known-empty. Subsystems extend
            // this method via additional partial declarations of TryReset
            // helpers (or by adding their cleanup logic to a partial-method
            // declared here once we have at least one subsystem to call into).
            Arena = null;
            return true;
        }
    }
}
