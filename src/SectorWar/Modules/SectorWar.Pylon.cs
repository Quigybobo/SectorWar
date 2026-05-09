using System.Text;
using SS.SectorWar.Interfaces;
using SS.SectorWar.Persist;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — Pylon subsystem.
// =============================================================================
//
// PURPOSE
// -------
// Pylons are the foundation of SectorWar's power+claim infrastructure. Each
// pylon is a player-deployable static-turret fake (spawned via IStaticTurret)
// with three jobs:
//
//   1. POWER PROJECTION  — pylons gate structure operation. PowerGrid + the
//      StationDeployer subsystem call IPylon.IsPowered() to decide whether a
//      structure's turrets should fire (RequiredPower>=1 in StaticTurret).
//   2. CLAIM WEIGHT      — SectorClaim listens to PylonDeployed/PylonDespawned
//      to compute per-arena ownership.
//   3. DESTROYABLE TARGET — they ARE static turrets. Real-player bullets can
//      destroy them via the existing IDamage→IStaticTurret pipeline; the
//      BotKilled event flows back to OnTurretBotKilled_Pylon below to drop
//      the registry entry, ring, and level indicator slots.
//
// SOURCE
// ------
// Standalone module `Modules/Pylon.cs` stays as a library copy (Hard rule 11).
// This file folds Pylon into the umbrella partial class. Async because per-
// arena IPersist registration is awaited.
//
// CONF MIGRATION
// --------------
// Pylon's standalone form has NO conf keys (turret type is hardcoded to the
// "pylon" turret-key in the existing arena.conf, and pool sizes are compile-
// time constants). Nothing to migrate to `[SectorWar]`. If/when conf keys are
// added, they go under `[SectorWar]` with the `Pylon` prefix per Hard rule 7.
// (StaticTurret's own `[staticturret_pylon]` section is unaffected — that
// belongs to the StaticTurret subsystem, not Pylon.)
//
// PERSISTENCE
// -----------
// PersistKeys.Pylons / PersistInterval.ForeverNotShared / PerArena.
// Schema v1: count + per-pylon (freq i16, ownerName u8+utf8, x i32, y i32,
// level u8). Anchor (the static-turret fake-player) is NOT persisted — the
// deployer might be offline at restore time and the new fake is owned by
// IStaticTurret. OwnerName carries attribution. Mirrors the StationDeployer
// schema in shape.
//
// REPLAY-FROM-PERSIST PATTERN (matches StationDeployer)
// -----------------------------------------------------
// ArenaAction.Create fires in DoInit1 BEFORE persist load (DoInit2). So we
// CAN'T replay PendingRestore from the Create hook — by the time Create
// fires, PendingRestore is empty. Real replay queues onto IMainloop from
// Persist_Pylon_SetData (which runs on the persist worker thread). The
// ArenaAction.Create hook is kept as a cheap idempotent safety net.
//
// LIFECYCLE
// ---------
// Zone-wide, in LoadPylonAsync (called from the umbrella's IAsyncModule.LoadAsync):
//   - Add 5 commands (?deploypylon ?despawnpylons ?listpylons ?upgradepylon ?wipearena)
//   - Register IPersist data provider (awaited)
//   - Subscribe ArenaActionCallback (Create + Destroy)
//   - Resolve IStaticTurret + subscribe to its BotKilled event
//   - Register IPylon broker interface
//
// In UnloadPylonAsync (reverse order, with explicit flush before unregister):
//   - Unregister IPylon broker interface
//   - Unsubscribe BotKilled event + release IStaticTurret
//   - Unsubscribe ArenaActionCallback
//   - FlushAllPylonArenasAsync()  ← MUST happen BEFORE persist unregister
//                                   else recently-deployed pylons disappear
//                                   on fast shutdown (race documented below)
//   - Unregister IPersist data provider + release IPersist/IPersistExecutor
//   - Remove the 5 commands
//
// Per-arena (AttachPylon / DetachPylon): no work — Pylon's per-arena state
// flows through the umbrella's shared ArenaData and the ArenaActionCallback.
// Both are kept as no-op stubs to satisfy the umbrella's symmetric attach/
// detach contract.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: per-arena pylon registry, ring slot pool (16 slots,
//                  9000..9015), level-indicator slot pool (16 slots,
//                  9100..9115 — StationDeployer owns 9116..9131),
//                  PylonPendingRestore queue, Pylon→{Ring,Level} maps.
//   - Conf keys read: NONE.
//   - Persisted: yes (PerArena Forever, key 205).
//   - Fakes registered: 0 directly — IStaticTurret owns the bots; Pylon just
//                       calls AddBot/RemoveBotAt and listens for BotKilled.
//   - Timers scheduled: NONE.
//   - Commands registered: 5.
//   - Broker interfaces published: IPylon.
//
// CALLBACKS HOOKED
// ----------------
//   - ArenaActionCallback   (Create — replay safety net; Destroy — drain registry)
//   - IStaticTurret.BotKilled event — match dead bot to PylonInstance and
//     run registry-only despawn.
//
// THREADING
// ---------
// All mutation of per-arena pylon state happens on the mainloop thread:
//   - Commands run on the mainloop.
//   - ArenaActionCallback runs on the mainloop.
//   - BotKilled is invoked from StaticTurret on the mainloop.
//   - Persist_Pylon_GetData runs on the persist worker thread but it only
//     reads the pylon list; it never mutates registry state. Mainloop-only
//     mutation means Persist_Pylon_GetData's read is safe (no concurrent
//     writers). Persist_Pylon_SetData also runs on the persist worker thread
//     but ONLY writes to the PylonPendingRestore staging list before
//     QueueMainWorkItem-ing the actual registry mutation onto the mainloop.
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave: ArenaAction.Destroy hook with DrainPylonRegistryOnDestroy — without
//   this, an arena recycle leaves SectorClaim's claim weights attributed to
//   the dead arena and PowerGrid subscriptions stale (TryReset only nukes
//   local fields; downstream listeners never see PylonDespawned).
// Wave: DespawnPylonInternal(callRemoveBot) refactor — combat-kill path uses
//   callRemoveBot=false because the bot is already gone (StaticTurret's
//   OnBotDamaged tore it down BEFORE firing BotKilled); a redundant
//   RemoveBotAt would race against StaticTurret's own teardown.
// Wave: Pool exhaustion warns the deployer — when ring/level slot pools are
//   full, the turret bot still spawns but the LVZ overlay is invisible; we
//   chat the deployer so they know to ?despawnpylons stale ones.
// Wave: Anchor=null on initial deploy (matches restore path) — IStaticTurret
//   doesn't return a fake-player handle from AddBot, and downstream consumers
//   (PowerGrid, SectorClaim) only need OwnerFreq + position, so we set
//   Anchor=null on BOTH paths. Field semantics are then consistent regardless
//   of how the pylon was created.
// Wave: Deploy partial-failure rollback — if AddBot succeeds but a later
//   step (LVZ pool / event invocation / persist write) throws mid-flight,
//   we RemoveBotAt to drop the orphan turret rather than leaving it alive
//   without a registry entry.
// =============================================================================

public sealed partial class SectorWar : IPylon
{
    // -------------------------------------------------------------------------
    // CONSTANTS
    //
    // All Pylon-prefixed to avoid collisions with other subsystems' partials.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Disk-format version. Bump on serialization-format breaking changes.
    /// v1 = count + per-pylon (freq i16, ownerName u8+utf8, x i32, y i32,
    /// level u8). New fields go on the END so old saves stay readable until
    /// the next bump.
    /// </summary>
    private const byte PylonPersistVersion = 1;

    private const string PylonDeployCommand = "deploypylon";
    private const string PylonDespawnCommand = "despawnpylons";
    private const string PylonListCommand = "listpylons";
    private const string PylonUpgradeCommand = "upgradepylon";
    private const string PylonWipeArenaCommand = "wipearena";

    /// <summary>
    /// Max upgrade tiers a pylon supports. Functional scaling per tier lives
    /// in a future phase that extends IStaticTurret with per-bot stat
    /// overrides; for now levels are tracked + logged + persisted only.
    /// </summary>
    private const int PylonMaxUpgradeLevel = 5;

    /// <summary>
    /// IStaticTurret turret-key for pylons. Looked up in the arena conf as
    /// `[staticturret_pylon]`. If that section doesn't exist, AddBot returns
    /// non-Ok and Deploy fails with a logged warning.
    /// </summary>
    private const string PylonDefaultTurretKey = "pylon";

    /// <summary>Default warp-in animation length when deploying a pylon.</summary>
    private const int PylonWarpInDurationMs = 1500;

    // ---- LVZ slot pools ----
    //
    // Ring (power radius visualization, big translucent circle):
    //   IDs 9000..9015 reserved by the LVZ generator (lvz_warbird_capital.py).
    //   Each pylon allocates one ring slot, sets its position to the pylon's
    //   center (offset by half-image-width because LVZ position = top-left,
    //   not center), toggles ON. Despawn toggles OFF and pushes back to pool.
    //
    // Level indicator (small numeral above the pylon, "1".."5"):
    //   IDs 9100..9115. Pylons own 9100..9115; StationDeployer owns 9116..9131.
    //   Image IDs in the LVZ: 0 = pylon_ring, 1..5 = level_1..level_5.

    private const short PylonRingPoolStart = 9000;
    private const short PylonRingPoolEnd = 9015;
    /// <summary>Ring image (pylon_ring.bmp) is 784x784 px — to center it on
    /// the pylon we offset the LVZ position by half-width. LVZ object
    /// position is top-left, not center.</summary>
    private const int PylonRingImageHalfWidth = 392;

    private const short PylonLevelIndicatorPoolStart = 9100;
    private const short PylonLevelIndicatorPoolEnd = 9115;
    private const int PylonLevelIconHalfSize = 16;       // 32x32 image
    private const int PylonLevelIconOffsetY = -64;       // place above the pylon

    // -------------------------------------------------------------------------
    // ArenaData extension
    //
    // Per-arena state lives on the umbrella's shared ArenaData (so we extend
    // it via another partial declaration). All fields are Pylon-prefixed.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Disk-format snapshot of a single pylon. Anchor (Player) is intentionally
    /// not persisted — the player who deployed may be offline when the arena
    /// re-creates. <c>OwnerName</c> carries the attribution. Marked
    /// <c>internal</c> (not <c>private</c>) because it's referenced from the
    /// umbrella's <c>internal sealed partial class ArenaData</c>; a private
    /// type referenced from an internal type fails accessibility checks.
    /// </summary>
    internal sealed class PylonSnapshot
    {
        public required short OwnerFreq;
        public required string OwnerName;
        public required int CenterPixelX;
        public required int CenterPixelY;
        public required int UpgradeLevel;
    }

    internal sealed partial class ArenaData
    {
        /// <summary>Live pylon registry for this arena. Mutated only on the
        /// mainloop thread; safely read from Persist_Pylon_GetData (also on
        /// mainloop).</summary>
        public List<PylonInstance> PylonInstances = new();

        /// <summary>Pylon → its allocated ring LVZ slot id (9000..9015) or -1
        /// if the pool was exhausted at deploy time.</summary>
        public Dictionary<PylonInstance, short> PylonToRingId = new();

        /// <summary>Pylon → its allocated level-indicator LVZ slot id
        /// (9100..9115) or -1 if pool exhausted.</summary>
        public Dictionary<PylonInstance, short> PylonToLevelId = new();

        /// <summary>Free slot stack for the ring pool. Lazy-init on first
        /// AllocatePylonRingSlot call per arena.</summary>
        public Stack<short> PylonFreeRingIds = new();

        /// <summary>Free slot stack for the level-indicator pool. Same lazy
        /// init pattern.</summary>
        public Stack<short> PylonFreeLevelIds = new();

        public bool PylonRingPoolInitialized;
        public bool PylonLevelPoolInitialized;

        /// <summary>Snapshots staged by Persist_Pylon_SetData (worker thread)
        /// and replayed during ArenaAction.Create (mainloop thread). Cleared
        /// after replay.</summary>
        public List<PylonSnapshot> PylonPendingRestore = new();
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    //
    // Class-level fields, all _pylon-prefixed.
    // -------------------------------------------------------------------------

    private IComponentBroker? _pylonBroker;
    private IPersist? _pylonPersist;
    private IPersistExecutor? _pylonPersistExecutor;
    private IStaticTurret? _pylonStaticTurretForKills;
    private DelegatePersistentData<Arena>? _pylonPersistRegistration;
    private InterfaceRegistrationToken<IPylon>? _pylonToken;

    // -------------------------------------------------------------------------
    // IPylon EVENTS
    //
    // Interface-declared events. C# requires the implementing class to expose
    // them with the same accessibility — public on a public interface — so
    // they live on the umbrella class itself, not behind explicit interface
    // implementation. SectorClaim subscribes to these via the IPylon broker
    // interface (see SectorWar.SectorClaim.cs).
    // -------------------------------------------------------------------------

    /// <summary>Fired when a new pylon deploys (initial deploy AND
    /// persistence restore both fire this).</summary>
    public event Action<PylonInstance>? PylonDeployed;

    /// <summary>Fired when a pylon is removed — combat kill, ?despawnpylons,
    /// ?wipearena, AND arena destroy all route through this.</summary>
    public event Action<PylonInstance>? PylonDespawned;

    // -------------------------------------------------------------------------
    // ASYNC LOAD / UNLOAD
    // -------------------------------------------------------------------------

    /// <summary>
    /// Zone-wide subsystem load. Called from the umbrella's
    /// <c>IAsyncModule.LoadAsync</c>. Awaits IPersist registration so the
    /// persist provider is in place before any arena attaches.
    /// </summary>
    private async Task LoadPylonAsync(IComponentBroker broker, CancellationToken ct)
    {
        _pylonBroker = broker;

        _commandManager.AddCommand(PylonDeployCommand, Command_PylonDeploy);
        _commandManager.AddCommand(PylonDespawnCommand, Command_PylonDespawn);
        _commandManager.AddCommand(PylonListCommand, Command_PylonList);
        _commandManager.AddCommand(PylonUpgradeCommand, Command_PylonUpgrade);
        _commandManager.AddCommand(PylonWipeArenaCommand, Command_PylonWipeArena);

        // Arena-scoped persistence. SetData fires when an arena is being
        // CREATED — we deserialize into ArenaData.PylonPendingRestore. The
        // actual re-deploy (which depends on IStaticTurret being attached)
        // defers to IMainloop via QueueMainWorkItem from Persist_Pylon_SetData.
        // GetData fires on arena DESTROY (last player left) and also on
        // server shutdown, capturing the current pylon set to disk.
        _pylonPersist = broker.GetInterface<IPersist>();
        _pylonPersistExecutor = broker.GetInterface<IPersistExecutor>();
        if (_pylonPersist is not null)
        {
            _pylonPersistRegistration = new DelegatePersistentData<Arena>(
                PersistKeys.Pylons,
                PersistInterval.ForeverNotShared,
                PersistScope.PerArena,
                Persist_Pylon_GetData,
                Persist_Pylon_SetData,
                Persist_Pylon_ClearData);
            await _pylonPersist.RegisterPersistentDataAsync(_pylonPersistRegistration);
        }
        else
        {
            _logManager.LogM(LogLevel.Warn, LogCategory,
                "Pylon: IPersist unavailable — pylons will not survive arena recycle / server restart.");
        }

        ArenaActionCallback.Register(broker, OnArenaAction_Pylon);

        // Subscribe to StaticTurret kill events so destroyed pylon turrets
        // also drop their registry entry (and ring + claim weight). Held in
        // a separate field so we can unsubscribe in UnloadPylonAsync without
        // re-resolving the interface.
        _pylonStaticTurretForKills = broker.GetInterface<IStaticTurret>();
        if (_pylonStaticTurretForKills is not null)
        {
            _pylonStaticTurretForKills.BotKilled += OnTurretBotKilled_Pylon;
        }
        else
        {
            // Without IStaticTurret we can't subscribe to bot-death events;
            // pylons destroyed in combat will leave ghost registry entries.
            // In the umbrella this likely only happens if StaticTurret's
            // module isn't loaded at all (unlike the standalone Pylon, which
            // worried about Modules.config ordering). Loud warn either way.
            _logManager.LogM(LogLevel.Warn, LogCategory,
                "Pylon: IStaticTurret not loaded — pylon-kill cleanup will not fire. Pylons destroyed in combat will leave ghost registry entries.");
        }

        _pylonToken = broker.RegisterInterface<IPylon>(this);

        _logManager.LogM(LogLevel.Info, LogCategory,
            "Pylon subsystem loaded (persistent, kill-aware).");
    }

    /// <summary>
    /// Zone-wide subsystem unload. Reverse of <see cref="LoadPylonAsync"/>.
    /// CRITICAL: flushes every arena's pylon data to disk BEFORE unregistering
    /// the persist provider — see comment in body.
    /// </summary>
    private async Task UnloadPylonAsync(IComponentBroker broker, CancellationToken ct)
    {
        if (_pylonToken is not null)
            broker.UnregisterInterface(ref _pylonToken);

        if (_pylonStaticTurretForKills is not null)
        {
            _pylonStaticTurretForKills.BotKilled -= OnTurretBotKilled_Pylon;
            broker.ReleaseInterface(ref _pylonStaticTurretForKills);
        }

        ArenaActionCallback.Unregister(broker, OnArenaAction_Pylon);

        // CRITICAL: flush all arenas to disk BEFORE unregistering our persist
        // provider. PutArena queues a save onto the persist worker thread; if
        // we unregister first, the worker iterates the now-shrunken
        // registration list and never calls our GetData. End result: pylons
        // deployed shortly before shutdown disappear. So we await each
        // PutArena here, then unregister.
        await FlushAllPylonArenasAsync();

        if (_pylonPersist is not null && _pylonPersistRegistration is not null)
        {
            await _pylonPersist.UnregisterPersistentDataAsync(_pylonPersistRegistration);
            _pylonPersistRegistration = null;
            broker.ReleaseInterface(ref _pylonPersist);
        }
        if (_pylonPersistExecutor is not null)
            broker.ReleaseInterface(ref _pylonPersistExecutor);

        _commandManager.RemoveCommand(PylonDeployCommand, Command_PylonDeploy);
        _commandManager.RemoveCommand(PylonDespawnCommand, Command_PylonDespawn);
        _commandManager.RemoveCommand(PylonListCommand, Command_PylonList);
        _commandManager.RemoveCommand(PylonUpgradeCommand, Command_PylonUpgrade);
        _commandManager.RemoveCommand(PylonWipeArenaCommand, Command_PylonWipeArena);

        _pylonBroker = null;
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    //
    // Pylon's per-arena work is driven by ArenaActionCallback (Create/Destroy)
    // hooked zone-wide in LoadPylonAsync, so AttachPylon/DetachPylon are no-op
    // stubs. They exist to keep the umbrella's Attach* / Detach* dispatch
    // symmetric and to give future per-arena-only init a single place to land.
    // -------------------------------------------------------------------------

    private void AttachPylon(Arena arena) { /* arena state driven via ArenaActionCallback */ }
    private void DetachPylon(Arena arena) { /* same */ }

    // -------------------------------------------------------------------------
    // ARENA ACTION CALLBACK
    // -------------------------------------------------------------------------

    /// <summary>
    /// Per-arena lifecycle hook. Drives:
    ///   - Create: idempotent safety-net replay (real replay queued from
    ///             Persist_Pylon_SetData onto the mainloop).
    ///   - Destroy: drain the pylon registry so SectorClaim/PowerGrid see a
    ///              clean teardown before TryReset wipes ArenaData fields.
    /// </summary>
    private void OnArenaAction_Pylon(Arena arena, ArenaAction action)
    {
        switch (action)
        {
            case ArenaAction.Create:
                // ArenaAction.Create fires in DoInit1, BEFORE persist load
                // happens in DoInit2 — so PylonPendingRestore is always
                // empty here. The actual replay is queued from
                // Persist_Pylon_SetData via IMainloop. Keep this as a cheap
                // safety-net hook in case the order ever changes.
                ReplayPylonPendingRestore(arena);
                break;

            case ArenaAction.Destroy:
                // Wave-fix: when an arena recycles, ArenaData.TryReset only
                // clears local state — it doesn't fire PylonDespawned for
                // each pylon, so SectorClaim's claim weights stay attributed
                // to the dead arena and PowerGrid subs go stale. Drain the
                // registry properly first so downstream listeners get clean
                // notifications.
                DrainPylonRegistryOnDestroy(arena);
                break;
        }
    }

    /// <summary>
    /// Fires PylonDespawned + recomputes power for every pylon, then clears
    /// the registry. Called from ArenaAction.Destroy so downstream modules
    /// (SectorClaim, PowerGrid) see correct teardown ordering.
    /// </summary>
    private void DrainPylonRegistryOnDestroy(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.PylonInstances.Count == 0) return;

        // Snapshot under no lock (mainloop thread, no concurrent writes
        // expected during arena destroy).
        var pylons = ad.PylonInstances.ToArray();
        ad.PylonInstances.Clear();
        ad.PylonToRingId.Clear();
        ad.PylonToLevelId.Clear();

        var freqsTouched = new HashSet<short>();
        foreach (var p in pylons)
        {
            freqsTouched.Add(p.OwnerFreq);
            // Don't try to call IStaticTurret.RemoveBotAt — the arena is
            // tearing down, the turret bots are about to be reaped by
            // StaticTurret's own arena-destroy path (or already are).
            try { PylonDespawned?.Invoke(p); }
            catch (Exception ex)
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"PylonDespawned subscriber threw during arena drain: {ex.Message}");
            }
        }

        foreach (var freq in freqsTouched)
            UpdatePylonFreqPower(arena, ad, freq);

        _logManager.LogA(LogLevel.Info, LogCategory, arena,
            $"Drained {pylons.Length} pylon(s) from registry on arena destroy.");
    }

    /// <summary>
    /// Replay all pylon snapshots in <see cref="ArenaData.PylonPendingRestore"/>.
    /// Runs on the mainloop thread (either via ArenaAction or via
    /// QueueMainWorkItem). Idempotent — clears PylonPendingRestore on
    /// completion so repeated calls are no-ops.
    /// </summary>
    private void ReplayPylonPendingRestore(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.PylonPendingRestore.Count == 0) return;

        int restored = 0;
        foreach (var snap in ad.PylonPendingRestore)
        {
            if (RestorePylon(arena, ad, snap)) restored++;
        }
        ad.PylonPendingRestore.Clear();

        _logManager.LogA(LogLevel.Info, LogCategory, arena,
            $"Restored {restored} pylon(s) from persistence.");
    }

    // -------------------------------------------------------------------------
    // IPylon IMPLEMENTATION
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    PylonInstance? IPylon.Deploy(Arena arena, int pixelX, int pixelY, short freq, Player deployer)
    {
        if (_pylonBroker is null) return null;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return null;
        if (deployer.Name is null) return null;

        IStaticTurret? staticTurret = _pylonBroker.GetInterface<IStaticTurret>();
        if (staticTurret is null)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                "Cannot deploy pylon — IStaticTurret not loaded.");
            return null;
        }

        PylonInstance? result = null;
        bool botSpawned = false;
        try
        {
            // Spawn the pylon as a static turret.
            //
            // noLocationCheck=true: StaticTurret's default FitsOnMap rejects
            // placement on solid walls AND inside safe zones (tile 171). We
            // skip that check for pylons since:
            //   - Player spawns are typically in safe zones — first deploy
            //     would fail otherwise.
            //   - Game-design-wise we WANT pylons placeable anywhere the
            //     player wants. They're vulnerable so positioning is the
            //     player's call.
            //   - If a player tries to deploy on a wall they get a weird-
            //     looking pylon stuck in a wall — accepted tradeoff vs.
            //     restricting placement to "open space only".
            AddBotResult res = staticTurret.AddBot(arena, PylonDefaultTurretKey,
                pixelX, pixelY, freq,
                infiniteRespawn: false,
                noLocationCheck: true);
            if (res != AddBotResult.Ok)
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"Pylon deploy failed: AddBot returned {res} (type='{PylonDefaultTurretKey}').");
                return null;
            }
            botSpawned = true;

            // Allocate a ring LVZ slot + a level-indicator slot from per-arena pools.
            short ringId = AllocatePylonRingSlot(ad);
            short levelId = AllocatePylonLevelSlot(ad);
            if (ringId < PylonRingPoolStart || levelId < PylonLevelIndicatorPoolStart)
            {
                // Wave-fix: pool exhaustion warns the deployer so they know
                // the visual feedback won't appear (turret bot still spawns).
                _chat.SendMessage(deployer,
                    "Pylon deployed but the LVZ overlay pool is full — power ring / level indicator will be invisible. Despawn old pylons to free slots.");
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"Pylon LVZ pool exhausted at ({pixelX},{pixelY}) freq {freq}: ringId={ringId} levelId={levelId}.");
            }

            result = new PylonInstance
            {
                // Wave-fix: Anchor=null on initial deploy (matches restore
                // path). IStaticTurret doesn't return a fake-player handle
                // from AddBot, and downstream consumers (PowerGrid,
                // SectorClaim) only need OwnerFreq + position.
                Anchor = null,
                Arena = arena,
                OwnerFreq = freq,
                OwnerName = deployer.Name,
                CenterPixelX = pixelX,
                CenterPixelY = pixelY,
                DeployedAt = DateTime.UtcNow,
            };
            ad.PylonInstances.Add(result);
            ad.PylonToRingId[result] = ringId;
            ad.PylonToLevelId[result] = levelId;

            // Position the level indicator slot above the pylon. We don't
            // toggle ON unless level >= 1 (no indicator on base/level-0
            // pylons). The level indicator becomes visible only after the
            // first upgrade.
            if (levelId >= PylonLevelIndicatorPoolStart)
            {
                short lx = (short)(pixelX - PylonLevelIconHalfSize);
                short ly = (short)(pixelY + PylonLevelIconOffsetY);
                _lvzObjects.SetPosition(arena, levelId, lx, ly,
                    ScreenOffset.Normal, ScreenOffset.Normal);
                // image_id 0 = pylon_ring; 1..5 = level_1..level_5
                if (result.UpgradeLevel >= 1)
                {
                    _lvzObjects.SetImage(arena, levelId, (byte)result.UpgradeLevel);
                    _lvzObjects.Toggle(arena, levelId, true);
                }
            }

            // Phase 2.5 power gating: bump freq's power level. StaticTurret
            // bots with RequiredPower=1 fire when power >= 1. We use the
            // friendly-pylon count as power level — each pylon contributes
            // one unit of "power for this freq". Per-freq simplification:
            // any pylon on freq X powers all structures of freq X anywhere
            // in the arena. Per-pylon spatial gating (cleaner, harder) is
            // a future refinement.
            UpdatePylonFreqPower(arena, ad, freq);

            // Show the ring at the pylon's center. SetPosition expects top-
            // left of the image, so offset by -half-width on each axis. Then
            // Toggle ON to make it visible.
            if (ringId >= PylonRingPoolStart)
            {
                short ringX = (short)(pixelX - PylonRingImageHalfWidth);
                short ringY = (short)(pixelY - PylonRingImageHalfWidth);
                _lvzObjects.SetPosition(arena, ringId, ringX, ringY,
                    ScreenOffset.Normal, ScreenOffset.Normal);
                _lvzObjects.Toggle(arena, ringId, true);
            }

            _logManager.LogA(LogLevel.Info, LogCategory, arena,
                $"Pylon deployed by {deployer.Name} freq {freq} at " +
                $"({pixelX},{pixelY}). Ring={ringId}. Total: {ad.PylonInstances.Count}.");

            // Fire warp-in animation. Best-effort; missing IWarpInEffect just
            // means no anim (safe degradation).
            IWarpInEffect? warpIn = _pylonBroker.GetInterface<IWarpInEffect>();
            try
            {
                warpIn?.Play(arena, pixelX, pixelY, PylonWarpInDurationMs, WarpInFlavor.PylonCyan);
            }
            finally
            {
                if (warpIn is not null) _pylonBroker.ReleaseInterface(ref warpIn);
            }

            try { PylonDeployed?.Invoke(result); }
            catch (Exception ex)
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"PylonDeployed subscriber threw: {ex.Message}");
            }
            SavePylonArena(arena);
        }
        catch (Exception ex)
        {
            // Wave-fix: partial-failure rollback. If the bot spawned but
            // post-spawn registration threw, kill the bot so we don't leave
            // an orphan turret with no registry entry. Best-effort — log
            // and swallow.
            _logManager.LogA(LogLevel.Error, LogCategory, arena,
                $"Pylon deploy threw mid-registration: {ex}. Rolling back.");
            if (botSpawned && result is null)
            {
                try { staticTurret.RemoveBotAt(arena, pixelX, pixelY, freq, PylonDefaultTurretKey); }
                catch { /* best-effort cleanup */ }
            }
            result = null;
        }
        finally
        {
            _pylonBroker.ReleaseInterface(ref staticTurret);
        }

        return result;
    }

    /// <inheritdoc />
    void IPylon.Despawn(Arena arena, PylonInstance pylon)
    {
        // Wave-fix: callRemoveBot=true here because external callers
        // (?despawnpylons / ?wipearena / Despawn-from-API) expect the bot to
        // also disappear. The combat-kill path (OnTurretBotKilled_Pylon)
        // routes through DespawnPylonInternal with callRemoveBot=false
        // because the bot is already gone.
        DespawnPylonInternal(arena, pylon, callRemoveBot: true);
    }

    /// <summary>
    /// Internal teardown shared by full despawn (?despawnpylons / ?wipearena
    /// / external IPylon callers) and the bot-kill cleanup
    /// (<see cref="OnTurretBotKilled_Pylon"/>).
    /// </summary>
    /// <param name="callRemoveBot">
    /// True for explicit despawn calls (the bot is still alive and must be
    /// torn down). False for combat-kill cleanup (the bot is already gone —
    /// StaticTurret.OnBotDamaged tore it down BEFORE firing BotKilled, so a
    /// redundant RemoveBotAt would race and log a "no such bot" warning).
    /// </param>
    private void DespawnPylonInternal(Arena arena, PylonInstance pylon, bool callRemoveBot)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (!ad.PylonInstances.Remove(pylon)) return;

        // Toggle the ring OFF and return slot to pool.
        if (ad.PylonToRingId.TryGetValue(pylon, out short ringId))
        {
            _lvzObjects.Toggle(arena, ringId, false);
            ad.PylonToRingId.Remove(pylon);
            ad.PylonFreeRingIds.Push(ringId);
        }
        // Same for the level indicator slot.
        if (ad.PylonToLevelId.TryGetValue(pylon, out short levelId))
        {
            _lvzObjects.Toggle(arena, levelId, false);
            ad.PylonToLevelId.Remove(pylon);
            ad.PylonFreeLevelIds.Push(levelId);
        }

        // Recompute freq power after the pylon is removed. If this was the
        // last pylon on its freq, structures of that freq lose power and
        // their turrets stop firing.
        UpdatePylonFreqPower(arena, ad, pylon.OwnerFreq);

        if (callRemoveBot && _pylonBroker is not null)
        {
            IStaticTurret? staticTurret = _pylonBroker.GetInterface<IStaticTurret>();
            try
            {
                staticTurret?.RemoveBotAt(arena, pylon.CenterPixelX, pylon.CenterPixelY,
                    pylon.OwnerFreq, PylonDefaultTurretKey);
            }
            finally
            {
                if (staticTurret is not null) _pylonBroker.ReleaseInterface(ref staticTurret);
            }
        }

        _logManager.LogA(LogLevel.Info, LogCategory, arena,
            $"Pylon at ({pylon.CenterPixelX},{pylon.CenterPixelY}) despawned. " +
            $"Remaining: {ad.PylonInstances.Count}.");
        try { PylonDespawned?.Invoke(pylon); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"PylonDespawned subscriber threw: {ex.Message}");
        }
        SavePylonArena(arena);
    }

    /// <inheritdoc />
    IReadOnlyList<PylonInstance> IPylon.GetPylons(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
            return Array.Empty<PylonInstance>();
        // Snapshot — caller iterates without holding our lock.
        return ad.PylonInstances.ToArray();
    }

    /// <inheritdoc />
    bool IPylon.IsPowered(Arena arena, int pixelX, int pixelY, short freq)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return false;

        // Per-tick: linear scan. Fine while pylon counts are low (< 50/arena).
        // If we ever stack thousands, switch to a spatial index.
        foreach (var pylon in ad.PylonInstances)
        {
            if (pylon.OwnerFreq != freq) continue;
            int dx = pixelX - pylon.CenterPixelX;
            int dy = pixelY - pylon.CenterPixelY;
            int rsq = pylon.PowerRadiusPixels * pylon.PowerRadiusPixels;
            if (dx * dx + dy * dy <= rsq) return true;
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // FREQ POWER UPDATE
    // -------------------------------------------------------------------------

    /// <summary>
    /// Count active pylons on <paramref name="freq"/> and push the count as
    /// the freq's power level via <c>IStaticTurret.SetPower</c>. With
    /// outpost_gun's <c>RequiredPower=1</c>, any pylon-count >= 1 keeps that
    /// freq's turrets firing.
    /// </summary>
    private void UpdatePylonFreqPower(Arena arena, ArenaData ad, short freq)
    {
        if (_pylonBroker is null) return;
        int pylonCount = 0;
        foreach (var p in ad.PylonInstances)
            if (p.OwnerFreq == freq) pylonCount++;

        IStaticTurret? staticTurret = _pylonBroker.GetInterface<IStaticTurret>();
        try
        {
            staticTurret?.SetPower(arena, freq, pylonCount);
        }
        finally
        {
            if (staticTurret is not null) _pylonBroker.ReleaseInterface(ref staticTurret);
        }

        _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
            $"Freq {freq} power updated to {pylonCount} (pylon count).");
    }

    // -------------------------------------------------------------------------
    // SLOT POOL ALLOCATION
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pop a free ring LVZ id from the per-arena pool. Returns -1 if the
    /// pool is exhausted (16 pylons in one arena should be plenty for the
    /// first slice).
    /// </summary>
    private short AllocatePylonRingSlot(ArenaData ad)
    {
        // Lazy-init the free pool on first use per arena.
        if (!ad.PylonRingPoolInitialized)
        {
            for (short id = PylonRingPoolEnd; id >= PylonRingPoolStart; id--)
                ad.PylonFreeRingIds.Push(id);
            ad.PylonRingPoolInitialized = true;
        }
        return ad.PylonFreeRingIds.Count > 0 ? ad.PylonFreeRingIds.Pop() : (short)-1;
    }

    /// <summary>Same pattern as <see cref="AllocatePylonRingSlot"/>, separate
    /// pool for level indicators (9100..9115).</summary>
    private short AllocatePylonLevelSlot(ArenaData ad)
    {
        if (!ad.PylonLevelPoolInitialized)
        {
            for (short id = PylonLevelIndicatorPoolEnd; id >= PylonLevelIndicatorPoolStart; id--)
                ad.PylonFreeLevelIds.Push(id);
            ad.PylonLevelPoolInitialized = true;
        }
        return ad.PylonFreeLevelIds.Count > 0 ? ad.PylonFreeLevelIds.Pop() : (short)-1;
    }

    // -------------------------------------------------------------------------
    // KILL TRACKING (IStaticTurret.BotKilled)
    // -------------------------------------------------------------------------

    /// <summary>
    /// IStaticTurret.BotKilled handler. When a pylon turret is destroyed by
    /// real-player bullets (StaticTurret + IDamage routes the kill here),
    /// match the dead bot to a PylonInstance by position+freq and remove it
    /// from the registry. That fires PylonDespawned (SectorClaim picks up
    /// the claim flip), turns off the cyan ring + level indicator, and
    /// recomputes freq power so dependent structures lose power if this
    /// was the last pylon on the freq.
    /// </summary>
    private void OnTurretBotKilled_Pylon(Arena arena, string turretKey, int x, int y,
        short freq, Player? killer)
    {
        if (!string.Equals(turretKey, PylonDefaultTurretKey, StringComparison.OrdinalIgnoreCase))
            return;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        // Exact-match by position + freq. Pylons are spawned at the deployer's
        // exact coords so an exact equality is fine.
        PylonInstance? match = null;
        foreach (var p in ad.PylonInstances)
        {
            if (p.OwnerFreq != freq) continue;
            if (p.CenterPixelX != x || p.CenterPixelY != y) continue;
            match = p;
            break;
        }
        if (match is null) return;

        // Wave-fix: the bot is already gone (StaticTurret.OnBotDamaged tore
        // it down BEFORE firing this event). Use registry-only despawn so we
        // don't race with RemoveBotAt against a bot that doesn't exist
        // anymore.
        DespawnPylonInternal(arena, match, callRemoveBot: false);
    }

    // -------------------------------------------------------------------------
    // COMMANDS
    // -------------------------------------------------------------------------

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = null,
        Description = "Deploy a pylon at your current position on your freq. Sysop only for now.")]
    private void Command_PylonDeploy(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null)
        {
            _chat.SendMessage(player, "Not in an arena.");
            return;
        }
        if (player.Ship == ShipType.Spec)
        {
            _chat.SendMessage(player, "Get in a ship to deploy a pylon.");
            return;
        }

        IPylon self = this;
        var pylon = self.Deploy(player.Arena, player.Position.X, player.Position.Y,
            player.Freq, player);
        if (pylon is null)
            _chat.SendMessage(player,
                "Pylon deploy failed (check server log — likely missing [staticturret_pylon] conf).");
        else
            _chat.SendMessage(player,
                $"Pylon deployed at ({pylon.CenterPixelX},{pylon.CenterPixelY}).");
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = null,
        Description = "Despawn ALL pylons in this arena. Sysop only.")]
    private void Command_PylonDespawn(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null) return;
        IPylon self = this;
        var pylons = self.GetPylons(player.Arena);
        foreach (var p in pylons) self.Despawn(player.Arena, p);
        _chat.SendMessage(player, $"Despawned {pylons.Count} pylons.");
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = null,
        Description = "Nuke this arena's deployable state — every pylon, " +
            "structure, and turret bot (player-deployed AND AI-spawned) gets " +
            "removed. Sysop only. AI ArenaDefenses turrets won't auto-respawn " +
            "until the arena is recycled (?go elsewhere then ?go back).")]
    private void Command_PylonWipeArena(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null) return;
        var arena = player.Arena;
        int pylonCount = 0, structureCount = 0, botCount = 0;

        // Despawn pylons via own interface (covers both player and AI-spawned).
        IPylon selfPylon = this;
        var pylons = selfPylon.GetPylons(arena);
        foreach (var p in pylons) selfPylon.Despawn(arena, p);
        pylonCount = pylons.Count;

        // Despawn structures via IStationDeployer (player-deployed warstations
        // / outposts). Resolved through the broker so we don't depend on the
        // umbrella's call order; either the umbrella's StationDeployer
        // partial OR the standalone module satisfies this lookup.
        if (_pylonBroker is not null)
        {
            IStationDeployer? deployer = _pylonBroker.GetInterface<IStationDeployer>();
            try
            {
                if (deployer is not null)
                {
                    var structures = deployer.GetStructures(arena);
                    foreach (var s in structures) deployer.Despawn(arena, s);
                    structureCount = structures.Count;
                }
            }
            finally
            {
                if (deployer is not null) _pylonBroker.ReleaseInterface(ref deployer);
            }

            // Final pass: nuke every remaining StaticTurret bot in the arena.
            // Catches AI ArenaDefenses turrets (which spawn outside the
            // Pylon/StationDeployer registries) plus any orphaned bots. The
            // counts above already include bots torn down by Despawn; this
            // call returns whatever's left.
            IStaticTurret? staticTurret = _pylonBroker.GetInterface<IStaticTurret>();
            try
            {
                if (staticTurret is not null)
                    botCount = staticTurret.RemoveAllBots(arena);
            }
            finally
            {
                if (staticTurret is not null) _pylonBroker.ReleaseInterface(ref staticTurret);
            }
        }

        _chat.SendMessage(player,
            $"Wiped arena: {pylonCount} pylon(s), {structureCount} structure(s), " +
            $"{botCount} stray bot(s) removed. " +
            "(AI defenses re-spawn on arena recycle.)");
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = null,
        Description = "Upgrade the nearest pylon you own. Sysop only. Tracks level only — functional scaling pending Phase 3.")]
    private void Command_PylonUpgrade(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null) return;
        if (!player.Arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        // Find the nearest friendly pylon to the player.
        PylonInstance? nearest = null;
        long bestDsq = long.MaxValue;
        foreach (var p in ad.PylonInstances)
        {
            if (p.OwnerFreq != player.Freq) continue;
            long dx = p.CenterPixelX - player.Position.X;
            long dy = p.CenterPixelY - player.Position.Y;
            long dsq = dx * dx + dy * dy;
            if (dsq < bestDsq) { bestDsq = dsq; nearest = p; }
        }
        if (nearest is null)
        {
            _chat.SendMessage(player, "No friendly pylons in this arena to upgrade.");
            return;
        }
        if (nearest.UpgradeLevel >= PylonMaxUpgradeLevel)
        {
            _chat.SendMessage(player, $"Pylon already at max upgrade level ({PylonMaxUpgradeLevel}).");
            return;
        }
        nearest.UpgradeLevel++;

        // Refresh the level indicator visual.
        if (ad.PylonToLevelId.TryGetValue(nearest, out short lvlId))
        {
            _lvzObjects.SetImage(player.Arena, lvlId, (byte)nearest.UpgradeLevel);
            _lvzObjects.Toggle(player.Arena, lvlId, true);
        }

        _logManager.LogA(LogLevel.Info, LogCategory, player.Arena,
            $"Pylon at ({nearest.CenterPixelX},{nearest.CenterPixelY}) " +
            $"upgraded to lvl {nearest.UpgradeLevel} by {player.Name}.");
        _chat.SendMessage(player,
            $"Pylon upgraded to level {nearest.UpgradeLevel}/{PylonMaxUpgradeLevel} " +
            "(functional scaling pending Phase 3).");
        SavePylonArena(player.Arena);
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = null,
        Description = "List all pylons in this arena.")]
    private void Command_PylonList(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null) return;
        IPylon self = this;
        var pylons = self.GetPylons(player.Arena);
        if (pylons.Count == 0)
        {
            _chat.SendMessage(player, "No pylons in this arena.");
            return;
        }
        _chat.SendMessage(player, $"--- Pylons ({pylons.Count}) ---");
        foreach (var p in pylons)
        {
            _chat.SendMessage(player,
                $"  freq {p.OwnerFreq} owner {p.OwnerName} at " +
                $"({p.CenterPixelX >> 4},{p.CenterPixelY >> 4}) " +
                $"radius {p.PowerRadiusPixels >> 4}t " +
                $"lvl {p.UpgradeLevel}/{PylonMaxUpgradeLevel}");
        }
    }

    // -------------------------------------------------------------------------
    // PERSISTENCE
    // -------------------------------------------------------------------------

    /// <summary>
    /// Queue an arena-scope save to disk. Non-blocking — Persist runs the
    /// actual write on its worker thread. Called after every deploy /
    /// despawn / upgrade so state changes are durable even if the server
    /// crashes or shuts down before the natural arena-empty save fires.
    /// </summary>
    private void SavePylonArena(Arena arena)
    {
        _pylonPersistExecutor?.PutArena(arena, null);
    }

    /// <summary>
    /// Force a synchronous save of every arena's pylon data. Used in
    /// <see cref="UnloadPylonAsync"/> so the server doesn't lose recently-
    /// deployed pylons on fast shutdown. Each PutArena callback fires on
    /// the mainloop thread AFTER the worker has written the data, so
    /// awaiting them all gives us "all arenas durable on disk" before we
    /// proceed.
    /// </summary>
    private async Task FlushAllPylonArenasAsync()
    {
        if (_pylonPersistExecutor is null) return;

        var tasks = new List<Task>();
        _arenaManager.Lock();
        try
        {
            foreach (var arena in _arenaManager.Arenas)
            {
                var tcs = new TaskCompletionSource();
                _pylonPersistExecutor.PutArena(arena, _ => tcs.TrySetResult());
                tasks.Add(tcs.Task);
            }
        }
        finally
        {
            _arenaManager.Unlock();
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Replay a persisted pylon: re-spawn the static turret, re-allocate
    /// ring + level LVZ slots, push freq power, fire PylonDeployed event so
    /// SectorClaim et al. pick it up. No warp-in effect (it would visually
    /// "spawn" the pylon in front of an arriving player which is wrong).
    /// </summary>
    private bool RestorePylon(Arena arena, ArenaData ad, PylonSnapshot snap)
    {
        if (_pylonBroker is null) return false;
        IStaticTurret? staticTurret = _pylonBroker.GetInterface<IStaticTurret>();
        if (staticTurret is null)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                "Cannot restore pylon — IStaticTurret not loaded.");
            return false;
        }

        try
        {
            AddBotResult res = staticTurret.AddBot(arena, PylonDefaultTurretKey,
                snap.CenterPixelX, snap.CenterPixelY, snap.OwnerFreq,
                infiniteRespawn: false,
                noLocationCheck: true);
            if (res != AddBotResult.Ok)
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"Pylon restore failed at ({snap.CenterPixelX},{snap.CenterPixelY}): AddBot={res}.");
                return false;
            }

            short ringId = AllocatePylonRingSlot(ad);
            short levelId = AllocatePylonLevelSlot(ad);
            var pylon = new PylonInstance
            {
                // No anchor on restore — the original deployer might be
                // offline. Downstream consumers (PowerGrid, SectorClaim)
                // only need the freq/position/arena which are preserved.
                Anchor = null,
                Arena = arena,
                OwnerFreq = snap.OwnerFreq,
                OwnerName = snap.OwnerName,
                CenterPixelX = snap.CenterPixelX,
                CenterPixelY = snap.CenterPixelY,
                DeployedAt = DateTime.UtcNow,
                UpgradeLevel = snap.UpgradeLevel,
            };
            ad.PylonInstances.Add(pylon);
            ad.PylonToRingId[pylon] = ringId;
            ad.PylonToLevelId[pylon] = levelId;

            if (levelId >= PylonLevelIndicatorPoolStart)
            {
                short lx = (short)(snap.CenterPixelX - PylonLevelIconHalfSize);
                short ly = (short)(snap.CenterPixelY + PylonLevelIconOffsetY);
                _lvzObjects.SetPosition(arena, levelId, lx, ly,
                    ScreenOffset.Normal, ScreenOffset.Normal);
                if (pylon.UpgradeLevel >= 1)
                {
                    _lvzObjects.SetImage(arena, levelId, (byte)pylon.UpgradeLevel);
                    _lvzObjects.Toggle(arena, levelId, true);
                }
            }

            UpdatePylonFreqPower(arena, ad, snap.OwnerFreq);

            if (ringId >= PylonRingPoolStart)
            {
                short ringX = (short)(snap.CenterPixelX - PylonRingImageHalfWidth);
                short ringY = (short)(snap.CenterPixelY - PylonRingImageHalfWidth);
                _lvzObjects.SetPosition(arena, ringId, ringX, ringY,
                    ScreenOffset.Normal, ScreenOffset.Normal);
                _lvzObjects.Toggle(arena, ringId, true);
            }

            PylonDeployed?.Invoke(pylon);
            return true;
        }
        finally
        {
            _pylonBroker.ReleaseInterface(ref staticTurret);
        }
    }

    /// <summary>
    /// IPersist GetData callback — serialize the live pylon list. Runs on
    /// the persist worker thread, but ArenaData mutation only happens on the
    /// mainloop, so reading without a lock is safe (no concurrent writers).
    /// </summary>
    private void Persist_Pylon_GetData(Arena? arena, Stream outStream)
    {
        if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.PylonInstances.Count == 0) return;

        using BinaryWriter w = new(outStream, Encoding.UTF8, leaveOpen: true);
        w.Write(PylonPersistVersion);
        w.Write(ad.PylonInstances.Count);
        foreach (var p in ad.PylonInstances)
        {
            w.Write(p.OwnerFreq);
            w.Write(p.OwnerName ?? string.Empty);
            w.Write(p.CenterPixelX);
            w.Write(p.CenterPixelY);
            w.Write((byte)p.UpgradeLevel);
        }
    }

    /// <summary>
    /// IPersist SetData callback — stage snapshots into PylonPendingRestore
    /// for replay on the mainloop. Runs on the persist worker thread; cannot
    /// call IStaticTurret.AddBot directly (mainloop-only). The actual replay
    /// is queued onto IMainloop and runs from
    /// <see cref="ReplayPylonPendingRestore"/>.
    /// </summary>
    private void Persist_Pylon_SetData(Arena? arena, Stream inStream)
    {
        if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        using BinaryReader r = new(inStream, Encoding.UTF8, leaveOpen: true);
        byte version = r.ReadByte();
        if (version != PylonPersistVersion)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Unknown pylon persist version {version}; skipping restore.");
            return;
        }

        int count = r.ReadInt32();
        ad.PylonPendingRestore.Clear();
        for (int i = 0; i < count; i++)
        {
            short freq = r.ReadInt16();
            string ownerName = r.ReadString();
            int x = r.ReadInt32();
            int y = r.ReadInt32();
            byte level = r.ReadByte();
            ad.PylonPendingRestore.Add(new PylonSnapshot
            {
                OwnerFreq = freq,
                OwnerName = ownerName,
                CenterPixelX = x,
                CenterPixelY = y,
                UpgradeLevel = level,
            });
        }

        // Persist_SetData runs on the persist worker thread, but
        // IStaticTurret.AddBot is mainloop-only. Queue the replay onto the
        // mainloop. Also note: ArenaAction.Create fires earlier in the arena
        // init pipeline (DoInit1) — BEFORE this SetData runs (DoInit2) — so
        // we can't rely on the Create event to trigger replay.
        if (count > 0)
        {
            _mainloop.QueueMainWorkItem(
                static state => state.self.ReplayPylonPendingRestore(state.arena),
                (self: this, arena));
        }
    }

    /// <summary>
    /// IPersist ClearData callback — drop the staging list when the persist
    /// layer says this arena's data was reset.
    /// </summary>
    private void Persist_Pylon_ClearData(Arena? arena)
    {
        if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        ad.PylonPendingRestore.Clear();
    }
}
