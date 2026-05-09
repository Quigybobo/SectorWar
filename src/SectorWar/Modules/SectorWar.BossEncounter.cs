using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — BossEncounter subsystem.
// =============================================================================
//
// PURPOSE
// -------
// One boss per sector arena: a static-position fake-player with high HP that
// registers with IDamage so real-player bullets do server-side damage. When
// HP hits 0, ISectorWar.RegisterBossKill is called → the gate to the next
// arena unlocks.
//
// SOURCE
// ------
// Standalone module `Modules/BossEncounter.cs` stays as a library copy.
//
// TUNING (hardcoded for first slice; later phases will read per-arena conf)
//   - Spawn at tile (256, 512) — off-center to dodge any center safe-zone.
//   - 50000 HP. Devastation's BulletDamageLevel=200 + level-3 bullets in
//     upper-tier kits => ~30s solo fight at endgame, scales linearly.
//   - 30000 visible energy bar (proportional to HP).
//   - Big radius (64) so it's hard to miss.
//   - Freq 9999 — separate from real player freqs.
//   - 50ms tick (20 Hz) — Continuum drops fakes from view if no position
//     packet arrives within ~1-2s; this keeps the boss visible AND lets the
//     energy bar feel responsive.
//
// HEALTH SCALING TRICK
// --------------------
// Levi's MaximumEnergy is overridden to 30000 at spawn so the displayed bar
// uses that scale; HP-to-displayed mapping is:
//   displayed = 30000 * Math.Max(hp, 0) / 50000
// → bar drains proportionally with our 50k HP regardless of total.
// We do NOT override Radius — see comment in TrySpawnBoss for why.
//
// RUNTIME OWNERSHIP
//   - Owned state: ArenaData with BossPlayer, position, current displayed energy,
//                  closure pointer.
//   - Conf keys read: NONE.
//   - Persisted data: NONE (boss alive flag lives on ISectorWar's ArenaSectorState).
//   - Fakes registered: 1 boss per sector arena.
//   - Timers scheduled: 50ms IMainloopTimer position-refresh tick.
//   - Commands registered: NONE.
//
// CALLBACKS HOOKED (per-arena)
//   - ArenaActionCallback → OnArenaAction_BossEncounter (Create=spawn, Destroy=despawn)
//
// THREADING
// ---------
// Mainloop only. IDamage callbacks fire on mainloop. _arenaManager.Lock for
// the per-tick arena enumeration.
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave 6: snapshot-then-null pattern in DespawnBoss + OnBossKilled — clear
// ad.BossPlayer and ctx.Killed BEFORE calling EndFaked so any in-flight
// IDamage callback short-circuits instead of dereferencing a freed Player.
// Multi-bullets-in-one-tick guard via Killed flag in OnBossDamaged.
// =============================================================================

public sealed partial class SectorWar
{
    private const int BossEncounterSpawnTileX = 256;
    private const int BossEncounterSpawnTileY = 512;
    private const int BossEncounterEnergy = 50000;
    private const int BossEncounterDisplayMaxEnergy = 30000;
    private const int BossEncounterRadius = 64;
    private const short BossEncounterFreq = 9999;
    private const int BossEncounterTickMs = 50;

    // -------------------------------------------------------------------------
    // ArenaData extension
    // -------------------------------------------------------------------------

    internal sealed partial class ArenaData
    {
        public Player? BossEncounterPlayer;
        public short BossEncounterPixelX;
        public short BossEncounterPixelY;
        public short BossEncounterEnergyCurrent;
        public BossEncounterClosure? BossEncounterClosure;
    }

    /// <summary>
    /// Per-boss state passed as the closure to IDamage. Killed flag handles
    /// the multi-bullets-in-one-tick race (Damage.UpdateWeapons snapshots its
    /// bot list before iterating, so multiple bullets on one tick can call
    /// OnBossDamaged after HP already hit 0).
    /// </summary>
    internal sealed class BossEncounterClosure
    {
        public required Arena Arena { get; init; }
        public required string ArenaName { get; init; }
        public required int LaneIndex { get; init; }
        public int HpRemaining;
        public bool Killed;
    }

    private IComponentBroker? _bossEncounterBroker;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadBossEncounter(IComponentBroker broker)
    {
        _bossEncounterBroker = broker;
        _mainloopTimer.SetTimer(OnTick_BossEncounter, BossEncounterTickMs,
            BossEncounterTickMs, this);
        _logManager.LogM(LogLevel.Info, LogCategory, "BossEncounter subsystem loaded.");
    }

    private void UnloadBossEncounter(IComponentBroker broker)
    {
        _mainloopTimer.ClearTimer(OnTick_BossEncounter, this);
        _bossEncounterBroker = null;
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    // -------------------------------------------------------------------------

    private void AttachBossEncounter(Arena arena)
    {
        ArenaActionCallback.Register(arena, OnArenaAction_BossEncounter);
    }

    private void DetachBossEncounter(Arena arena)
    {
        ArenaActionCallback.Unregister(arena, OnArenaAction_BossEncounter);
        DespawnBossEncounter(arena);
    }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    private void OnArenaAction_BossEncounter(Arena arena, ArenaAction action)
    {
        switch (action)
        {
            case ArenaAction.Create: TrySpawnBossEncounter(arena); break;
            case ArenaAction.Destroy: DespawnBossEncounter(arena); break;
        }
    }

    /// <summary>
    /// Periodic position refresh. Without this, Continuum drops the fake from
    /// view after ~1-2s. We also use it to push the latest energy bar value
    /// (driven by OnBossDamaged updates).
    /// </summary>
    private bool OnTick_BossEncounter()
    {
        _arenaManager.Lock();
        try
        {
            foreach (Arena arena in _arenaManager.Arenas)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) continue;
                if (ad.BossEncounterPlayer is null) continue;

                C2S_PositionPacket pkt = default;
                pkt.Type = 0x03;
                pkt.X = ad.BossEncounterPixelX;
                pkt.Y = ad.BossEncounterPixelY;
                pkt.XSpeed = 0;
                pkt.YSpeed = 0;
                pkt.Rotation = 0;
                pkt.Bounty = 0;
                pkt.Energy = ad.BossEncounterEnergyCurrent;
                pkt.Time = ServerTick.Now;
                _game.FakePosition(ad.BossEncounterPlayer, ref pkt);
            }
        }
        finally
        {
            _arenaManager.Unlock();
        }
        return true;
    }

    // -------------------------------------------------------------------------
    // SPAWN / DESPAWN
    // -------------------------------------------------------------------------

    private void TrySpawnBossEncounter(Arena arena)
    {
        if (arena.Name is null) return;
        if (_bossEncounterBroker is null) return;

        ISectorWar? sectorWar = _bossEncounterBroker.GetInterface<ISectorWar>();
        if (sectorWar is null) return;
        try
        {
            ArenaSectorState? state = sectorWar.GetArenaState(arena.Name);
            if (state is null) return;          // not a tracked sector arena
            if (!state.BossAlive) return;       // dead this week
            if (state.BossEntity is not null) return;  // already spawned

            IDamage? damage = _bossEncounterBroker.GetInterface<IDamage>();
            if (damage is null)
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    "IDamage not loaded — boss cannot take damage.");
                return;
            }

            try
            {
                string bossName = $"~Boss-{state.LaneIndex}";
                Player? boss = _fake.CreateFakePlayer(bossName, arena, ShipType.Leviathan,
                    BossEncounterFreq);
                if (boss is null)
                {
                    _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                        "Failed to create boss fake player.");
                    return;
                }

                C2S_PositionPacket pos = default;
                pos.Type = 0x03;
                pos.X = (short)(BossEncounterSpawnTileX * 16);
                pos.Y = (short)(BossEncounterSpawnTileY * 16);
                pos.XSpeed = 0;
                pos.YSpeed = 0;
                pos.Rotation = 0;
                pos.Bounty = 0;
                pos.Energy = (short)BossEncounterDisplayMaxEnergy;
                pos.Time = ServerTick.Now;
                _game.FakePosition(boss, ref pos);

                // CRITICAL: do NOT override Radius. The Damage subsystem reads
                // it from arena conf ([Leviathan] Radius=18). But per-player
                // Radius overrides ALSO drive the Continuum CLIENT's visual
                // bullet-absorption zone — mismatch = bullets vanish in empty
                // space past the visible sprite. Keep client + server in sync
                // by inheriting the conf's Levi Radius.
                //
                // We DO override MaximumEnergy so the HP bar uses the 30000
                // display scale.
                if (_clientSettings.TryGetSettingsIdentifier("Leviathan", "MaximumEnergy",
                    out var maxEnergyId))
                {
                    _clientSettings.OverrideSetting(boss, maxEnergyId,
                        BossEncounterDisplayMaxEnergy);
                    _clientSettings.SendClientSettings(boss);
                }

                // manageEnergy=false: the module's auto-tracking caps energy at
                // [Leviathan] InitialEnergy from conf (~1700), too low for boss
                // feel. We track HP manually in the closure and call KillFake
                // when HP <= 0.
                BossEncounterClosure closure = new()
                {
                    Arena = arena,
                    ArenaName = arena.Name,
                    LaneIndex = state.LaneIndex,
                    HpRemaining = BossEncounterEnergy,
                };
                damage.AddFake(boss, ref pos, manageEnergy: false,
                    killFunc: OnBossEncounterKilled,
                    respawnFunc: null,
                    damageFunc: OnBossEncounterDamaged,
                    closure: closure);

                state.BossEntity = boss;
                if (arena.TryGetExtraData(_adKey, out ArenaData? ad))
                {
                    ad.BossEncounterPlayer = boss;
                    ad.BossEncounterPixelX = pos.X;
                    ad.BossEncounterPixelY = pos.Y;
                    ad.BossEncounterEnergyCurrent = (short)BossEncounterDisplayMaxEnergy;
                    ad.BossEncounterClosure = closure;
                }

                _logManager.LogA(LogLevel.Info, LogCategory, arena,
                    $"Boss '{bossName}' spawned with {BossEncounterEnergy} HP.");
            }
            finally
            {
                _bossEncounterBroker.ReleaseInterface(ref damage);
            }
        }
        finally
        {
            _bossEncounterBroker.ReleaseInterface(ref sectorWar);
        }
    }

    private void DespawnBossEncounter(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.BossEncounterPlayer is null) return;
        if (_bossEncounterBroker is null) return;

        // Wave-6 snapshot-then-null pattern: clear ad.BossEncounterPlayer
        // BEFORE EndFaked so per-tick reads + IDamage callbacks during teardown
        // can't dereference the freed Player.
        Player boss = ad.BossEncounterPlayer;
        ad.BossEncounterPlayer = null;
        if (ad.BossEncounterClosure is not null) ad.BossEncounterClosure.Killed = true;

        IDamage? damage = _bossEncounterBroker.GetInterface<IDamage>();
        try { damage?.RemoveFake(boss); }
        finally
        {
            if (damage is not null) _bossEncounterBroker.ReleaseInterface(ref damage);
        }
        _fake.EndFaked(boss);
    }

    // -------------------------------------------------------------------------
    // DAMAGE / KILL CALLBACKS
    // -------------------------------------------------------------------------

    private void OnBossEncounterDamaged(Player fake, Player firedBy, int dist, int damage,
        WeaponCodes weaponType, int level, bool bouncing, int empTime, object? closure)
    {
        if (closure is not BossEncounterClosure ctx) return;

        // Wave 6: multi-bullets-in-one-tick race — Damage.UpdateWeapons
        // snapshots bots before iterating, so a single tick with several
        // bullets can fire this callback after HP already hit zero. Without
        // the Killed early-out we'd KillFake again and re-EndFake a freed
        // Player.
        if (ctx.Killed) return;

        _logManager.LogA(LogLevel.Drivel, LogCategory, ctx.Arena,
            $"Boss took {damage} dmg from {firedBy.Name} " +
            $"(HP {ctx.HpRemaining} -> {ctx.HpRemaining - damage}).");

        ctx.HpRemaining -= damage;

        // Sync visible energy bar — proportional scaling so bar drains with HP.
        if (ctx.Arena.TryGetExtraData(_adKey, out ArenaData? ad))
        {
            int displayed = (int)((long)Math.Max(ctx.HpRemaining, 0)
                * BossEncounterDisplayMaxEnergy / BossEncounterEnergy);
            ad.BossEncounterEnergyCurrent = (short)Math.Clamp(displayed, 0,
                BossEncounterDisplayMaxEnergy);
        }

        if (ctx.HpRemaining <= 0)
        {
            ctx.HpRemaining = 0;
            if (_bossEncounterBroker is not null)
            {
                IDamage? damageModule = _bossEncounterBroker.GetInterface<IDamage>();
                try { damageModule?.KillFake(fake, firedBy); }
                finally
                {
                    if (damageModule is not null)
                        _bossEncounterBroker.ReleaseInterface(ref damageModule);
                }
            }
        }
    }

    private void OnBossEncounterKilled(Player fake, Player? killer, object? closure)
    {
        if (closure is not BossEncounterClosure ctx) return;
        if (_bossEncounterBroker is null) return;

        Arena? arena = ctx.Arena;
        if (arena is null) return;

        // Tell SectorWar so the gate opens.
        ISectorWar? sectorWar = _bossEncounterBroker.GetInterface<ISectorWar>();
        try
        {
            if (sectorWar is not null)
            {
                var killers = killer is not null ? new[] { killer } : Array.Empty<Player>();
                sectorWar.RegisterBossKill(ctx.ArenaName, killers);
            }
        }
        finally
        {
            if (sectorWar is not null) _bossEncounterBroker.ReleaseInterface(ref sectorWar);
        }

        // Wave 6: snapshot-then-null + Killed flag BEFORE EndFaked.
        if (arena.TryGetExtraData(_adKey, out ArenaData? ad))
            ad.BossEncounterPlayer = null;
        ctx.Killed = true;

        IDamage? damage = _bossEncounterBroker.GetInterface<IDamage>();
        try { damage?.RemoveFake(fake); }
        finally
        {
            if (damage is not null) _bossEncounterBroker.ReleaseInterface(ref damage);
        }
        _fake.EndFaked(fake);

        string killerName = killer?.Name ?? "(unknown)";
        _logManager.LogA(LogLevel.Info, LogCategory, arena,
            $"Boss (lane {ctx.LaneIndex}) killed by {killerName}. Gate to next arena unlocked.");
        _chat.SendArenaMessage(arena, $"*** Boss defeated by {killerName}! The next sector is open.");
    }
}
