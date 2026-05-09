using Microsoft.Extensions.ObjectPool;
using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — ArenaDefenses subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Auto-populates contested + fortress sector arenas with hostile turrets on
// freq 9999 for combat content. Pure "stuff to shoot at" — these turrets
// don't go through Pylon / StationDeployer (those are player-driven and
// claim-attached). They respawn on every arena Create.
//
// SOURCE
// ------
// Standalone module `Modules/ArenaDefenses.cs` stays as a library copy.
// This partial preserves the exact spec layouts (mid + end arenas).
//
// 1-ARENA COLLAPSE NOTE
// ---------------------
// The Specs table currently lists `sectorwarmid` and `sectorwarend` because the
// 3-arena topology is still in place. With the 1-arena collapse, this becomes
// a single entry for `sectorwar` (or whatever the consolidated arena name is)
// with both mid + end content merged, partitioned by X-region. Phase 1 final
// will rewrite Specs once the X-region constants land in conf.
//
// SPAWN TIMING
// ------------
// `ArenaAction.Create` fires before persist load (DoInit1 vs DoInit2). Raw
// StaticTurret bots aren't persisted, so we COULD spawn directly on Create;
// the original module uses a 500ms one-shot timer to give per-arena module
// attachments (StaticTurret in particular) time to wire up. Preserved here.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: ArenaData.ArenaDefensesSpawned (per-arena bool flag).
//   - Conf keys read: NONE (specs hardcoded).
//   - Persisted data: NONE (turret bots respawn fresh on Create).
//   - Fakes registered: NONE directly — IStaticTurret owns them.
//   - Timers scheduled: One-shot 500ms timer per arena Create event.
//   - Commands registered: NONE.
//   - Broker interfaces published: NONE.
//
// CALLBACKS HOOKED (zone-wide)
//   - ArenaActionCallback → OnArenaAction_ArenaDefenses (Create-only)
//
// DEPENDS ON
//   - IStaticTurret (resolved per-spawn via broker.GetInterface; release
//     bracketed). The standalone StaticTurret currently provides this; the
//     umbrella's StaticTurret subsystem will provide it after that merge.
//
// THREADING
// ---------
// All callbacks + the timer fire on the mainloop. IStaticTurret APIs are
// mainloop-only.
//
// WAVE-FIXES PRESERVED
// --------------------
// Original Drivel-level logging on per-slot failure (avoids spamming the log
// when a turret misses placement). Try/catch around the entire spawn path so
// one bad slot can't take down the rest of the layout.
// =============================================================================

public sealed partial class SectorWar
{
    /// <summary>Hostile freq for all AI defenses. 9999 keeps us clear of any
    /// real player freq (0/1) and matches BossEncounter's convention.</summary>
    private const short ArenaDefensesFreq = 9999;

    /// <summary>Power level set for freq 9999 so RequiredPower>0 turrets fire
    /// without us having to spawn pylons. 99 covers any plausible RequiredPower
    /// (warstation_command needs 3).</summary>
    private const int ArenaDefensesPower = 99;

    /// <summary>Delay between ArenaAction.Create and the defense spawn. Lets
    /// per-arena module attachments (especially StaticTurret) finish wiring.</summary>
    private const int ArenaDefensesSpawnDelayMs = 500;

    /// <summary>Dedicated AI-fortress baseplate slot. Lives outside
    /// StationDeployer's player pool (9300..9315) so a player who deploys 16
    /// WarStations can't starve the AI's slot.</summary>
    private const short ArenaDefensesBaseplateLvzId = 9316;

    /// <summary>Image index for the warstation_baseplate (384x384) in the LVZ.</summary>
    private const byte ArenaDefensesBaseplateImageIndex = 15;

    /// <summary>Half of the 384x384 baseplate. Used to center on the fortress
    /// command core's pixel coords.</summary>
    private const int ArenaDefensesBaseplateHalfSize = 192;

    // -------------------------------------------------------------------------
    // ArenaData extension: per-arena spawn state
    // -------------------------------------------------------------------------

    internal sealed partial class ArenaData
    {
        /// <summary>True after defenses have been spawned for the current
        /// arena instance. Cleared by IResettable.TryReset so the next Create
        /// event re-spawns fresh.</summary>
        public bool ArenaDefensesSpawned;
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    /// <summary>Cached broker handle for the per-spawn IStaticTurret lookup.</summary>
    private IComponentBroker? _arenaDefensesBroker;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadArenaDefenses(IComponentBroker broker)
    {
        _arenaDefensesBroker = broker;
        ArenaActionCallback.Register(broker, OnArenaAction_ArenaDefenses);
        _logManager.LogM(LogLevel.Info, LogCategory, "ArenaDefenses subsystem loaded.");
    }

    private void UnloadArenaDefenses(IComponentBroker broker)
    {
        ArenaActionCallback.Unregister(broker, OnArenaAction_ArenaDefenses);
        _arenaDefensesBroker = null;
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH (no-ops — driven by ArenaActionCallback)
    // -------------------------------------------------------------------------

    private void AttachArenaDefenses(Arena arena) { /* zone-wide, driven by Create */ }

    /// <summary>Cancel any pending one-shot timer if the arena is detaching
    /// before the timer fires.</summary>
    private void DetachArenaDefenses(Arena arena)
    {
        _mainloopTimer.ClearTimer<Arena>(SpawnArenaDefenses, arena);
    }

    // -------------------------------------------------------------------------
    // CALLBACK
    // -------------------------------------------------------------------------

    /// <summary>
    /// On arena Create (and only Create), schedule the one-shot defense
    /// spawn. The Spawned flag prevents double-spawning if Create somehow
    /// fires twice for the same instance.
    /// </summary>
    private void OnArenaAction_ArenaDefenses(Arena arena, ArenaAction action)
    {
        if (action != ArenaAction.Create) return;
        if (arena.Name is null) return;
        if (GetArenaDefensesSpec(arena.Name) is null) return;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.ArenaDefensesSpawned) return;
        ad.ArenaDefensesSpawned = true;

        // One-shot timer — `int.MaxValue` interval means the period is
        // effectively never (ServerTimer treats as not-recurring once
        // SpawnArenaDefenses returns false). 500ms initial delay lets
        // StaticTurret finish AttachModule.
        _mainloopTimer.SetTimer<Arena>(SpawnArenaDefenses, ArenaDefensesSpawnDelayMs,
            int.MaxValue, arena, arena);
    }

    /// <summary>
    /// Timer callback. Resolves IStaticTurret, walks the spec slot list,
    /// applies SetPower for freq 9999, calls AddBot per slot, then drops a
    /// fortress baseplate if the spec specifies one. Returns false so the
    /// timer self-clears.
    /// </summary>
    private bool SpawnArenaDefenses(Arena arena)
    {
        try
        {
            if (_arenaDefensesBroker is null) return false;
            if (arena.Name is null) return false;
            ArenaDefensesDefenseSpec? spec = GetArenaDefensesSpec(arena.Name);
            if (spec is null) return false;

            IStaticTurret? staticTurret = _arenaDefensesBroker.GetInterface<IStaticTurret>();
            if (staticTurret is null)
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    "Cannot spawn AI defenses — IStaticTurret not loaded.");
                return false;
            }

            try
            {
                // SetPower BEFORE AddBot so RequiredPower>0 turrets (warstation_command)
                // see the freq's power level on their first tick.
                staticTurret.SetPower(arena, ArenaDefensesFreq, ArenaDefensesPower);

                int spawned = 0;
                int failed = 0;
                foreach (var slot in spec.Slots)
                {
                    AddBotResult res = staticTurret.AddBot(arena, slot.TurretKey,
                        slot.PixelX, slot.PixelY, ArenaDefensesFreq,
                        infiniteRespawn: false, noLocationCheck: true);
                    if (res == AddBotResult.Ok)
                    {
                        spawned++;
                    }
                    else
                    {
                        failed++;
                        // Drivel: bad-slot diagnostics for ops, NOT spammy in
                        // the production log.
                        _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
                            $"AI turret '{slot.TurretKey}' at " +
                            $"({slot.PixelX},{slot.PixelY}) failed: {res}");
                    }
                }

                _logManager.LogA(LogLevel.Info, LogCategory, arena,
                    $"AI defenses online: {spawned} turret(s) spawned, {failed} failed " +
                    $"(freq {ArenaDefensesFreq}).");
            }
            finally
            {
                _arenaDefensesBroker.ReleaseInterface(ref staticTurret);
            }

            // If the spec has a fortress center, drop the warstation baseplate
            // graphic there (same 384x384 visual as player-deployed warstations
            // get, just on the dedicated AI slot).
            if (spec.BaseplateCenter is { } center)
            {
                short lx = (short)(center.X - ArenaDefensesBaseplateHalfSize);
                short ly = (short)(center.Y - ArenaDefensesBaseplateHalfSize);
                _lvzObjects.SetPosition(arena, ArenaDefensesBaseplateLvzId, lx, ly,
                    ScreenOffset.Normal, ScreenOffset.Normal);
                _lvzObjects.SetImage(arena, ArenaDefensesBaseplateLvzId, ArenaDefensesBaseplateImageIndex);
                _lvzObjects.Toggle(arena, ArenaDefensesBaseplateLvzId, true);
                _logManager.LogA(LogLevel.Info, LogCategory, arena,
                    $"AI fortress baseplate shown at ({center.X},{center.Y}).");
            }
        }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Error, LogCategory, arena,
                $"Defense spawn threw: {ex}");
        }
        return false;  // one-shot
    }

    // -------------------------------------------------------------------------
    // SPEC LOOKUP
    // -------------------------------------------------------------------------

    /// <summary>Returns the defense layout for the given arena, or null if
    /// the arena has no AI defenses configured.</summary>
    private static ArenaDefensesDefenseSpec? GetArenaDefensesSpec(string arenaName)
    {
        foreach (var s in ArenaDefensesSpecs)
            if (string.Equals(s.ArenaName, arenaName, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    // -------------------------------------------------------------------------
    // HARDCODED SPECS
    //
    // Tile-to-pixel: pixelX = tileX * 16. Map is 1024x1024 tiles; center is
    // (8192, 8192) px. Coords below are in pixels.
    //
    // sectorwarmid = "contested zone" — moderate AI presence covering map
    // body, easier perimeter so players can enter and find a foothold.
    //
    // sectorwarend = "AI fortress" — heavy multi-ring defense including the
    // 9-turret WarStation formation around the central command core. Visible
    // baseplate shown at the fortress center to match player-deployed
    // WarStation visual identity.
    // -------------------------------------------------------------------------

    private static readonly ArenaDefensesDefenseSpec[] ArenaDefensesSpecs =
    {
        new("sectorwarmid", null, new ArenaDefensesTurretSlot[]
        {
            new("outpost_frigate", 8192, 8192),
            // Inner ring of guns at ~25 tiles from center (400 px).
            new("outpost_gun", 6592, 6592),
            new("outpost_gun", 9792, 6592),
            new("outpost_gun", 6592, 9792),
            new("outpost_gun", 9792, 9792),
            new("outpost_gun", 8192, 6192),
            new("outpost_gun", 8192, 10192),
            // Outer perimeter warstation guns at ~80 tiles from center (1280 px).
            new("warstation_gun", 8192, 5600),
            new("warstation_gun", 8192, 10784),
            new("warstation_gun", 5600, 8192),
            new("warstation_gun", 10784, 8192),
        }),
        new("sectorwarend", (8192, 8192), new ArenaDefensesTurretSlot[]
        {
            // Central command core (warstation_command — RequiredPower=3 covered
            // by SetPower(99) above).
            new("warstation_command", 8192, 8192),
            // 8-point octagon of warstation guns at radius ~144 px (the same
            // formation a player-deployed WarStation uses).
            new("warstation_gun", 8192, 8048),
            new("warstation_gun", 8294, 8090),
            new("warstation_gun", 8336, 8192),
            new("warstation_gun", 8294, 8294),
            new("warstation_gun", 8192, 8336),
            new("warstation_gun", 8090, 8294),
            new("warstation_gun", 8048, 8192),
            new("warstation_gun", 8090, 8090),
            // Mid-radius ring of frigates at ~50 tiles (800 px).
            new("outpost_frigate", 8192, 7392),
            new("outpost_frigate", 8192, 8992),
            new("outpost_frigate", 7392, 8192),
            new("outpost_frigate", 8992, 8192),
            // Outer perimeter outpost guns at ~100 tiles (1600 px), 8 around.
            new("outpost_gun", 8192, 6592),
            new("outpost_gun", 8192, 9792),
            new("outpost_gun", 6592, 8192),
            new("outpost_gun", 9792, 8192),
            new("outpost_gun", 7060, 7060),
            new("outpost_gun", 9324, 7060),
            new("outpost_gun", 7060, 9324),
            new("outpost_gun", 9324, 9324),
        }),
    };

    /// <summary>Per-arena defense layout. <c>BaseplateCenter</c> nullable —
    /// arenas without a fortress visual omit the baseplate.</summary>
    private sealed record ArenaDefensesDefenseSpec(
        string ArenaName,
        (int X, int Y)? BaseplateCenter,
        ArenaDefensesTurretSlot[] Slots);

    /// <summary>One AI turret slot. <c>TurretKey</c> matches an entry in
    /// the structures.conf turret-type table; <c>PixelX/Y</c> are world
    /// pixels.</summary>
    private sealed record ArenaDefensesTurretSlot(string TurretKey, int PixelX, int PixelY);
}
