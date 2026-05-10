using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — HQ subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Per-team headquarters auto-spawned at arena attach. Each freq (0 = left,
// 1 = right) gets:
//   - A multi-turret formation (4 perimeter guns + 1 command core for now;
//     the slot table in HqDefinitions is the only place that needs to grow
//     to add more defenders).
//   - One stationary "patrolling" capital ship that discretely teleports
//     between four corners of the HQ footprint when no enemy is nearby, then
//     stops warping and lets StaticTurret's built-in AI engage when an enemy
//     enters its sight range.
// HQ defenders use RequiredPower = 0 (set via the per-type structures.conf
// section) so they DON'T depend on the player-deployed pylon network — they
// always operate.
//
// PATROL STATE MACHINE (capital)
// ------------------------------
//   PATROL: every HqCapitalPatrolPeriodMs (jittered ±20%), if no enemy player
//           is within HqCapitalEngageHoldPixels of the capital's current
//           corner, play warp-out → IStaticTurret.RemoveBotAt(current) →
//           IStaticTurret.AddBot(next corner) → play warp-in. Otherwise the
//           capital stays put and StaticTurret's AI handles combat.
//   DEAD:   on BotKilled, mark as dead, schedule respawn after
//           HqCapitalRespawnDelaySeconds at HQ center (corner 0), then
//           resume PATROL.
//
// This partial intentionally does NOT replicate StaticTurret's target-
// acquisition / fire AI. The capital IS a static turret; we only orchestrate
// when it teleports.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: per-arena `HqArenaState` (in ArenaData).
//   - Conf keys read: [SectorWar] HqEnabled / HqCapital* (see
//     docs/ARENA_SETTINGS.md). Per-turret-type stats live in
//     [staticturret_hq_*] sections owned by StaticTurret.
//   - Persisted data: NONE — HQs are arena-wide fixtures, not player-deployed.
//   - Fakes registered: yes, indirectly via IStaticTurret.AddBot.
//   - Timers scheduled: HqTickIntervalMs mainloop tick (drives patrol +
//     respawn checks). Cancelled in UnloadHq.
//   - Broker interfaces published: NONE.
//
// CALLBACKS HOOKED:
//   - ArenaActionCallback (via AttachHq) — spawn on Create, despawn on Destroy.
//   - IStaticTurret.BotKilled (zone-wide subscription in LoadHq) — to detect
//     capital death and trigger respawn.
//
// THREADING
// ---------
// Mainloop only. The mainloop timer + ArenaAction callback all fire on the
// mainloop. BotKilled also fires on the mainloop per IStaticTurret's
// contract.
// =============================================================================

[ConfigHelp<bool>("SectorWar", "HqEnabled", ConfigScope.Arena,
    Default = true,
    Description = "Auto-spawn the per-team HQ formation on arena Create. 0 disables HQs entirely.")]
[ConfigHelp<int>("SectorWar", "HqCapitalPatrolPeriodMs", ConfigScope.Arena,
    Default = 10000, Min = 1000, Max = 99999,
    Description = "Base interval (ms) between patrolling-capital teleports. Jittered ±20% per warp to keep capitals out of sync.")]
[ConfigHelp<int>("SectorWar", "HqCapitalEngageHoldPixels", ConfigScope.Arena,
    Default = 1024, Min = 64, Max = 16383,
    Description = "If any enemy player is within this radius of the capital, the patrol tick is suppressed so the capital stays put for combat.")]
[ConfigHelp<int>("SectorWar", "HqCapitalRespawnDelaySeconds", ConfigScope.Arena,
    Default = 60, Min = 1, Max = 9999,
    Description = "Seconds after a capital is killed before it respawns at HQ center (corner 0).")]
public sealed partial class SectorWar
{
    // -------------------------------------------------------------------------
    // CONSTANTS
    // -------------------------------------------------------------------------

    /// <summary>Mainloop tick cadence for the HQ patrol/respawn driver.
    /// Patrol cadence is much slower than this (seconds), so 1 Hz is plenty.</summary>
    private const int HqTickIntervalMs = 1000;

    /// <summary>How long after the last damage hit on a capital its patrol
    /// stays suppressed. Lets a battle play out instead of the capital
    /// warping out mid-fight. 8s = covers the typical patrol window so the
    /// capital won't slip away between volleys.</summary>
    private const int HqCapitalDamageHoldMs = 8000;

    // ── HQ baseplate LVZ ────────────────────────────────────────────────────
    // Subsystem allocates one slot per HQ at AttachHq, returns it at DetachHq.
    // Image is 512×512 — half-size = 256 is subtracted from HQ center to
    // compute LVZ top-left (LVZ map objects anchor top-left, see
    // SectorWar.StationDeployer.cs's baseplate code).
    //
    // Pool moved from 9316..9331 to 9332..9347 because slot 9316 is owned by
    // ArenaDefenses for the AI fortress baseplate (see
    // SectorWar.ArenaDefenses.cs:89). Duplicate object IDs in the LVZ caused
    // both baseplates to render at HQ coords.
    private const short HqBaseplatePoolStart = 9332;
    private const short HqBaseplatePoolEnd = 9347;
    private const int HqBaseplateHalfSize = 256;

    /// <summary>Turret-type keys registered in structures.conf as
    /// [staticturret_hq_perimeter_gun] / [staticturret_hq_command] /
    /// [staticturret_hq_capital]. If any of these sections are missing,
    /// IStaticTurret.AddBot returns UnknownType and the spawn step logs +
    /// continues — partial HQs are tolerable, no-throw is required.</summary>
    private const string HqPerimeterGunKey = "hq_perimeter_gun";
    private const string HqCommandKey = "hq_command";
    private const string HqCapitalKey = "hq_capital";

    // -------------------------------------------------------------------------
    // HQ DEFINITION TABLE
    //
    // One entry per playable freq. Center coords come from arena.conf
    // [Spawn] TeamN-X / TeamN-Y, multiplied to pixels. The slot table +
    // capital corner pattern are hardcoded.
    // -------------------------------------------------------------------------

    private sealed class HqTurretSlot
    {
        public required string TurretKey;
        public int OffsetX;
        public int OffsetY;
    }

    private sealed class HqDefinition
    {
        public required short Freq;
        /// <summary>Slot offsets (in pixels) for non-capital defenders.
        /// Each slot is a fresh AddBot call relative to HQ center.</summary>
        public required HqTurretSlot[] Defenders;
        /// <summary>Four corner offsets (in pixels) the patrolling capital
        /// cycles through. Index 0 is also the respawn position.</summary>
        public required (int X, int Y)[] CapitalCorners;
    }

    private static readonly HqDefinition[] _hqDefinitions =
    {
        // Freq 0 — left team. 4 perimeter guns at the corners of a 192-px
        // square, 1 command core at center. The capital patrols a 192-px
        // square OUTSIDE the gun ring (offset ±256) so it doesn't spawn on
        // top of the command core.
        new HqDefinition
        {
            Freq = 0,
            Defenders = new[]
            {
                new HqTurretSlot { TurretKey = HqPerimeterGunKey, OffsetX = -96, OffsetY = -96 },
                new HqTurretSlot { TurretKey = HqPerimeterGunKey, OffsetX =  96, OffsetY = -96 },
                new HqTurretSlot { TurretKey = HqPerimeterGunKey, OffsetX = -96, OffsetY =  96 },
                new HqTurretSlot { TurretKey = HqPerimeterGunKey, OffsetX =  96, OffsetY =  96 },
                new HqTurretSlot { TurretKey = HqCommandKey,      OffsetX =   0, OffsetY =   0 },
            },
            CapitalCorners = new (int, int)[]
            {
                (-256, -256),
                ( 256, -256),
                ( 256,  256),
                (-256,  256),
            },
        },

        // Freq 1 — right team. Same shape as freq 0; the per-team center
        // comes from arena.conf [Spawn] coords at attach time, so the only
        // thing that differs is the freq number.
        new HqDefinition
        {
            Freq = 1,
            Defenders = new[]
            {
                new HqTurretSlot { TurretKey = HqPerimeterGunKey, OffsetX = -96, OffsetY = -96 },
                new HqTurretSlot { TurretKey = HqPerimeterGunKey, OffsetX =  96, OffsetY = -96 },
                new HqTurretSlot { TurretKey = HqPerimeterGunKey, OffsetX = -96, OffsetY =  96 },
                new HqTurretSlot { TurretKey = HqPerimeterGunKey, OffsetX =  96, OffsetY =  96 },
                new HqTurretSlot { TurretKey = HqCommandKey,      OffsetX =   0, OffsetY =   0 },
            },
            CapitalCorners = new (int, int)[]
            {
                (-256, -256),
                ( 256, -256),
                ( 256,  256),
                (-256,  256),
            },
        },
    };

    // -------------------------------------------------------------------------
    // PER-ARENA STATE
    // -------------------------------------------------------------------------

    /// <summary>Live patrol state for one HQ's capital. Lives inside
    /// HqArenaState. Created on spawn, mutated only on the mainloop.</summary>
    internal sealed class HqCapitalRuntime
    {
        public required short Freq;
        public required int CenterPixelX;
        public required int CenterPixelY;
        public required (int X, int Y)[] Corners;
        /// <summary>Index into Corners[] of where the capital currently sits.</summary>
        public int CornerIndex;
        /// <summary>Mainloop time (ms) of the last successful warp. Compared
        /// against HqCapitalPatrolPeriodMs (jittered) for the next decision.</summary>
        public int LastWarpTickMs;
        /// <summary>Jitter applied to the next patrol period — set fresh on
        /// each warp so capitals don't sync up.</summary>
        public int CurrentPeriodMs;
        /// <summary>True when the capital fake exists in StaticTurret. False
        /// after BotKilled until DeadRespawnAtTickMs has passed.</summary>
        public bool Alive;
        /// <summary>Mainloop time (ms) at which a dead capital should respawn.
        /// Ignored when Alive=true.</summary>
        public int DeadRespawnAtTickMs;
        /// <summary>Mainloop time (ms) of the last perimeter-gun / command
        /// defender kill on THIS HQ. Drives the HqHud DMG icon (amber state)
        /// for a short window after a defender dies. 0 = never.</summary>
        public int LastDefenderDeathTickMs;
        /// <summary>Mainloop time (ms) of the last damage hit on the capital
        /// itself. Drives the patrol-hold: the capital does NOT teleport
        /// while it's under sustained fire (HqCapitalDamageHoldMs). 0 = never.</summary>
        public int LastCapitalDamageTickMs;
    }

    internal sealed class HqArenaState
    {
        public bool Enabled;
        public int PatrolPeriodMs;
        public int EngageHoldPixels;
        public int RespawnDelaySeconds;
        public List<HqCapitalRuntime> Capitals = new();
        /// <summary>LVZ baseplate slot IDs allocated for each spawned HQ.
        /// Index parallels the freq order from _hqDefinitions. Cleared on
        /// detach (slots toggled off + returned to a free pool).</summary>
        public List<short> BaseplateIds = new();
        public Stack<short> BaseplateFreePool = new();
        public bool BaseplatePoolInitialized;
    }

    internal sealed partial class ArenaData
    {
        public HqArenaState? HqArenaState;
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private IComponentBroker? _hqBroker;
    private IStaticTurret? _hqStaticTurretForKills;
    private readonly Random _hqJitterRng = new();

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD
    // -------------------------------------------------------------------------

    private void LoadHq(IComponentBroker broker)
    {
        _hqBroker = broker;

        // Subscribe to BotKilled zone-wide so we can detect capital deaths
        // and schedule respawns. The handler matches by (freq, position) so
        // it ignores other modules' turret kills.
        _hqStaticTurretForKills = broker.GetInterface<IStaticTurret>();
        if (_hqStaticTurretForKills is not null)
        {
            _hqStaticTurretForKills.BotKilled += OnBotKilled_Hq;
            _hqStaticTurretForKills.BotDamaged += OnBotDamaged_Hq;
        }

        _mainloopTimer.SetTimer(OnTick_Hq, HqTickIntervalMs, HqTickIntervalMs, this);

        _logManager.LogM(LogLevel.Info, LogCategory, "Hq subsystem loaded.");
    }

    private void UnloadHq(IComponentBroker broker)
    {
        _mainloopTimer.ClearTimer(OnTick_Hq, this);

        if (_hqStaticTurretForKills is not null)
        {
            _hqStaticTurretForKills.BotKilled -= OnBotKilled_Hq;
            _hqStaticTurretForKills.BotDamaged -= OnBotDamaged_Hq;
            broker.ReleaseInterface(ref _hqStaticTurretForKills);
        }

        _hqBroker = null;
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    //
    // Mirror BossEncounter: register an ArenaAction callback. The actual HQ
    // spawn happens on ArenaAction.Create (after the map is loaded) — not
    // on AttachModule, because at AttachModule time the arena's map data
    // may not be ready.
    // -------------------------------------------------------------------------

    private void AttachHq(Arena arena)
    {
        ArenaActionCallback.Register(arena, OnArenaAction_Hq);
    }

    private void DetachHq(Arena arena)
    {
        ArenaActionCallback.Unregister(arena, OnArenaAction_Hq);
        DespawnHqArena(arena);
    }

    private void OnArenaAction_Hq(Arena arena, ArenaAction action)
    {
        switch (action)
        {
            case ArenaAction.Create: TrySpawnHqArena(arena); break;
            case ArenaAction.Destroy: DespawnHqArena(arena); break;
            case ArenaAction.ConfChanged: ReloadHqConf(arena); break;
        }
    }

    // -------------------------------------------------------------------------
    // CONF READ
    // -------------------------------------------------------------------------

    /// <summary>Pulls the current [SectorWar] Hq* values into HqArenaState.
    /// Called both at spawn time and on ConfChanged so admins can ?quickfix
    /// HqCapitalPatrolPeriodMs etc. without restarting.</summary>
    private HqArenaState ReadHqConf(Arena arena)
    {
        var cfg = arena.Cfg!;
        return new HqArenaState
        {
            Enabled = _configManager.GetInt(cfg, ConfSection, "HqEnabled", 1) != 0,
            PatrolPeriodMs = Math.Max(1000,
                _configManager.GetInt(cfg, ConfSection, "HqCapitalPatrolPeriodMs", 10000)),
            EngageHoldPixels = Math.Clamp(
                _configManager.GetInt(cfg, ConfSection, "HqCapitalEngageHoldPixels", 1024),
                64, 16383),
            RespawnDelaySeconds = Math.Max(1,
                _configManager.GetInt(cfg, ConfSection, "HqCapitalRespawnDelaySeconds", 60)),
        };
    }

    private void ReloadHqConf(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.HqArenaState is null) return;
        var fresh = ReadHqConf(arena);
        ad.HqArenaState.PatrolPeriodMs = fresh.PatrolPeriodMs;
        ad.HqArenaState.EngageHoldPixels = fresh.EngageHoldPixels;
        ad.HqArenaState.RespawnDelaySeconds = fresh.RespawnDelaySeconds;
        // Enabled flip is a bigger ask (would require live spawn/despawn);
        // require a recyclezone for that for now.
    }

    // -------------------------------------------------------------------------
    // SPAWN
    // -------------------------------------------------------------------------

    private void TrySpawnHqArena(Arena arena)
    {
        if (_hqBroker is null) return;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.HqArenaState is not null) return;  // already spawned

        var state = ReadHqConf(arena);
        if (!state.Enabled)
        {
            _logManager.LogA(LogLevel.Info, LogCategory, arena, "HQs disabled by HqEnabled=0.");
            ad.HqArenaState = state;
            return;
        }

        IStaticTurret? staticTurret = _hqBroker.GetInterface<IStaticTurret>();
        if (staticTurret is null)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                "HQ spawn skipped — IStaticTurret unavailable.");
            return;
        }

        try
        {
            var cfg = arena.Cfg!;
            int totalSpawned = 0;

            // Initialize the baseplate slot pool once per state.
            if (!state.BaseplatePoolInitialized)
            {
                for (short id = HqBaseplatePoolEnd; id >= HqBaseplatePoolStart; id--)
                    state.BaseplateFreePool.Push(id);
                state.BaseplatePoolInitialized = true;
            }

            foreach (HqDefinition def in _hqDefinitions)
            {
                // [Spawn] Team{N}-X / Team{N}-Y are tile coords (0..1023).
                // The hyphenated form is REQUIRED — SS.NET's ClientSettings
                // reads `Team0-X` (with hyphen) for the client-settings packet
                // that drives Continuum's spawn picker. The legacy Subgame
                // `Team0X` form is NOT translated by SubgameCompatibility, so
                // using it leaves spawn coords at default 0 and players spawn
                // at the map origin. (tile << 4) + 8 puts us at tile-center.
                int teamTileX = _configManager.GetInt(cfg, "Spawn", $"Team{def.Freq}-X", 512);
                int teamTileY = _configManager.GetInt(cfg, "Spawn", $"Team{def.Freq}-Y", 512);
                int centerPx = (teamTileX << 4) + 8;
                int centerPy = (teamTileY << 4) + 8;

                ShowHqBaseplate(arena, state, centerPx, centerPy);

                int spawned = SpawnHqDefenders(arena, staticTurret, def, centerPx, centerPy);
                totalSpawned += spawned;

                HqCapitalRuntime? capital = SpawnHqCapital(arena, staticTurret, def, centerPx, centerPy);
                if (capital is not null)
                    state.Capitals.Add(capital);
            }

            ad.HqArenaState = state;
            _logManager.LogA(LogLevel.Info, LogCategory, arena,
                $"HQ deployed: {totalSpawned} defenders + {state.Capitals.Count} capital(s) " +
                $"across {_hqDefinitions.Length} freq(s).");
        }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Error, LogCategory, arena,
                $"HQ spawn threw: {ex}. Partial state may exist; ?wipearena to clean up.");
        }
        finally
        {
            _hqBroker.ReleaseInterface(ref staticTurret);
        }
    }

    /// <summary>Iterate the slot table, AddBot per slot. RequiredPower=0 in
    /// the [staticturret_hq_*] sections means freq power doesn't matter, so
    /// we skip SetPower here.</summary>
    private int SpawnHqDefenders(Arena arena, IStaticTurret staticTurret,
        HqDefinition def, int centerPx, int centerPy)
    {
        int spawned = 0;
        foreach (HqTurretSlot slot in def.Defenders)
        {
            int sx = centerPx + slot.OffsetX;
            int sy = centerPy + slot.OffsetY;
            AddBotResult res = staticTurret.AddBot(arena, slot.TurretKey, sx, sy, def.Freq,
                infiniteRespawn: true, noLocationCheck: true);
            if (res == AddBotResult.Ok) spawned++;
            else
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"HQ defender '{slot.TurretKey}' freq {def.Freq} at ({sx},{sy}) failed: {res}.");
            }
        }
        return spawned;
    }

    private HqCapitalRuntime? SpawnHqCapital(Arena arena, IStaticTurret staticTurret,
        HqDefinition def, int centerPx, int centerPy)
    {
        int cornerPx = centerPx + def.CapitalCorners[0].X;
        int cornerPy = centerPy + def.CapitalCorners[0].Y;
        AddBotResult res = staticTurret.AddBot(arena, HqCapitalKey, cornerPx, cornerPy, def.Freq,
            infiniteRespawn: false, noLocationCheck: true);
        if (res != AddBotResult.Ok)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"HQ capital freq {def.Freq} at ({cornerPx},{cornerPy}) failed: {res}.");
            return null;
        }

        return new HqCapitalRuntime
        {
            Freq = def.Freq,
            CenterPixelX = centerPx,
            CenterPixelY = centerPy,
            Corners = def.CapitalCorners,
            CornerIndex = 0,
            LastWarpTickMs = Environment.TickCount,
            CurrentPeriodMs = JitterPatrolPeriod(10000),
            Alive = true,
        };
    }

    // -------------------------------------------------------------------------
    // DESPAWN
    // -------------------------------------------------------------------------

    private void DespawnHqArena(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.HqArenaState is null) return;
        if (_hqBroker is null) { ad.HqArenaState = null; return; }

        IStaticTurret? staticTurret = _hqBroker.GetInterface<IStaticTurret>();
        if (staticTurret is null) { ad.HqArenaState = null; return; }

        try
        {
            // Defender bots are the simplest path: nuke them per-position via
            // RemoveBotAt. Capital is a single bot per HQ at the current corner.
            foreach (HqDefinition def in _hqDefinitions)
            {
                var cfg = arena.Cfg!;
                int teamTileX = _configManager.GetInt(cfg, "Spawn", $"Team{def.Freq}-X", 512);
                int teamTileY = _configManager.GetInt(cfg, "Spawn", $"Team{def.Freq}-Y", 512);
                int centerPx = (teamTileX << 4) + 8;
                int centerPy = (teamTileY << 4) + 8;

                foreach (HqTurretSlot slot in def.Defenders)
                {
                    int sx = centerPx + slot.OffsetX;
                    int sy = centerPy + slot.OffsetY;
                    try { staticTurret.RemoveBotAt(arena, sx, sy, def.Freq, slot.TurretKey); }
                    catch { /* best-effort despawn; phong's no-crash rule */ }
                }
            }

            foreach (HqCapitalRuntime cap in ad.HqArenaState.Capitals)
            {
                if (!cap.Alive) continue;
                int cx = cap.CenterPixelX + cap.Corners[cap.CornerIndex].X;
                int cy = cap.CenterPixelY + cap.Corners[cap.CornerIndex].Y;
                try { staticTurret.RemoveBotAt(arena, cx, cy, cap.Freq, HqCapitalKey); }
                catch { /* best-effort */ }
            }

            // Toggle off + recycle baseplate LVZ slots.
            foreach (short bid in ad.HqArenaState.BaseplateIds)
            {
                try { _lvzObjects.Toggle(arena, bid, false); }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            _hqBroker.ReleaseInterface(ref staticTurret);
            ad.HqArenaState = null;
        }
    }

    // -------------------------------------------------------------------------
    // BASEPLATE
    // -------------------------------------------------------------------------

    /// <summary>Allocate one LVZ baseplate slot, position it under the HQ
    /// center, toggle on. Slot ID is appended to state.BaseplateIds so
    /// despawn can clean up. Silent no-op if the pool is exhausted.</summary>
    private void ShowHqBaseplate(Arena arena, HqArenaState state, int centerPx, int centerPy)
    {
        if (state.BaseplateFreePool.Count == 0)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                "HQ baseplate pool exhausted; new HQ will render without a floor.");
            return;
        }

        short slotId = state.BaseplateFreePool.Pop();
        // LVZ map objects anchor top-left — subtract half-size to center the
        // 512×512 baseplate on HQ center.
        short anchorX = (short)(centerPx - HqBaseplateHalfSize);
        short anchorY = (short)(centerPy - HqBaseplateHalfSize);
        try
        {
            _lvzObjects.SetPosition(arena, slotId, anchorX, anchorY,
                ScreenOffset.Normal, ScreenOffset.Normal);
            _lvzObjects.Toggle(arena, slotId, true);
            state.BaseplateIds.Add(slotId);
        }
        catch (Exception ex)
        {
            // Return the slot if SetPosition/Toggle threw — don't leak the pool.
            state.BaseplateFreePool.Push(slotId);
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"HQ baseplate display failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // PATROL / RESPAWN TICK
    // -------------------------------------------------------------------------

    private bool OnTick_Hq()
    {
        if (_hqBroker is null) return true;
        IStaticTurret? staticTurret = _hqBroker.GetInterface<IStaticTurret>();
        if (staticTurret is null) return true;

        try
        {
            int nowMs = Environment.TickCount;

            _arenaManager.Lock();
            try
            {
                foreach (Arena arena in _arenaManager.Arenas)
                {
                    if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) continue;
                    if (ad.HqArenaState is null) continue;
                    if (!ad.HqArenaState.Enabled) continue;

                    foreach (HqCapitalRuntime cap in ad.HqArenaState.Capitals)
                    {
                        if (!cap.Alive)
                        {
                            // Respawn after delay.
                            if (nowMs - cap.DeadRespawnAtTickMs >= 0)
                                RespawnHqCapital(arena, staticTurret, cap, nowMs);
                            continue;
                        }

                        // Suppress patrol if any enemy player / hostile turret
                        // bot is within hold range — the capital holds position
                        // for combat.
                        if (EnemyWithinRange(arena, cap, ad.HqArenaState.EngageHoldPixels))
                            continue;

                        // Suppress patrol if the capital was recently hit even
                        // if no enemy is within EngageHoldPixels right now (e.g.
                        // a long-range warstation is shelling from outside the
                        // proximity ring). Lets fights actually resolve.
                        if (cap.LastCapitalDamageTickMs != 0
                            && (uint)(nowMs - cap.LastCapitalDamageTickMs) < HqCapitalDamageHoldMs)
                            continue;

                        // Time for next warp?
                        int elapsed = nowMs - cap.LastWarpTickMs;
                        if (elapsed < cap.CurrentPeriodMs) continue;

                        WarpHqCapitalToNextCorner(arena, staticTurret, cap, nowMs,
                            ad.HqArenaState.PatrolPeriodMs);
                    }
                }
            }
            finally
            {
                _arenaManager.Unlock();
            }
        }
        finally
        {
            _hqBroker.ReleaseInterface(ref staticTurret);
        }
        return true;
    }

    /// <summary>True iff any hostile player or hostile static-turret bot is
    /// within `range` pixels of the capital's current corner. Used to gate
    /// the teleport tick — capital holds position for combat.
    ///
    /// Fakes ARE counted (other freq's warstations / HQ defenders are valid
    /// "engagement nearby" signals); only same-freq players + spectators are
    /// skipped.</summary>
    private bool EnemyWithinRange(Arena arena, HqCapitalRuntime cap, int rangePx)
    {
        int capX = cap.CenterPixelX + cap.Corners[cap.CornerIndex].X;
        int capY = cap.CenterPixelY + cap.Corners[cap.CornerIndex].Y;
        int rangeSq = rangePx * rangePx;

        _playerData.Lock();
        try
        {
            foreach (Player p in _playerData.Players)
            {
                if (p.Arena != arena) continue;
                if (p.Ship == ShipType.Spec) continue;
                if (p.Freq == cap.Freq) continue;

                int dx = p.Position.X - capX;
                int dy = p.Position.Y - capY;
                if ((long)dx * dx + (long)dy * dy <= rangeSq)
                    return true;
            }
        }
        finally
        {
            _playerData.Unlock();
        }
        return false;
    }

    private void WarpHqCapitalToNextCorner(Arena arena, IStaticTurret staticTurret,
        HqCapitalRuntime cap, int nowMs, int basePeriodMs)
    {
        int oldCx = cap.CenterPixelX + cap.Corners[cap.CornerIndex].X;
        int oldCy = cap.CenterPixelY + cap.Corners[cap.CornerIndex].Y;

        int nextIndex = (cap.CornerIndex + 1) % cap.Corners.Length;
        int newCx = cap.CenterPixelX + cap.Corners[nextIndex].X;
        int newCy = cap.CenterPixelY + cap.Corners[nextIndex].Y;

        // Warp-out flash at the old position.
        IWarpInEffect? warpIn = _hqBroker?.GetInterface<IWarpInEffect>();
        try { warpIn?.Play(arena, oldCx, oldCy, 600, WarpInFlavor.FortressRed); }
        finally { if (warpIn is not null) _hqBroker?.ReleaseInterface(ref warpIn); }

        // MoveBot updates the turret's internal coords + broadcasts a fresh
        // position packet WITHOUT destroying/recreating the fake-player. The
        // ~HQ entry stays in F2 the whole time; clients just see the ship
        // sprite teleport to the new corner.
        bool moved = staticTurret.MoveBot(arena, oldCx, oldCy, cap.Freq, HqCapitalKey,
            newCx, newCy);
        if (!moved)
        {
            // Bot record vanished from StaticTurret (probably killed between
            // ticks but BotKilled hasn't reached us yet). Mark dead, let the
            // respawn timer handle it.
            _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
                $"HQ capital freq {cap.Freq} not at expected corner — assuming killed.");
            cap.Alive = false;
            cap.DeadRespawnAtTickMs = nowMs + 1000;
            return;
        }

        cap.CornerIndex = nextIndex;

        // Warp-in flash at the new position.
        warpIn = _hqBroker?.GetInterface<IWarpInEffect>();
        try { warpIn?.Play(arena, newCx, newCy, 600, WarpInFlavor.FortressRed); }
        finally { if (warpIn is not null) _hqBroker?.ReleaseInterface(ref warpIn); }

        cap.LastWarpTickMs = nowMs;
        cap.CurrentPeriodMs = JitterPatrolPeriod(basePeriodMs);

        _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
            $"HQ capital freq {cap.Freq} warped to corner {cap.CornerIndex} ({newCx},{newCy}).");
    }

    private void RespawnHqCapital(Arena arena, IStaticTurret staticTurret,
        HqCapitalRuntime cap, int nowMs)
    {
        cap.CornerIndex = 0;
        int cx = cap.CenterPixelX + cap.Corners[0].X;
        int cy = cap.CenterPixelY + cap.Corners[0].Y;
        AddBotResult res = staticTurret.AddBot(arena, HqCapitalKey, cx, cy, cap.Freq,
            infiniteRespawn: false, noLocationCheck: true);
        if (res != AddBotResult.Ok)
        {
            // Try again next tick.
            cap.DeadRespawnAtTickMs = nowMs + 1000;
            return;
        }

        cap.Alive = true;
        cap.LastWarpTickMs = nowMs;
        cap.CurrentPeriodMs = JitterPatrolPeriod(10000);

        // Capital is back — unfreeze defender respawns so the HQ can rebuild
        // its perimeter. The freeze was set when the capital died (so
        // attackers had a clean window to clear defenders). With capital
        // back, defenders should resume their normal 3s/6s respawn cadence.
        staticTurret.FreezeRespawn(arena, cap.Freq, false);

        IWarpInEffect? warpIn = _hqBroker?.GetInterface<IWarpInEffect>();
        try { warpIn?.Play(arena, cx, cy, 1200, WarpInFlavor.FortressRed); }
        finally { if (warpIn is not null) _hqBroker?.ReleaseInterface(ref warpIn); }

        _logManager.LogA(LogLevel.Info, LogCategory, arena,
            $"HQ capital freq {cap.Freq} respawned at corner 0.");
    }

    // -------------------------------------------------------------------------
    // BotKilled — capital death handler
    //
    // Subscribed zone-wide in LoadHq. Every static-turret bot kill in any
    // arena routes here; we filter by turretKey="hq_capital" + match against
    // each arena's HqArenaState to find which capital died.
    // -------------------------------------------------------------------------

    /// <summary>Stamp LastCapitalDamageTickMs on the matching capital so its
    /// next patrol tick is suppressed for HqCapitalDamageHoldMs. Lets ongoing
    /// fights resolve instead of the capital warping out mid-volley.</summary>
    private void OnBotDamaged_Hq(Arena arena, string turretKey, int pixelX, int pixelY,
        short freq, Player? firedBy)
    {
        if (!string.Equals(turretKey, HqCapitalKey, StringComparison.OrdinalIgnoreCase))
            return;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.HqArenaState is null) return;

        int now = Environment.TickCount;
        foreach (HqCapitalRuntime cap in ad.HqArenaState.Capitals)
        {
            if (cap.Freq != freq) continue;
            int cx = cap.CenterPixelX + cap.Corners[cap.CornerIndex].X;
            int cy = cap.CenterPixelY + cap.Corners[cap.CornerIndex].Y;
            if (Math.Abs(pixelX - cx) > 32 || Math.Abs(pixelY - cy) > 32) continue;
            cap.LastCapitalDamageTickMs = now;
            return;
        }
    }

    private void OnBotKilled_Hq(Arena arena, string turretKey, int pixelX, int pixelY,
        short freq, Player? killer)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.HqArenaState is null) return;

        // Defender kill (perimeter gun / command core) — stamp the freq's
        // capital so HqHud can light the amber DMG icon for a short window,
        // then re-check the sudden-death gate: if the capital is already
        // dead and this was the last surviving defender, end the round.
        if (string.Equals(turretKey, HqPerimeterGunKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(turretKey, HqCommandKey, StringComparison.OrdinalIgnoreCase))
        {
            int now = Environment.TickCount;
            foreach (HqCapitalRuntime cap in ad.HqArenaState.Capitals)
            {
                if (cap.Freq == freq) cap.LastDefenderDeathTickMs = now;
            }
            CheckSuddenDeathGate_Hq(arena, ad, freq);
            return;
        }

        if (!string.Equals(turretKey, HqCapitalKey, StringComparison.OrdinalIgnoreCase))
            return;

        foreach (HqCapitalRuntime cap in ad.HqArenaState.Capitals)
        {
            if (cap.Freq != freq) continue;
            int cx = cap.CenterPixelX + cap.Corners[cap.CornerIndex].X;
            int cy = cap.CenterPixelY + cap.Corners[cap.CornerIndex].Y;
            // BotKilled position should be the bot's actual position; allow
            // small tolerance in case StaticTurret reports center-of-tile vs.
            // exact pixel.
            if (Math.Abs(pixelX - cx) > 32 || Math.Abs(pixelY - cy) > 32)
                continue;

            cap.Alive = false;
            cap.DeadRespawnAtTickMs = Environment.TickCount + (ad.HqArenaState.RespawnDelaySeconds * 1000);
            _logManager.LogA(LogLevel.Info, LogCategory, arena,
                $"HQ capital freq {freq} killed (by {killer?.Name ?? "?"}). " +
                $"Respawn in {ad.HqArenaState.RespawnDelaySeconds}s.");

            // Freeze defender respawns for this freq while capital is dead —
            // gives the attacker a real window to clear the defenders. The
            // freeze affects ALL fakes on this freq (perimeter guns + command
            // + any player-deployed structures owned by the same freq), which
            // is fine since the defending team's losing their HQ anyway.
            // Unfrozen in RespawnHqCapital when capital comes back.
            _hqStaticTurretForKills?.FreezeRespawn(arena, freq, true);

            CheckSuddenDeathGate_Hq(arena, ad, freq);
            return;
        }
    }

    /// <summary>
    /// Round-end gate. Fired from BOTH the capital-kill path and each
    /// defender-kill path so the round can end on either:
    ///   - A capital kill that lands while all defenders are already dead, OR
    ///   - A last-defender kill that lands while the capital is already dead.
    /// Defender respawns are frozen while the capital is dead, so the
    /// attacker has a 60s window to clear them before the capital returns.
    /// </summary>
    private void CheckSuddenDeathGate_Hq(Arena arena, ArenaData ad, short freq)
    {
        if (_hqStaticTurretForKills is null) return;
        if (ad.HqArenaState is null) return;

        // Capital must be dead.
        bool capitalDead = false;
        foreach (HqCapitalRuntime cap in ad.HqArenaState.Capitals)
        {
            if (cap.Freq != freq) continue;
            capitalDead = !cap.Alive;
            break;
        }
        if (!capitalDead) return;

        int liveGuns = _hqStaticTurretForKills.CountBots(arena, freq, HqPerimeterGunKey);
        int liveCommand = _hqStaticTurretForKills.CountBots(arena, freq, HqCommandKey);
        if (liveGuns == 0 && liveCommand == 0)
        {
            OnHqCapitalKilled_RoundManager(arena, freq);
        }
        else
        {
            _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
                $"Sudden-death gate freq {freq}: " +
                $"capital dead, {liveGuns} gun(s) + {liveCommand} command core(s) remain.");
        }
    }

    // -------------------------------------------------------------------------
    // HELPERS
    // -------------------------------------------------------------------------

    /// <summary>±20% jitter around a base period so multiple capitals don't
    /// teleport in lockstep. Returns at least 1000ms.</summary>
    private int JitterPatrolPeriod(int basePeriodMs)
    {
        double frac = 0.8 + (_hqJitterRng.NextDouble() * 0.4);
        return Math.Max(1000, (int)(basePeriodMs * frac));
    }

    /// <summary>
    /// Tear down a single freq's HQ entirely (defenders + capital + baseplate).
    /// Called by RoundManager on round-end so the destroyed HQ visibly stays
    /// gone during the 30s celebration before the full-reset respawn. Does
    /// NOT remove the freq's HqCapitalRuntime entry — the round-reset path
    /// expects the runtime list intact and will rebuild via DespawnHqArena
    /// + TrySpawnHqArena.
    /// </summary>
    internal void HideHqForFreq(Arena arena, short freq)
    {
        if (_hqBroker is null) return;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.HqArenaState is null) return;

        IStaticTurret? staticTurret = _hqBroker.GetInterface<IStaticTurret>();
        if (staticTurret is null) return;
        try
        {
            var cfg = arena.Cfg!;
            HqDefinition? def = null;
            foreach (var d in _hqDefinitions) if (d.Freq == freq) { def = d; break; }
            if (def is null) return;

            int teamTileX = _configManager.GetInt(cfg, "Spawn", $"Team{freq}-X", 512);
            int teamTileY = _configManager.GetInt(cfg, "Spawn", $"Team{freq}-Y", 512);
            int centerPx = (teamTileX << 4) + 8;
            int centerPy = (teamTileY << 4) + 8;

            // Despawn each defender slot. Use the slot's expected position;
            // skip-on-failure so a missing/respawning defender doesn't abort
            // the rest of the cleanup.
            foreach (HqTurretSlot slot in def.Defenders)
            {
                int sx = centerPx + slot.OffsetX;
                int sy = centerPy + slot.OffsetY;
                try { staticTurret.RemoveBotAt(arena, sx, sy, freq, slot.TurretKey); }
                catch { /* best-effort */ }
            }

            // Capital may already be dead (we got here because it was just killed).
            // If somehow still alive (race), tear it down too.
            foreach (HqCapitalRuntime cap in ad.HqArenaState.Capitals)
            {
                if (cap.Freq != freq || !cap.Alive) continue;
                int cx = cap.CenterPixelX + cap.Corners[cap.CornerIndex].X;
                int cy = cap.CenterPixelY + cap.Corners[cap.CornerIndex].Y;
                try { staticTurret.RemoveBotAt(arena, cx, cy, freq, HqCapitalKey); }
                catch { /* best-effort */ }
                cap.Alive = false;
                // Park respawn timer FAR in the future so the existing patrol
                // tick doesn't try to respawn during the round-end window.
                cap.DeadRespawnAtTickMs = Environment.TickCount + (24 * 60 * 60 * 1000);
            }

            // Hide the freq's baseplate. BaseplateIds is populated in the
            // same order as _hqDefinitions — index = freq for the standard 2-freq layout.
            int idx = -1;
            for (int i = 0; i < _hqDefinitions.Length; i++)
                if (_hqDefinitions[i].Freq == freq) { idx = i; break; }
            if (idx >= 0 && idx < ad.HqArenaState.BaseplateIds.Count)
            {
                short bid = ad.HqArenaState.BaseplateIds[idx];
                try { _lvzObjects.Toggle(arena, bid, false); }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            _hqBroker.ReleaseInterface(ref staticTurret);
        }

        _logManager.LogA(LogLevel.Info, LogCategory, arena,
            $"HQ freq {freq} hidden for round-end window.");
    }
}
