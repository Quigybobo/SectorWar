using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.Utilities;
using System.Threading;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — Damage subsystem (Phase 1 asss-damage bullet+bomb port).
// =============================================================================
//
// PURPOSE
// -------
// Server-side bullet+bomb collision detection for registered fake players.
// Real players' damage stays receiver-authoritative (PlayerDamageCallback +
// IWatchDamage); this subsystem ONLY handles projectiles hitting fakes —
// fakes have no client to compute hits, so without this they're invincible
// bullet-absorbers.
//
// SOURCE
// ------
// Phase 1 port of JoWie's asss-damage (damage.c). Standalone module
// `Modules/Damage.cs` stays as a library copy. Direct-hit only — splash,
// prox bombs, bouncing, EMP, thors, multifire, tile damage, region damage
// all DEFERRED to a future phase.
//
// CONF SECTIONS — KEPT IN STANDARD `[Bullet]` / `[Bomb]` / `[<Ship>]`
// ------------------------------------------------------------------
// These are SS.NET-conventional sections owned by the core game module.
// Documented exception in `docs/SECTORWAR_CONF.md`.
//
// HOOKS
// -----
//   - PlayerPositionPacketCallback (zone-wide): captures bullet/bomb fires
//   - PlayerActionCallback / ShipFreqChangeCallback: clear weapons on player
//     transition
//   - ArenaActionCallback (per-arena): refresh arena settings on Create/ConfChanged
//   - 10ms IMainloopTimer: weapon advance + collision per tick
//
// IDamage IMPLEMENTATION
// ----------------------
//   AddFake / KillFake / RemoveFake — caller code (CompositeHitbox,
//   BossEncounter, DevCommands ?damtest, …) routes damage via closure.
//
// SUBPIXEL PHYSICS
// ----------------
// asss uses x*1000 subpixel positions to track sub-pixel motion. We do the
// same with X1000/Y1000 longs. xspeed/yspeed = subpixels per 10ms tick.
//
// SinTab — 40 entries (9° each), sin(i*pi/20)*128 as sbyte. Index 0 = north,
// clockwise. Multiply-and-shift-right-7 reproduces asss's velocity-component
// math exactly.
//
// HOT-PATH GUARDS PRESERVED
// -------------------------
// Wave 1: owner-disconnect guard before weapon update (RemoveWeaponsFor is
// event-driven and may run AFTER the tick, so we'd otherwise dereference a
// freed Player). Energy clamp `Math.Max(0, ...)` to prevent negative.
// Snapshot bots+weapons under lock then process outside (DamageFunc could
// trigger AddFake re-entrancy).
// =============================================================================

public sealed partial class SectorWar : IDamage
{
    private const int DamageTickIntervalMs = 10;
    private const int DamageBulletRadius = 3;
    private const int DamageBombRadius = 7;

    /// <summary>sin(i*pi/20)*128 as sbyte. Multiplied with projSpeed and
    /// shifted right 7 = sin(angle)*projSpeed.</summary>
    private static readonly sbyte[] DamageSinTab = BuildDamageSinTab();
    private static sbyte[] BuildDamageSinTab()
    {
        var t = new sbyte[40];
        for (int i = 0; i < 40; i++)
            t[i] = (sbyte)Math.Round(Math.Sin(i * Math.PI / 20.0) * 128.0);
        return t;
    }

    // -------------------------------------------------------------------------
    // ArenaData extension
    // -------------------------------------------------------------------------

    internal sealed class DamageBotData
    {
        public Player Fake = null!;
        public bool ManageEnergy;
        public DamageKilledFunc? KillFunc;
        public DamageRespawnFunc? RespawnFunc;
        public FakeDamageFunc? DamageFunc;
        public object? Closure;
        public int Energy;
        public int MaxEnergy;
        public int Recharge;
        public C2S_PositionPacket Pos;
        /// <summary>Per-fake collision radius override. Null = use
        /// arena-wide ship Radius.</summary>
        public int? RadiusOverride;
    }

    internal sealed class DamageWeapon
    {
        public Player Owner = null!;
        public long X1000;
        public long Y1000;
        public short XSpeed;
        public short YSpeed;
        public uint StartedAt;
        public uint LastUpdate;
        public int AliveTimeMs;
        public WeaponCodes Type;
        public byte Level;
    }

    internal sealed partial class ArenaData
    {
        public readonly Lock DamageLock = new();
        public List<DamageWeapon> DamageWeaponSet = new();
        public List<DamageBotData> DamageBots = new();
        public bool DamageIgnoreTeamDamage;
        public int DamageBulletDamageLevel = 200;
        public int DamageBulletDamageUpgrade = 100;
        public int DamageBulletAliveTime = 550;
        public int DamageBombDamageLevel = 750;
        public int DamageBombAliveTime = 800;
        public int[] DamageBulletSpeed = new int[8];
        public int[] DamageBombSpeed = new int[8];
        public int[] DamageRadius = new int[8];
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private InterfaceRegistrationToken<IDamage>? _damageToken;
    private readonly ClientSettingIdentifier?[] _damageBulletSpeedIds = new ClientSettingIdentifier?[8];
    private readonly ClientSettingIdentifier?[] _damageBombSpeedIds = new ClientSettingIdentifier?[8];
    private bool _damageIdentifiersResolved;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadDamage(IComponentBroker broker)
    {
        PlayerPositionPacketCallback.Register(broker, OnPlayerPosition_Damage);
        PlayerActionCallback.Register(broker, OnPlayerAction_Damage);
        ShipFreqChangeCallback.Register(broker, OnShipFreqChange_Damage);

        _mainloopTimer.SetTimer(OnTick_Damage, DamageTickIntervalMs,
            DamageTickIntervalMs, this);

        _damageToken = broker.RegisterInterface<IDamage>(this);

        _logManager.LogM(LogLevel.Info, LogCategory,
            "Damage subsystem loaded (Phase 1: bullet + bomb direct-hit).");
    }

    private void UnloadDamage(IComponentBroker broker)
    {
        if (_damageToken is not null)
            broker.UnregisterInterface(ref _damageToken);

        _mainloopTimer.ClearTimer(OnTick_Damage, this);

        PlayerPositionPacketCallback.Unregister(broker, OnPlayerPosition_Damage);
        PlayerActionCallback.Unregister(broker, OnPlayerAction_Damage);
        ShipFreqChangeCallback.Unregister(broker, OnShipFreqChange_Damage);
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    // -------------------------------------------------------------------------

    private void AttachDamage(Arena arena)
    {
        ArenaActionCallback.Register(arena, OnArenaAction_Damage);
        ReadDamageArenaSettings(arena);
    }

    private void DetachDamage(Arena arena)
    {
        ArenaActionCallback.Unregister(arena, OnArenaAction_Damage);

        if (arena.TryGetExtraData(_adKey, out ArenaData? ad))
        {
            lock (ad.DamageLock)
            {
                ad.DamageWeaponSet.Clear();
                ad.DamageBots.Clear();
            }
        }
    }

    private void OnArenaAction_Damage(Arena arena, ArenaAction action)
    {
        if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            ReadDamageArenaSettings(arena);
    }

    private void ReadDamageArenaSettings(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        ConfigHandle? cfg = arena.Cfg;
        if (cfg is null) return;

        ad.DamageIgnoreTeamDamage = _configManager.GetInt(cfg, "Damage", "IgnoreTeamDamage", 0) != 0;
        ad.DamageBulletDamageLevel = _configManager.GetInt(cfg, "Bullet", "BulletDamageLevel", 200);
        ad.DamageBulletDamageUpgrade = _configManager.GetInt(cfg, "Bullet", "BulletDamageUpgrade", 100);
        ad.DamageBulletAliveTime = _configManager.GetInt(cfg, "Bullet", "BulletAliveTime", 550);
        ad.DamageBombDamageLevel = _configManager.GetInt(cfg, "Bomb", "BombDamageLevel", 750);
        ad.DamageBombAliveTime = _configManager.GetInt(cfg, "Bomb", "BombAliveTime", 800);

        for (int s = 0; s < 8; s++)
        {
            string ship = ((ShipType)s).ToString();
            ad.DamageBulletSpeed[s] = _configManager.GetInt(cfg, ship, "BulletSpeed", 2000);
            ad.DamageBombSpeed[s] = _configManager.GetInt(cfg, ship, "BombSpeed", 2000);
            ad.DamageRadius[s] = _configManager.GetInt(cfg, ship, "Radius", 14);
        }
    }

    // -------------------------------------------------------------------------
    // IDamage IMPLEMENTATION
    // -------------------------------------------------------------------------

    void IDamage.AddFake(Player fake, ref C2S_PositionPacket pos, bool manageEnergy,
        DamageKilledFunc? killFunc, DamageRespawnFunc? respawnFunc,
        FakeDamageFunc? damageFunc, object? closure, int? radiusOverride)
    {
        if (fake?.Arena is null) return;
        if (!fake.Arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        var bd = new DamageBotData
        {
            Fake = fake,
            ManageEnergy = manageEnergy,
            KillFunc = killFunc,
            RespawnFunc = respawnFunc,
            DamageFunc = damageFunc,
            Closure = closure,
            Pos = pos,
            RadiusOverride = radiusOverride,
        };

        if (manageEnergy)
        {
            int ship = (int)fake.Ship;
            if (ship < 0 || ship > 7) ship = 0;
            ConfigHandle? cfg = fake.Arena.Cfg;
            string shipName = ((ShipType)ship).ToString();
            int initial = cfg is null ? 1000
                : _configManager.GetInt(cfg, shipName, "InitialEnergy", 1000);
            bd.Energy = bd.MaxEnergy = initial;
            int recharge = cfg is null ? 1150
                : _configManager.GetInt(cfg, shipName, "MaximumRecharge", 1150);
            bd.Recharge = recharge;
        }

        lock (ad.DamageLock)
        {
            // Reject duplicate adds — overwrite (matches asss behavior).
            for (int i = 0; i < ad.DamageBots.Count; i++)
            {
                if (ad.DamageBots[i].Fake == fake)
                {
                    ad.DamageBots[i] = bd;
                    return;
                }
            }
            ad.DamageBots.Add(bd);
        }
    }

    void IDamage.KillFake(Player fake, Player killer)
    {
        if (fake?.Arena is null) return;
        if (!fake.Arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        DamageBotData? toKill = null;
        lock (ad.DamageLock)
        {
            for (int i = 0; i < ad.DamageBots.Count; i++)
            {
                if (ad.DamageBots[i].Fake == fake)
                {
                    toKill = ad.DamageBots[i];
                    break;
                }
            }
        }
        toKill?.KillFunc?.Invoke(fake, killer, toKill.Closure);
    }

    void IDamage.RemoveFake(Player fake)
    {
        if (fake?.Arena is null) return;
        if (!fake.Arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        lock (ad.DamageLock)
        {
            for (int i = ad.DamageBots.Count - 1; i >= 0; i--)
            {
                if (ad.DamageBots[i].Fake == fake)
                {
                    ad.DamageBots.RemoveAt(i);
                    return;
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    private void OnPlayerPosition_Damage(Player player,
        ref readonly C2S_PositionPacket pos, ref readonly ExtraPositionData extra, bool hasExtra)
    {
        Arena? arena = player.Arena;
        if (arena is null) return;
        if (player.Status != PlayerState.Playing) return;
        if (player.Ship < ShipType.Warbird || player.Ship > ShipType.Shark) return;
        if (pos.X == -1 && pos.Y == -1) return;

        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        WeaponCodes wt = pos.Weapon.Type;
        if (wt == WeaponCodes.Bullet || wt == WeaponCodes.Bomb)
            AddDamageProjectileWeapon(ad, player, in pos);
    }

    private void OnPlayerAction_Damage(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.LeaveArena || action == PlayerAction.Disconnect)
        {
            if (arena is not null && arena.TryGetExtraData(_adKey, out ArenaData? ad))
                RemoveDamageWeaponsFor(ad, player);
        }
    }

    private void OnShipFreqChange_Damage(Player player, ShipType newShip, ShipType oldShip,
        short newFreq, short oldFreq)
    {
        Arena? arena = player.Arena;
        if (arena is null) return;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        RemoveDamageWeaponsFor(ad, player);
    }

    // -------------------------------------------------------------------------
    // WEAPON MANAGEMENT
    // -------------------------------------------------------------------------

    private void AddDamageProjectileWeapon(ArenaData ad, Player firer, ref readonly C2S_PositionPacket pos)
    {
        int ship = (int)firer.Ship;
        if (ship < 0 || ship > 7) return;

        uint now = (uint)Environment.TickCount;
        bool isBomb = pos.Weapon.Type == WeaponCodes.Bomb;
        int aliveTime10ms = isBomb ? ad.DamageBombAliveTime : ad.DamageBulletAliveTime;

        // Pull projectile speed from the FIRER's effective per-player setting,
        // not the arena-wide one. Hyperspace-style configs put the cap in the
        // base section and override per-player to a lower value via
        // ShipSettings + IClientSettings. Continuum uses the OVERRIDDEN value
        // to draw + simulate; server-side collision must match or it tracks
        // a ghost bullet running ahead of where the client sees it.
        int projSpeed = GetEffectiveDamageProjectileSpeed(firer, ship, isBomb)
            ?? (isBomb ? ad.DamageBombSpeed[ship] : ad.DamageBulletSpeed[ship]);

        var w = new DamageWeapon
        {
            Owner = firer,
            X1000 = (long)pos.X * 1000,
            Y1000 = (long)pos.Y * 1000,
            XSpeed = pos.XSpeed,
            YSpeed = pos.YSpeed,
            StartedAt = now,
            LastUpdate = now,
            AliveTimeMs = aliveTime10ms * 10,
            Type = pos.Weapon.Type,
            Level = pos.Weapon.Level,
        };

        // Add velocity contribution from firing direction (sin/cos via SinTab).
        int rotIdx = ((pos.Rotation % 40) + 40) % 40;
        w.XSpeed += (short)((projSpeed * DamageSinTab[rotIdx]) >> 7);
        w.YSpeed += (short)((projSpeed * DamageSinTab[(rotIdx + 30) % 40]) >> 7);

        lock (ad.DamageLock) { ad.DamageWeaponSet.Add(w); }
    }

    private void RemoveDamageWeaponsFor(ArenaData ad, Player owner)
    {
        lock (ad.DamageLock) { ad.DamageWeaponSet.RemoveAll(w => w.Owner == owner); }
    }

    private void EnsureDamageIdentifiers()
    {
        if (_damageIdentifiersResolved) return;
        for (int s = 0; s < 8; s++)
        {
            string section = ((ShipType)s).ToString();
            if (_clientSettings.TryGetSettingsIdentifier(section, "BulletSpeed", out var bid))
                _damageBulletSpeedIds[s] = bid;
            if (_clientSettings.TryGetSettingsIdentifier(section, "BombSpeed", out var did))
                _damageBombSpeedIds[s] = did;
        }
        _damageIdentifiersResolved = true;
    }

    private int? GetEffectiveDamageProjectileSpeed(Player firer, int ship, bool isBomb)
    {
        EnsureDamageIdentifiers();
        ClientSettingIdentifier? id = isBomb ? _damageBombSpeedIds[ship]
                                              : _damageBulletSpeedIds[ship];
        if (id is null) return null;
        int value = _clientSettings.GetSetting(firer, id.Value);
        return value > 0 ? value : null;
    }

    // -------------------------------------------------------------------------
    // TICK — weapon advance + collision
    // -------------------------------------------------------------------------

    private bool OnTick_Damage()
    {
        _arenaManager.Lock();
        try
        {
            foreach (Arena arena in _arenaManager.Arenas)
            {
                if (arena.Status != ArenaState.Running) continue;
                if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) continue;
                UpdateDamageWeapons(arena, ad);
            }
        }
        finally { _arenaManager.Unlock(); }
        return true;
    }

    /// <summary>
    /// Per-arena tick: advance every weapon by its velocity, check tile +
    /// fake collision, fire DamageFunc on hit. Snapshots weapons + bots
    /// under lock then iterates outside (DamageFunc could re-enter via
    /// AddFake).
    /// </summary>
    private void UpdateDamageWeapons(Arena arena, ArenaData ad)
    {
        uint now = (uint)Environment.TickCount;

        DamageWeapon[] weaponsSnap;
        DamageBotData[] botsSnap;
        lock (ad.DamageLock)
        {
            if (ad.DamageWeaponSet.Count == 0) return;
            weaponsSnap = ad.DamageWeaponSet.ToArray();
            botsSnap = ad.DamageBots.ToArray();
        }

        var toRemove = new List<DamageWeapon>();

        foreach (var w in weaponsSnap)
        {
            // Wave-1 owner-disconnect guard.
            if (w.Owner is null
                || w.Owner.Status >= PlayerState.LeavingArena
                || w.Owner.Arena != arena)
            {
                toRemove.Add(w);
                continue;
            }

            int aliveMs = (int)(now - w.StartedAt);
            if (aliveMs >= w.AliveTimeMs) { toRemove.Add(w); continue; }

            int dtTicks = (int)((now - w.LastUpdate) / 10);
            if (dtTicks <= 0) continue;

            w.X1000 += (long)w.XSpeed * dtTicks;
            w.Y1000 += (long)w.YSpeed * dtTicks;
            w.LastUpdate = now;

            int wx = (int)(w.X1000 / 1000);
            int wy = (int)(w.Y1000 / 1000);
            int tx = wx >> 4;
            int ty = wy >> 4;

            // Tile collision (wall hit). Out-of-bounds = remove.
            if (tx < 0 || tx > 1023 || ty < 0 || ty > 1023)
            { toRemove.Add(w); continue; }
            MapTile tile = _mapData.GetTile(arena, new TileCoordinates((short)tx, (short)ty));
            if (IsDamageTileSolid(tile)) { toRemove.Add(w); continue; }

            // Fake collision.
            bool hitFake = false;
            foreach (var bot in botsSnap)
            {
                if (bot.Fake.Arena != arena) continue;
                if (bot.Fake.Freq == w.Owner.Freq) continue;          // friendly-fire skip
                if (bot.Fake.Flags.IsDead) continue;
                if (bot.Fake.Ship == ShipType.Spec) continue;

                int shipIdx = (int)bot.Fake.Ship;
                if (shipIdx < 0 || shipIdx > 7) continue;
                int shipRadius = bot.RadiusOverride ?? ad.DamageRadius[shipIdx];
                int weaponRadius = w.Type == WeaponCodes.Bomb ? DamageBombRadius : DamageBulletRadius;

                if (PointDamageCollision(wx, wy, weaponRadius, bot.Fake, shipRadius))
                {
                    int damage = w.Type == WeaponCodes.Bomb
                        ? ad.DamageBombDamageLevel * (w.Level + 1)
                        : ad.DamageBulletDamageLevel + ad.DamageBulletDamageUpgrade * w.Level;

                    int dx = wx - bot.Fake.Position.X;
                    int dy = wy - bot.Fake.Position.Y;
                    _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
                        $"{w.Type} hit: shipR={shipRadius} wpnR={weaponRadius} dx={dx} dy={dy}");

                    bot.DamageFunc?.Invoke(bot.Fake, w.Owner, 0, damage,
                        w.Type, w.Level, bouncing: false, empTime: 0, bot.Closure);

                    if (bot.ManageEnergy)
                    {
                        bot.Energy = Math.Max(0, bot.Energy - damage);  // Wave-1 clamp
                        if (bot.Energy <= 0)
                            bot.KillFunc?.Invoke(bot.Fake, w.Owner, bot.Closure);
                    }

                    toRemove.Add(w);
                    hitFake = true;
                    break;
                }
            }
            if (hitFake) continue;
        }

        if (toRemove.Count > 0)
        {
            lock (ad.DamageLock)
            {
                foreach (var w in toRemove) ad.DamageWeaponSet.Remove(w);
            }
        }
    }

    /// <summary>asss damage.c:1176 point_collision (axis-aligned bounding box).
    /// wr = weapon radius (3 bullet / 7 bomb); shipRadius = target ship radius.</summary>
    private static bool PointDamageCollision(int wx, int wy, int wr, Player p, int shipRadius)
    {
        int radius = wr <= 8 ? shipRadius : 0;
        ref readonly var pos = ref p.Position;
        if (pos.X + radius < wx - wr) return false;
        if (pos.X - radius > wx + wr) return false;
        if (pos.Y + radius < wy - wr) return false;
        if (pos.Y - radius > wy + wr) return false;
        return true;
    }

    /// <summary>asss IS_TILE_SOLID equivalent (damage.c:188-191).</summary>
    private static bool IsDamageTileSolid(MapTile tile)
    {
        byte b = tile;
        return (b >= 1 && b <= 161)
            || (b >= 192 && b <= 240)
            || (b >= 243 && b <= 251);
    }
}
