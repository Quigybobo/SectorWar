using System.Text;
using SS.SectorWar.Interfaces;
using SS.SectorWar.Persist;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — StationDeployer subsystem.
// =============================================================================
//
// PURPOSE
// -------
// Composite multi-turret structures (outpost / warstation). Player issues
// ?deploystructure → StationDeployer spawns N IStaticTurret bots in
// formation, allocates LVZ slots for level indicator + (warstation only)
// fortress baseplate, subscribes to IPowerGrid, plays warp-in effect, and
// persists the structure registry per-arena.
//
// SOURCE
// ------
// Standalone module `Modules/StationDeployer.cs` stays as a library copy.
// Async because per-arena IPersist registration is awaited.
//
// STRUCTURE TYPES (hardcoded for first slice)
//   outpost    — 4 corner guns (square 6 tiles) + 1 escort frigate at center
//   warstation — 8 perimeter guns in octagon + 1 warstation_command core at
//                center (command requires 3 pylons of power → encourages
//                multi-pylon investment)
//
// PERSISTENCE
// -----------
// PersistKeys.Structures / PersistInterval.ForeverNotShared / PerArena.
// Schema v1: count + per-structure (typeKey, freq, ownerName, x, y, level).
//
// REPLAY-FROM-PERSIST PATTERN
// ---------------------------
// ArenaAction.Create fires in DoInit1 BEFORE persist load (DoInit2). So we
// CAN'T replay PendingRestore from the Create hook — by the time it fires,
// PendingRestore is empty. Real replay queues onto IMainloop from
// Persist_SetData (which runs on persist worker thread).
//
// COMMANDS
//   ?deploystructure <typeKey>  — sysop deploy
//   ?despawnstructures          — sysop wipe-arena
//   ?liststructures             — list current
//   ?upgradestructure           — bump nearest friendly structure's level
//
// LVZ POOLS
//   Level indicators: 9116..9131 (16 slots; pylons own 9100..9115)
//   WarStation baseplates: 9300..9315 (16 slots, 384x384 floor graphic)
//
// RUNTIME OWNERSHIP
//   - Owned state: per-arena structures list, level/baseplate slot pools,
//                  LiveTurretCount registry, PendingRestore queue.
//   - Conf keys read: NONE (structure types hardcoded in first slice).
//   - Persisted: yes (PerArena Forever).
//   - Fakes registered: 0 directly — IStaticTurret owns the bots; we just
//                       AddBot/RemoveBotAt by position.
//   - Timers scheduled: NONE.
//   - Commands registered: 4.
//   - Broker interfaces published: IStationDeployer.
//
// CALLBACKS HOOKED
//   - ArenaActionCallback (Create — safety net for ReplayPendingRestore)
//   - IStaticTurret.BotKilled event — decrement LiveTurretCount, despawn
//     structure when last turret dies
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave 3: PowerGrid.Unsubscribe in Despawn (stops the closure leak that
// would otherwise fire forever). 32px tolerance for offset matching.
// Deploy partial-failure rollback (catch+Despawn). Double SaveArena
// removed (Despawn calls it once).
// =============================================================================

public sealed partial class SectorWar : IStationDeployer
{
    private const byte StationDeployerPersistVersion = 1;

    private const string StationDeployerDeployCommand = "deploystructure";
    private const string StationDeployerDespawnCommand = "despawnstructures";
    private const string StationDeployerListCommand = "liststructures";
    private const string StationDeployerUpgradeCommand = "upgradestructure";

    private const int StationDeployerMaxUpgradeLevel = 5;
    private const short StationDeployerLevelPoolStart = 9116;
    private const short StationDeployerLevelPoolEnd = 9131;
    private const int StationDeployerLevelIconHalfSize = 16;
    private const int StationDeployerLevelIconOffsetY = -64;
    private const short StationDeployerBaseplatePoolStart = 9300;
    private const short StationDeployerBaseplatePoolEnd = 9315;
    private const int StationDeployerBaseplateHalfSize = 192;
    private const byte StationDeployerBaseplateImageIndex = 15;

    /// <summary>32 px = 2 tiles. Slack for matching killed-bot coords back
    /// to the original slot offset.</summary>
    private const int StationDeployerOffsetMatchTolerancePx = 32;

    // -------------------------------------------------------------------------
    // STRUCTURE TYPE TABLE
    // -------------------------------------------------------------------------

    internal sealed class StationDeployerStructureTypeDef
    {
        public required string DisplayName;
        public required StationDeployerTurretSlot[] Turrets;
    }

    internal sealed class StationDeployerTurretSlot
    {
        public required string TurretKey;
        public int OffsetX;
        public int OffsetY;
    }

    private static readonly Dictionary<string, StationDeployerStructureTypeDef> StationDeployerTypes
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["outpost"] = new StationDeployerStructureTypeDef
        {
            DisplayName = "Outpost",
            Turrets = new[]
            {
                new StationDeployerTurretSlot { TurretKey = "outpost_gun",     OffsetX = -96, OffsetY = -96 },
                new StationDeployerTurretSlot { TurretKey = "outpost_gun",     OffsetX =  96, OffsetY = -96 },
                new StationDeployerTurretSlot { TurretKey = "outpost_gun",     OffsetX = -96, OffsetY =  96 },
                new StationDeployerTurretSlot { TurretKey = "outpost_gun",     OffsetX =  96, OffsetY =  96 },
                new StationDeployerTurretSlot { TurretKey = "outpost_frigate", OffsetX =   0, OffsetY =   0 },
            },
        },
        ["warstation"] = new StationDeployerStructureTypeDef
        {
            DisplayName = "WarStation",
            // Octagon at radius ~144 px (9 tiles) + central command core.
            Turrets = new[]
            {
                new StationDeployerTurretSlot { TurretKey = "warstation_gun",     OffsetX =    0, OffsetY = -144 },
                new StationDeployerTurretSlot { TurretKey = "warstation_gun",     OffsetX =  102, OffsetY = -102 },
                new StationDeployerTurretSlot { TurretKey = "warstation_gun",     OffsetX =  144, OffsetY =    0 },
                new StationDeployerTurretSlot { TurretKey = "warstation_gun",     OffsetX =  102, OffsetY =  102 },
                new StationDeployerTurretSlot { TurretKey = "warstation_gun",     OffsetX =    0, OffsetY =  144 },
                new StationDeployerTurretSlot { TurretKey = "warstation_gun",     OffsetX = -102, OffsetY =  102 },
                new StationDeployerTurretSlot { TurretKey = "warstation_gun",     OffsetX = -144, OffsetY =    0 },
                new StationDeployerTurretSlot { TurretKey = "warstation_gun",     OffsetX = -102, OffsetY = -102 },
                new StationDeployerTurretSlot { TurretKey = "warstation_command", OffsetX =    0, OffsetY =    0 },
            },
        },
    };

    // -------------------------------------------------------------------------
    // ArenaData extension
    // -------------------------------------------------------------------------

    internal sealed class StationDeployerStructureSnapshot
    {
        public required string TypeKey;
        public required short OwnerFreq;
        public required string OwnerName;
        public required int CenterPixelX;
        public required int CenterPixelY;
        public required int UpgradeLevel;
    }

    internal sealed partial class ArenaData
    {
        public List<StructureInstance> StationDeployerStructures = new();
        public Dictionary<StructureInstance, short> StationDeployerStructureToLevelId = new();
        public Stack<short> StationDeployerFreeLevelIds = new();
        public bool StationDeployerLevelPoolInitialized;
        public List<StationDeployerStructureSnapshot> StationDeployerPendingRestore = new();
        public Dictionary<StructureInstance, int> StationDeployerLiveTurretCount = new();
        public Dictionary<StructureInstance, short> StationDeployerStructureToBaseplateId = new();
        public Stack<short> StationDeployerFreeBaseplateIds = new();
        public bool StationDeployerBaseplatePoolInitialized;
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private IComponentBroker? _stationDeployerBroker;
    private IPersist? _stationDeployerPersist;
    private IPersistExecutor? _stationDeployerPersistExecutor;
    private IStaticTurret? _stationDeployerStaticTurretForKills;
    private DelegatePersistentData<Arena>? _stationDeployerPersistRegistration;
    private InterfaceRegistrationToken<IStationDeployer>? _stationDeployerToken;

    // -------------------------------------------------------------------------
    // ASYNC LOAD / UNLOAD
    // -------------------------------------------------------------------------

    private async Task LoadStationDeployerAsync(IComponentBroker broker, CancellationToken ct)
    {
        _stationDeployerBroker = broker;

        _commandManager.AddCommand(StationDeployerDeployCommand, Command_StationDeployerDeploy);
        _commandManager.AddCommand(StationDeployerDespawnCommand, Command_StationDeployerDespawn);
        _commandManager.AddCommand(StationDeployerListCommand, Command_StationDeployerList);
        _commandManager.AddCommand(StationDeployerUpgradeCommand, Command_StationDeployerUpgrade);

        _stationDeployerPersist = broker.GetInterface<IPersist>();
        _stationDeployerPersistExecutor = broker.GetInterface<IPersistExecutor>();
        if (_stationDeployerPersist is not null)
        {
            _stationDeployerPersistRegistration = new DelegatePersistentData<Arena>(
                PersistKeys.Structures,
                PersistInterval.ForeverNotShared,
                PersistScope.PerArena,
                Persist_StationDeployer_GetData,
                Persist_StationDeployer_SetData,
                Persist_StationDeployer_ClearData);
            await _stationDeployerPersist.RegisterPersistentDataAsync(_stationDeployerPersistRegistration);
        }
        else
        {
            _logManager.LogM(LogLevel.Warn, LogCategory,
                "StationDeployer: IPersist unavailable — structures won't survive recycle.");
        }

        ArenaActionCallback.Register(broker, OnArenaAction_StationDeployer);

        _stationDeployerStaticTurretForKills = broker.GetInterface<IStaticTurret>();
        if (_stationDeployerStaticTurretForKills is not null)
            _stationDeployerStaticTurretForKills.BotKilled += OnTurretBotKilled_StationDeployer;

        _stationDeployerToken = broker.RegisterInterface<IStationDeployer>(this);

        _logManager.LogM(LogLevel.Info, LogCategory,
            $"StationDeployer subsystem loaded — {StationDeployerTypes.Count} structure type(s).");
    }

    private async Task UnloadStationDeployerAsync(IComponentBroker broker, CancellationToken ct)
    {
        if (_stationDeployerToken is not null)
            broker.UnregisterInterface(ref _stationDeployerToken);

        if (_stationDeployerStaticTurretForKills is not null)
        {
            _stationDeployerStaticTurretForKills.BotKilled -= OnTurretBotKilled_StationDeployer;
            broker.ReleaseInterface(ref _stationDeployerStaticTurretForKills);
        }

        ArenaActionCallback.Unregister(broker, OnArenaAction_StationDeployer);

        // CRITICAL: flush before unregistering so we don't lose pending writes.
        await FlushAllStationDeployerArenasAsync();

        if (_stationDeployerPersist is not null && _stationDeployerPersistRegistration is not null)
        {
            await _stationDeployerPersist.UnregisterPersistentDataAsync(_stationDeployerPersistRegistration);
            _stationDeployerPersistRegistration = null;
            broker.ReleaseInterface(ref _stationDeployerPersist);
        }
        if (_stationDeployerPersistExecutor is not null)
            broker.ReleaseInterface(ref _stationDeployerPersistExecutor);

        _commandManager.RemoveCommand(StationDeployerDeployCommand, Command_StationDeployerDeploy);
        _commandManager.RemoveCommand(StationDeployerDespawnCommand, Command_StationDeployerDespawn);
        _commandManager.RemoveCommand(StationDeployerListCommand, Command_StationDeployerList);
        _commandManager.RemoveCommand(StationDeployerUpgradeCommand, Command_StationDeployerUpgrade);

        _stationDeployerBroker = null;
    }

    private void AttachStationDeployer(Arena arena) { /* arena-attached via callback */ }
    private void DetachStationDeployer(Arena arena) { /* same */ }

    // -------------------------------------------------------------------------
    // CALLBACK
    // -------------------------------------------------------------------------

    private void OnArenaAction_StationDeployer(Arena arena, ArenaAction action)
    {
        if (action != ArenaAction.Create) return;
        ReplayStationDeployerPendingRestore(arena);
    }

    private void ReplayStationDeployerPendingRestore(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.StationDeployerPendingRestore.Count == 0) return;

        int restored = 0;
        foreach (var snap in ad.StationDeployerPendingRestore)
        {
            if (RestoreStationDeployerStructure(arena, ad, snap)) restored++;
        }
        ad.StationDeployerPendingRestore.Clear();
        _logManager.LogA(LogLevel.Info, LogCategory, arena,
            $"Restored {restored} structure(s) from persistence.");
    }

    // -------------------------------------------------------------------------
    // IStationDeployer IMPLEMENTATION
    // -------------------------------------------------------------------------

    StructureInstance? IStationDeployer.Deploy(Arena arena, string typeKey,
        int pixelX, int pixelY, short freq, Player deployer)
    {
        if (_stationDeployerBroker is null) return null;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return null;
        if (deployer.Name is null) return null;

        if (!StationDeployerTypes.TryGetValue(typeKey, out var def))
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Unknown structure type '{typeKey}'. Known: {string.Join(",", StationDeployerTypes.Keys)}");
            return null;
        }

        // Pylon-power gate. Without it, structures could be placed anywhere
        // and just remain unpowered forever.
        IPylon? pylonCheck = _stationDeployerBroker.GetInterface<IPylon>();
        try
        {
            if (pylonCheck is null || !pylonCheck.IsPowered(arena, pixelX, pixelY, freq))
            {
                _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
                    $"Deploy rejected: ({pixelX},{pixelY}) outside pylon power.");
                return null;
            }
        }
        finally
        {
            if (pylonCheck is not null) _stationDeployerBroker.ReleaseInterface(ref pylonCheck);
        }

        IStaticTurret? staticTurret = _stationDeployerBroker.GetInterface<IStaticTurret>();
        if (staticTurret is null)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                "IStaticTurret not loaded — cannot deploy.");
            return null;
        }

        StructureInstance? instance = null;
        try
        {
            int spawnedCount = 0;
            int failedCount = 0;
            foreach (var slot in def.Turrets)
            {
                int sx = pixelX + slot.OffsetX;
                int sy = pixelY + slot.OffsetY;
                AddBotResult res = staticTurret.AddBot(arena, slot.TurretKey, sx, sy, freq,
                    infiniteRespawn: false, noLocationCheck: true);
                if (res == AddBotResult.Ok) spawnedCount++;
                else
                {
                    failedCount++;
                    _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
                        $"Turret '{slot.TurretKey}' at ({sx},{sy}) failed: {res}");
                }
            }

            if (spawnedCount == 0)
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"Structure '{typeKey}' deploy failed: 0 turrets spawned.");
                return null;
            }

            instance = new StructureInstance
            {
                TypeKey = typeKey,
                OwnerFreq = freq,
                OwnerName = deployer.Name,
                CenterPixelX = pixelX,
                CenterPixelY = pixelY,
                DeployedAt = DateTime.UtcNow,
                IsPowered = false,
            };
            ad.StationDeployerStructures.Add(instance);
            ad.StationDeployerLiveTurretCount[instance] = spawnedCount;

            // Level-indicator slot.
            short levelId = AllocateStationDeployerLevelSlot(ad);
            ad.StationDeployerStructureToLevelId[instance] = levelId;
            if (levelId >= StationDeployerLevelPoolStart)
            {
                short lx = (short)(pixelX - StationDeployerLevelIconHalfSize);
                short ly = (short)(pixelY + StationDeployerLevelIconOffsetY);
                _lvzObjects.SetPosition(arena, levelId, lx, ly,
                    ScreenOffset.Normal, ScreenOffset.Normal);
                if (instance.UpgradeLevel >= 1)
                {
                    _lvzObjects.SetImage(arena, levelId, (byte)instance.UpgradeLevel);
                    _lvzObjects.Toggle(arena, levelId, true);
                }
            }

            ShowStationDeployerBaseplate(arena, ad, instance);

            _logManager.LogA(LogLevel.Info, LogCategory, arena,
                $"Structure '{typeKey}' deployed by {deployer.Name} freq {freq} at ({pixelX},{pixelY}). " +
                $"Turrets: {spawnedCount}/{def.Turrets.Length}.");

            // Wave-3: Subscribe to PowerGrid; store token on instance for
            // Unsubscribe in Despawn (else closure + StructureInstance leak).
            IPowerGrid? powerGrid = _stationDeployerBroker.GetInterface<IPowerGrid>();
            try
            {
                if (powerGrid is not null)
                {
                    instance.PowerSub = powerGrid.Subscribe(arena, pixelX, pixelY, freq, (powered) =>
                    {
                        if (instance is null) return;
                        instance.IsPowered = powered;
                        _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
                            $"Structure '{typeKey}' at ({pixelX},{pixelY}) " +
                            $"power changed: {(powered ? "POWERED" : "UNPOWERED")}.");
                    });
                }
            }
            finally
            {
                if (powerGrid is not null) _stationDeployerBroker.ReleaseInterface(ref powerGrid);
            }

            // Warp-in animation.
            IWarpInEffect? warpIn = _stationDeployerBroker.GetInterface<IWarpInEffect>();
            try { warpIn?.Play(arena, pixelX, pixelY, 1500, WarpInFlavor.OutpostBlue); }
            finally { if (warpIn is not null) _stationDeployerBroker.ReleaseInterface(ref warpIn); }

            SaveStationDeployerArena(arena);
        }
        catch (Exception ex)
        {
            // Wave-3 partial-failure rollback.
            _logManager.LogA(LogLevel.Error, LogCategory, arena,
                $"Structure '{typeKey}' deploy threw mid-registration: {ex}. Rolling back.");
            if (instance is not null)
            {
                try { ((IStationDeployer)this).Despawn(arena, instance); }
                catch { /* best-effort */ }
            }
            else
            {
                foreach (var slot in def.Turrets)
                {
                    int sx = pixelX + slot.OffsetX;
                    int sy = pixelY + slot.OffsetY;
                    try { staticTurret.RemoveBotAt(arena, sx, sy, freq, slot.TurretKey); }
                    catch { /* best-effort */ }
                }
            }
            instance = null;
        }
        finally
        {
            _stationDeployerBroker.ReleaseInterface(ref staticTurret);
        }

        return instance;
    }

    void IStationDeployer.Despawn(Arena arena, StructureInstance structure)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        ad.StationDeployerStructures.Remove(structure);
        ad.StationDeployerLiveTurretCount.Remove(structure);

        // Wave-3: drop PowerGrid subscription (closure + StructureInstance leak guard).
        if (structure.PowerSub is not null && _stationDeployerBroker is not null)
        {
            IPowerGrid? powerGrid = _stationDeployerBroker.GetInterface<IPowerGrid>();
            try { powerGrid?.Unsubscribe(structure.PowerSub); }
            finally
            {
                if (powerGrid is not null) _stationDeployerBroker.ReleaseInterface(ref powerGrid);
            }
            structure.PowerSub = null;
        }

        // Toggle off + return level slot to pool.
        if (ad.StationDeployerStructureToLevelId.TryGetValue(structure, out short levelId))
        {
            _lvzObjects.Toggle(arena, levelId, false);
            ad.StationDeployerStructureToLevelId.Remove(structure);
            ad.StationDeployerFreeLevelIds.Push(levelId);
        }

        HideStationDeployerBaseplate(arena, ad, structure);

        // Tear down each turret bot in formation.
        if (_stationDeployerBroker is not null
            && StationDeployerTypes.TryGetValue(structure.TypeKey, out var def))
        {
            IStaticTurret? staticTurret = _stationDeployerBroker.GetInterface<IStaticTurret>();
            try
            {
                if (staticTurret is not null)
                {
                    foreach (var slot in def.Turrets)
                    {
                        int sx = structure.CenterPixelX + slot.OffsetX;
                        int sy = structure.CenterPixelY + slot.OffsetY;
                        staticTurret.RemoveBotAt(arena, sx, sy, structure.OwnerFreq, slot.TurretKey);
                    }
                }
            }
            finally
            {
                if (staticTurret is not null)
                    _stationDeployerBroker.ReleaseInterface(ref staticTurret);
            }
        }

        SaveStationDeployerArena(arena);
    }

    IReadOnlyList<StructureInstance> IStationDeployer.GetStructures(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
            return Array.Empty<StructureInstance>();
        return ad.StationDeployerStructures.ToArray();
    }

    // -------------------------------------------------------------------------
    // COMMANDS
    // -------------------------------------------------------------------------

    [CommandHelp(Targets = CommandTarget.None, Args = "<typeKey>",
        Description = "Deploy a structure at your position. Sysop only.")]
    private void Command_StationDeployerDeploy(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null) return;
        string typeKey = parameters.IsEmpty ? "outpost" : parameters.Trim().ToString();
        if (!StationDeployerTypes.ContainsKey(typeKey))
        {
            _chat.SendMessage(player,
                $"Unknown structure type '{typeKey}'. Known: {string.Join(", ", StationDeployerTypes.Keys)}.");
            return;
        }
        if (player.Ship == ShipType.Spec)
        { _chat.SendMessage(player, "Get in a ship to deploy."); return; }

        // Pre-flight power check for clearer error message.
        IPylon? pylon = _stationDeployerBroker?.GetInterface<IPylon>();
        bool inPower;
        try
        {
            inPower = pylon?.IsPowered(player.Arena, player.Position.X, player.Position.Y, player.Freq) ?? false;
        }
        finally
        {
            if (pylon is not null && _stationDeployerBroker is not null)
                _stationDeployerBroker.ReleaseInterface(ref pylon);
        }
        if (!inPower)
        {
            _chat.SendMessage(player,
                "Deploy failed: out of pylon power range. Place a pylon nearby first.");
            return;
        }

        IStationDeployer self = this;
        var inst = self.Deploy(player.Arena, typeKey, player.Position.X, player.Position.Y,
            player.Freq, player);
        if (inst is null) { _chat.SendMessage(player, "Deploy failed (check server log)."); return; }

        var typeDef = StationDeployerTypes[typeKey];
        _chat.SendMessage(player,
            $"{typeDef.DisplayName} deployed at ({inst.CenterPixelX},{inst.CenterPixelY}).");

        // Power-state feedback. Compare each turret slot's RequiredPower
        // against the freq's current pylon count so the deployer sees
        // immediately whether their network can power the structure
        // (or which slots are dormant). Saves a 200K-credit warstation
        // sitting idle because the player only has 1 pylon.
        ReportDeployPowerState_StationDeployer(player, typeDef);
    }

    private void ReportDeployPowerState_StationDeployer(Player player, StationDeployerStructureTypeDef typeDef)
    {
        if (_stationDeployerBroker is null) return;
        if (player.Arena is null) return;

        IPylon? pylon = _stationDeployerBroker.GetInterface<IPylon>();
        if (pylon is null) return;
        int friendlyPylons;
        try
        {
            friendlyPylons = 0;
            foreach (var p in pylon.GetPylons(player.Arena))
                if (p.OwnerFreq == player.Freq) friendlyPylons++;
        }
        finally { _stationDeployerBroker.ReleaseInterface(ref pylon); }

        if (!arena_TryGetTurretTypes(player.Arena, out var turretTypes)) return;

        // Tally how many slots of each turret type are powered vs not.
        int totalSlots = 0, powered = 0;
        var unpoweredTypes = new HashSet<string>();
        foreach (var slot in typeDef.Turrets)
        {
            totalSlots++;
            if (!turretTypes.TryGetValue(slot.TurretKey, out var tt)) continue;
            if (tt.RequiredPower <= friendlyPylons) powered++;
            else unpoweredTypes.Add($"{slot.TurretKey} (needs {tt.RequiredPower})");
        }

        if (powered == totalSlots)
        {
            _chat.SendMessage(player, $"  Power: {friendlyPylons} pylon(s). All {totalSlots} turrets active.");
        }
        else
        {
            string need = string.Join(", ", unpoweredTypes);
            _chat.SendMessage(player,
                $"  Power: {friendlyPylons} pylon(s). {powered}/{totalSlots} turrets active. " +
                $"Build more pylons to activate: {need}.");
        }
    }

    /// <summary>Helper to peek at StaticTurretType definitions through the
    /// arena data — needed for power-vs-required comparison at deploy time.
    /// Internal access lets us avoid round-tripping through IStaticTurret.</summary>
    private bool arena_TryGetTurretTypes(Arena arena,
        out IReadOnlyDictionary<string, StaticTurretType> types)
    {
        types = null!;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return false;
        types = ad.StaticTurretTypes;
        return true;
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Despawn ALL structures in this arena. Sysop only.")]
    private void Command_StationDeployerDespawn(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null) return;
        IStationDeployer self = this;
        var structures = self.GetStructures(player.Arena);
        foreach (var s in structures) self.Despawn(player.Arena, s);
        _chat.SendMessage(player, $"Despawned {structures.Count} structures.");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Upgrade nearest friendly structure. Sysop only.")]
    private void Command_StationDeployerUpgrade(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null) return;
        if (!player.Arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        StructureInstance? nearest = null;
        long bestDsq = long.MaxValue;
        foreach (var s in ad.StationDeployerStructures)
        {
            if (s.OwnerFreq != player.Freq) continue;
            long dx = s.CenterPixelX - player.Position.X;
            long dy = s.CenterPixelY - player.Position.Y;
            long dsq = dx * dx + dy * dy;
            if (dsq < bestDsq) { bestDsq = dsq; nearest = s; }
        }
        if (nearest is null)
        { _chat.SendMessage(player, "No friendly structures to upgrade."); return; }
        if (nearest.UpgradeLevel >= StationDeployerMaxUpgradeLevel)
        { _chat.SendMessage(player, $"Already at max level ({StationDeployerMaxUpgradeLevel})."); return; }
        nearest.UpgradeLevel++;

        if (ad.StationDeployerStructureToLevelId.TryGetValue(nearest, out short lvlId))
        {
            _lvzObjects.SetImage(player.Arena, lvlId, (byte)nearest.UpgradeLevel);
            _lvzObjects.Toggle(player.Arena, lvlId, true);
        }

        _logManager.LogA(LogLevel.Info, LogCategory, player.Arena,
            $"Structure '{nearest.TypeKey}' at ({nearest.CenterPixelX},{nearest.CenterPixelY}) " +
            $"upgraded to lvl {nearest.UpgradeLevel} by {player.Name}.");
        _chat.SendMessage(player,
            $"{nearest.TypeKey} upgraded to level {nearest.UpgradeLevel}/{StationDeployerMaxUpgradeLevel}.");
        SaveStationDeployerArena(player.Arena);
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "List all structures in this arena.")]
    private void Command_StationDeployerList(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null) return;
        IStationDeployer self = this;
        var structures = self.GetStructures(player.Arena);
        if (structures.Count == 0)
        { _chat.SendMessage(player, "No structures in this arena."); return; }
        _chat.SendMessage(player, $"--- Structures ({structures.Count}) ---");
        foreach (var s in structures)
        {
            string power = s.IsPowered ? "ON" : "OFF";
            _chat.SendMessage(player,
                $"  {s.TypeKey} freq {s.OwnerFreq} owner {s.OwnerName} at " +
                $"({s.CenterPixelX >> 4},{s.CenterPixelY >> 4}) power {power} " +
                $"lvl {s.UpgradeLevel}/{StationDeployerMaxUpgradeLevel}");
        }
    }

    // -------------------------------------------------------------------------
    // KILL TRACKING
    // -------------------------------------------------------------------------

    private void OnTurretBotKilled_StationDeployer(Arena arena, string turretKey, int x, int y,
        short freq, Player? killer)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.StationDeployerStructures.Count == 0) return;

        StructureInstance? owning = null;
        foreach (var s in ad.StationDeployerStructures)
        {
            if (s.OwnerFreq != freq) continue;
            if (!StationDeployerTypes.TryGetValue(s.TypeKey, out var def)) continue;
            int dx = x - s.CenterPixelX;
            int dy = y - s.CenterPixelY;
            foreach (var slot in def.Turrets)
            {
                if (!string.Equals(slot.TurretKey, turretKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                int sdx = slot.OffsetX - dx;
                int sdy = slot.OffsetY - dy;
                if (sdx >= -StationDeployerOffsetMatchTolerancePx
                    && sdx <= StationDeployerOffsetMatchTolerancePx
                    && sdy >= -StationDeployerOffsetMatchTolerancePx
                    && sdy <= StationDeployerOffsetMatchTolerancePx)
                {
                    owning = s;
                    break;
                }
            }
            if (owning is not null) break;
        }
        if (owning is null) return;

        if (!ad.StationDeployerLiveTurretCount.TryGetValue(owning, out int live))
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"OnTurretBotKilled: missing LiveTurretCount entry. Defaulting to 1.");
            live = 1;
        }
        live--;
        ad.StationDeployerLiveTurretCount[owning] = live;

        // Drivel — fires per-turret-of-structure killed during combat
        // (potentially 9 lines per warstation kill). The follow-up
        // "Structure '...' destroyed" line stays at Info because that's
        // the once-per-structure round-significant event.
        _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
            $"Structure '{owning.TypeKey}' lost turret '{turretKey}'. Remaining: {live}.");

        if (live <= 0)
        {
            _logManager.LogA(LogLevel.Info, LogCategory, arena,
                $"Structure '{owning.TypeKey}' at " +
                $"({owning.CenterPixelX},{owning.CenterPixelY}) destroyed.");
            ((IStationDeployer)this).Despawn(arena, owning);
        }
    }

    // -------------------------------------------------------------------------
    // PERSIST
    // -------------------------------------------------------------------------

    private void SaveStationDeployerArena(Arena arena)
    {
        _stationDeployerPersistExecutor?.PutArena(arena, null);
    }

    private async Task FlushAllStationDeployerArenasAsync()
    {
        if (_stationDeployerPersistExecutor is null) return;

        var tasks = new List<Task>();
        _arenaManager.Lock();
        try
        {
            foreach (var arena in _arenaManager.Arenas)
            {
                var tcs = new TaskCompletionSource();
                _stationDeployerPersistExecutor.PutArena(arena, _ => tcs.TrySetResult());
                tasks.Add(tcs.Task);
            }
        }
        finally { _arenaManager.Unlock(); }

        if (tasks.Count > 0) await Task.WhenAll(tasks);
    }

    private bool RestoreStationDeployerStructure(Arena arena, ArenaData ad,
        StationDeployerStructureSnapshot snap)
    {
        if (_stationDeployerBroker is null) return false;
        if (!StationDeployerTypes.TryGetValue(snap.TypeKey, out var def))
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Cannot restore: unknown type '{snap.TypeKey}'. Skipped.");
            return false;
        }

        IStaticTurret? staticTurret = _stationDeployerBroker.GetInterface<IStaticTurret>();
        if (staticTurret is null)
        { _logManager.LogA(LogLevel.Warn, LogCategory, arena, "IStaticTurret not loaded."); return false; }

        try
        {
            int spawnedCount = 0;
            foreach (var slot in def.Turrets)
            {
                int sx = snap.CenterPixelX + slot.OffsetX;
                int sy = snap.CenterPixelY + slot.OffsetY;
                AddBotResult res = staticTurret.AddBot(arena, slot.TurretKey, sx, sy,
                    snap.OwnerFreq, infiniteRespawn: false, noLocationCheck: true);
                if (res == AddBotResult.Ok) spawnedCount++;
            }
            if (spawnedCount == 0)
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"Structure '{snap.TypeKey}' restore failed: 0 turrets spawned.");
                return false;
            }

            var instance = new StructureInstance
            {
                TypeKey = snap.TypeKey,
                OwnerFreq = snap.OwnerFreq,
                OwnerName = snap.OwnerName,
                CenterPixelX = snap.CenterPixelX,
                CenterPixelY = snap.CenterPixelY,
                DeployedAt = DateTime.UtcNow,
                IsPowered = false,
                UpgradeLevel = snap.UpgradeLevel,
            };
            ad.StationDeployerStructures.Add(instance);
            ad.StationDeployerLiveTurretCount[instance] = spawnedCount;

            short levelId = AllocateStationDeployerLevelSlot(ad);
            ad.StationDeployerStructureToLevelId[instance] = levelId;
            if (levelId >= StationDeployerLevelPoolStart)
            {
                short lx = (short)(snap.CenterPixelX - StationDeployerLevelIconHalfSize);
                short ly = (short)(snap.CenterPixelY + StationDeployerLevelIconOffsetY);
                _lvzObjects.SetPosition(arena, levelId, lx, ly,
                    ScreenOffset.Normal, ScreenOffset.Normal);
                if (instance.UpgradeLevel >= 1)
                {
                    _lvzObjects.SetImage(arena, levelId, (byte)instance.UpgradeLevel);
                    _lvzObjects.Toggle(arena, levelId, true);
                }
            }

            ShowStationDeployerBaseplate(arena, ad, instance);

            IPowerGrid? powerGrid = _stationDeployerBroker.GetInterface<IPowerGrid>();
            try
            {
                if (powerGrid is not null)
                {
                    instance.PowerSub = powerGrid.Subscribe(arena, snap.CenterPixelX,
                        snap.CenterPixelY, snap.OwnerFreq, (powered) =>
                    {
                        if (instance is null) return;
                        instance.IsPowered = powered;
                    });
                }
            }
            finally
            {
                if (powerGrid is not null) _stationDeployerBroker.ReleaseInterface(ref powerGrid);
            }

            return true;
        }
        finally { _stationDeployerBroker.ReleaseInterface(ref staticTurret); }
    }

    private void Persist_StationDeployer_GetData(Arena? arena, Stream outStream)
    {
        if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.StationDeployerStructures.Count == 0) return;

        using BinaryWriter w = new(outStream, Encoding.UTF8, leaveOpen: true);
        w.Write(StationDeployerPersistVersion);
        w.Write(ad.StationDeployerStructures.Count);
        foreach (var s in ad.StationDeployerStructures)
        {
            w.Write(s.TypeKey ?? string.Empty);
            w.Write(s.OwnerFreq);
            w.Write(s.OwnerName ?? string.Empty);
            w.Write(s.CenterPixelX);
            w.Write(s.CenterPixelY);
            w.Write((byte)s.UpgradeLevel);
        }
    }

    private void Persist_StationDeployer_SetData(Arena? arena, Stream inStream)
    {
        if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        using BinaryReader r = new(inStream, Encoding.UTF8, leaveOpen: true);
        byte version = r.ReadByte();
        if (version != StationDeployerPersistVersion)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Unknown persist version {version}; skipping restore.");
            return;
        }

        int count = r.ReadInt32();
        ad.StationDeployerPendingRestore.Clear();
        for (int i = 0; i < count; i++)
        {
            string typeKey = r.ReadString();
            short freq = r.ReadInt16();
            string ownerName = r.ReadString();
            int x = r.ReadInt32();
            int y = r.ReadInt32();
            byte level = r.ReadByte();
            ad.StationDeployerPendingRestore.Add(new StationDeployerStructureSnapshot
            {
                TypeKey = typeKey,
                OwnerFreq = freq,
                OwnerName = ownerName,
                CenterPixelX = x,
                CenterPixelY = y,
                UpgradeLevel = level,
            });
        }

        // Persist_SetData runs on persist worker thread; replay must happen
        // on mainloop. Also Create fires earlier than persist load (DoInit1
        // vs DoInit2), so we can't rely on it.
        if (count > 0)
        {
            _mainloop.QueueMainWorkItem(
                static state => state.self.ReplayStationDeployerPendingRestore(state.arena),
                (self: this, arena));
        }
    }

    private void Persist_StationDeployer_ClearData(Arena? arena)
    {
        if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        ad.StationDeployerPendingRestore.Clear();
    }

    // -------------------------------------------------------------------------
    // SLOT POOLS
    // -------------------------------------------------------------------------

    private short AllocateStationDeployerLevelSlot(ArenaData ad)
    {
        if (!ad.StationDeployerLevelPoolInitialized)
        {
            for (short id = StationDeployerLevelPoolEnd; id >= StationDeployerLevelPoolStart; id--)
                ad.StationDeployerFreeLevelIds.Push(id);
            ad.StationDeployerLevelPoolInitialized = true;
        }
        return ad.StationDeployerFreeLevelIds.Count > 0
            ? ad.StationDeployerFreeLevelIds.Pop() : (short)-1;
    }

    private short AllocateStationDeployerBaseplateSlot(ArenaData ad)
    {
        if (!ad.StationDeployerBaseplatePoolInitialized)
        {
            for (short id = StationDeployerBaseplatePoolEnd;
                 id >= StationDeployerBaseplatePoolStart; id--)
                ad.StationDeployerFreeBaseplateIds.Push(id);
            ad.StationDeployerBaseplatePoolInitialized = true;
        }
        return ad.StationDeployerFreeBaseplateIds.Count > 0
            ? ad.StationDeployerFreeBaseplateIds.Pop() : (short)-1;
    }

    private void ShowStationDeployerBaseplate(Arena arena, ArenaData ad, StructureInstance structure)
    {
        if (!string.Equals(structure.TypeKey, "warstation", StringComparison.OrdinalIgnoreCase))
            return;
        short id = AllocateStationDeployerBaseplateSlot(ad);
        if (id < StationDeployerBaseplatePoolStart) return;
        ad.StationDeployerStructureToBaseplateId[structure] = id;
        short bx = (short)(structure.CenterPixelX - StationDeployerBaseplateHalfSize);
        short by = (short)(structure.CenterPixelY - StationDeployerBaseplateHalfSize);
        _lvzObjects.SetPosition(arena, id, bx, by, ScreenOffset.Normal, ScreenOffset.Normal);
        _lvzObjects.SetImage(arena, id, StationDeployerBaseplateImageIndex);
        _lvzObjects.Toggle(arena, id, true);
    }

    private void HideStationDeployerBaseplate(Arena arena, ArenaData ad, StructureInstance structure)
    {
        if (!ad.StationDeployerStructureToBaseplateId.TryGetValue(structure, out short id)) return;
        _lvzObjects.Toggle(arena, id, false);
        ad.StationDeployerStructureToBaseplateId.Remove(structure);
        ad.StationDeployerFreeBaseplateIds.Push(id);
    }
}
