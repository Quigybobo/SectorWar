using Microsoft.Extensions.ObjectPool;
using SS.SectorWar.Interfaces;
using SS.SectorWar.Items;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System.Threading;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — GunTurret subsystem.
// =============================================================================
//
// PURPOSE
// -------
// Each player can have multiple "gun turrets" — fake-player ships attached to
// their hull at configured offset positions. When the anchor fires a
// bullet/bomb, every turret fires its own weapon from its offset position.
//
// SOURCE
// ------
// Port of D1st0rt's gunturret module (ASSS, MIT/X11, 2010). Standalone module
// `Modules/GunTurret.cs` stays as a library copy.
//
// COAST BEHAVIOR
// --------------
// On anchor death, turret-fakes "coast" (drift to off-map sentinel position
// (1,1)) for COAST_TIME ms, then re-attach when anchor respawns. Matches
// the original ASSS behavior.
//
// ROTATION-OFFSET MATH
// --------------------
// SinTab[i] = sin(i * pi/20) — 40-step rotation lookup. Turret world position:
//   rot = (anchorRot + Info.RotationOffset) % 40
//   dx  = OffsetX * sin((rot+10)%40) - OffsetY * sin(rot)
//   dy  = OffsetX * sin(rot)         + OffsetY * sin((rot+10)%40)
// Since sin((θ+π/2)) = cos(θ), the (rot+10) lookup is the cos term — this is
// the standard 2D rotation matrix in disguise.
//
// RUNTIME OWNERSHIP
//   - Owned state: per-player TurretPlayerData with List<TurretEntry>;
//                  global lock guarding all turret state (mirrors ASSS globalmutex).
//   - Conf keys read: NONE (turrets configured via IGunTurret.AddTurret).
//   - Persisted data: NONE (session-only).
//   - Fakes registered: 1 per turret (each is a fake-player attached to
//                       anchor via IGame.Attach).
//   - Timers scheduled: 50ms IMainloopTimer for coast/PPK refresh.
//   - Commands registered: cmd_resetturrets, cmd_addturret, cmd_clearturrets,
//                          cmd_listturrets.
//   - Broker interfaces published: IGunTurret.
//
// CALLBACKS HOOKED (zone-wide)
//   - PlayerActionCallback / ShipFreqChangeCallback / PlayerPositionPacketCallback
//   - KillCallback (mark turrets to coast)
//   - NewPlayerCallback (Player object freed → scrub stale Fake refs)
//
// THREADING
// ---------
// Most events fire on the mainloop. PlayerPositionPacketCallback is on the
// network thread. Single global lock (matches ASSS) guards turret state.
// Snapshot pattern in RemoveAllTurrets to handle re-entrant Callback_NewPlayer
// firing from EndFaked.
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave 5: snapshot+ToArray before destroying turrets so re-entrant callbacks
// don't mutate the list during iteration. Null Fake field BEFORE EndFaked.
// =============================================================================

public sealed partial class SectorWar : IGunTurret
{
    private const int GunTurretCoastTimeMs = 500;
    private const int GunTurretPpkIntervalMs = 50;
    private const int GunTurretTimerCadenceMs = 50;

    private const string GunTurretResetTurretsCommand = "resetturrets";
    private const string GunTurretAddTurretCommand = "addturret";
    private const string GunTurretClearTurretsCommand = "clearturrets";
    private const string GunTurretListTurretsCommand = "listturrets";

    /// <summary>40-step sin LUT (i*pi/20). Index 0 = north.</summary>
    private static readonly double[] GunTurretSinTab = BuildGunTurretSinTab();
    private static double[] BuildGunTurretSinTab()
    {
        var t = new double[40];
        for (int i = 0; i < 40; i++) t[i] = Math.Sin(i * Math.PI / 20.0);
        return t;
    }

    // -------------------------------------------------------------------------
    // PER-PLAYER + ENTRY DATA
    // -------------------------------------------------------------------------

    internal sealed class GunTurretEntry
    {
        public GunTurretInfo Info;
        public Player? Fake;
        public bool Coast;
        public int LastPacketTickMs;
        public int StartCoastTickMs;
        public GunTurretEntry(GunTurretInfo info) { Info = info; }
    }

    private sealed class GunTurretPlayerData : IResettable
    {
        public List<GunTurretEntry> Turrets = new();
        bool IResettable.TryReset()
        {
            // Don't EndFaked here — the GunTurret subsystem must be involved
            // in cleanup. Just clear the list; entries are orphan refs that
            // get scrubbed by Callback_NewPlayer's stale-Fake walk.
            Turrets.Clear();
            return true;
        }
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private InterfaceRegistrationToken<IGunTurret>? _gunTurretToken;
    private PlayerDataKey<GunTurretPlayerData> _gunTurretPdKey;
    private readonly Lock _gunTurretGlobalLock = new();

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadGunTurret(IComponentBroker broker)
    {
        _gunTurretPdKey = _playerData.AllocatePlayerData<GunTurretPlayerData>();

        PlayerActionCallback.Register(broker, OnPlayerAction_GunTurret);
        ShipFreqChangeCallback.Register(broker, OnShipFreqChange_GunTurret);
        PlayerPositionPacketCallback.Register(broker, OnPlayerPosition_GunTurret);
        KillCallback.Register(broker, OnKill_GunTurret);
        NewPlayerCallback.Register(broker, OnNewPlayer_GunTurret);

        _commandManager.AddCommand(GunTurretResetTurretsCommand, Command_GunTurretReset);
        _commandManager.AddCommand(GunTurretAddTurretCommand, Command_GunTurretAdd);
        _commandManager.AddCommand(GunTurretClearTurretsCommand, Command_GunTurretClear);
        _commandManager.AddCommand(GunTurretListTurretsCommand, Command_GunTurretList);

        _mainloopTimer.SetTimer(OnTick_GunTurret, GunTurretTimerCadenceMs,
            GunTurretTimerCadenceMs, this);

        _gunTurretToken = broker.RegisterInterface<IGunTurret>(this);

        _logManager.LogM(LogLevel.Info, LogCategory, "GunTurret subsystem loaded.");
    }

    private void UnloadGunTurret(IComponentBroker broker)
    {
        if (_gunTurretToken is not null)
            broker.UnregisterInterface(ref _gunTurretToken);

        _mainloopTimer.ClearTimer(OnTick_GunTurret, this);

        _commandManager.RemoveCommand(GunTurretResetTurretsCommand, Command_GunTurretReset);
        _commandManager.RemoveCommand(GunTurretAddTurretCommand, Command_GunTurretAdd);
        _commandManager.RemoveCommand(GunTurretClearTurretsCommand, Command_GunTurretClear);
        _commandManager.RemoveCommand(GunTurretListTurretsCommand, Command_GunTurretList);

        PlayerActionCallback.Unregister(broker, OnPlayerAction_GunTurret);
        ShipFreqChangeCallback.Unregister(broker, OnShipFreqChange_GunTurret);
        PlayerPositionPacketCallback.Unregister(broker, OnPlayerPosition_GunTurret);
        KillCallback.Unregister(broker, OnKill_GunTurret);
        NewPlayerCallback.Unregister(broker, OnNewPlayer_GunTurret);

        // Tear down all turrets before unload.
        _playerData.Lock();
        try
        {
            foreach (Player p in _playerData.Players)
                ((IGunTurret)this).RemoveAllTurrets(p);
        }
        finally { _playerData.Unlock(); }

        _playerData.FreePlayerData(ref _gunTurretPdKey);
    }

    private void AttachGunTurret(Arena arena) { /* zone-wide */ }
    private void DetachGunTurret(Arena arena) { /* zone-wide */ }

    // -------------------------------------------------------------------------
    // IGunTurret IMPLEMENTATION
    // -------------------------------------------------------------------------

    bool IGunTurret.HasTurret(Player player, GunTurretInfo info)
    {
        if (player is null || info is null) return false;
        if (!player.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd)) return false;
        lock (_gunTurretGlobalLock)
        {
            foreach (var t in pd.Turrets)
                if (ReferenceEquals(t.Info, info)) return true;
        }
        return false;
    }

    bool IGunTurret.SetTurret(Player player, GunTurretInfo info)
    {
        if (player is null || info is null) return false;
        lock (_gunTurretGlobalLock)
        {
            ((IGunTurret)this).RemoveAllTurrets(player);
            return ((IGunTurret)this).AddTurret(player, info);
        }
    }

    bool IGunTurret.AddTurret(Player player, GunTurretInfo info)
    {
        if (player is null || info is null) return false;
        if (!player.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd)) return false;

        lock (_gunTurretGlobalLock)
        {
            if (((IGunTurret)this).HasTurret(player, info))
            {
                _logManager.LogP(LogLevel.Warn, LogCategory, player,
                    "Tried to add existing turret again, ignoring.");
                return true;
            }

            var t = new GunTurretEntry(info);
            pd.Turrets.Add(t);

            if (player.Ship != ShipType.Spec) ActivateGunTurret(player, t);
        }
        return true;
    }

    bool IGunTurret.RemoveTurret(Player player, GunTurretInfo info)
    {
        if (player is null || info is null) return false;
        if (!player.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd)) return false;

        lock (_gunTurretGlobalLock)
        {
            for (int i = 0; i < pd.Turrets.Count; i++)
            {
                if (ReferenceEquals(pd.Turrets[i].Info, info))
                {
                    DestroyGunTurret(player, pd.Turrets[i]);
                    pd.Turrets.RemoveAt(i);
                    return true;
                }
            }
        }
        return false;
    }

    bool IGunTurret.RemoveAllTurrets(Player player)
    {
        if (player is null) return false;
        if (!player.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd)) return false;

        lock (_gunTurretGlobalLock)
        {
            // Wave 5 snapshot — DestroyTurret → DisableTurret → EndFaked may
            // trigger Callback_NewPlayer(isNew=false) re-entrantly which walks
            // pd.Turrets to scrub stale Fake refs. Snapshot avoids
            // InvalidOperationException from concurrent mutation.
            var snapshot = pd.Turrets.ToArray();
            pd.Turrets.Clear();
            foreach (var t in snapshot) DestroyGunTurret(player, t);
        }
        return true;
    }

    IReadOnlyList<GunTurretInfo> IGunTurret.GetTurrets(Player player)
    {
        if (player is null) return Array.Empty<GunTurretInfo>();
        if (!player.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd))
            return Array.Empty<GunTurretInfo>();

        lock (_gunTurretGlobalLock)
        {
            var copy = new List<GunTurretInfo>(pd.Turrets.Count);
            foreach (var t in pd.Turrets) copy.Add(t.Info);
            return copy;
        }
    }

    // -------------------------------------------------------------------------
    // ACTIVATION / LIFECYCLE
    // -------------------------------------------------------------------------

    private void ActivateGunTurret(Player anchor, GunTurretEntry t)
    {
        if (anchor.Arena is null) return;

        Player? fakePlayer = _fake.CreateFakePlayer(t.Info.Name, anchor.Arena, t.Info.Ship, anchor.Freq);
        if (fakePlayer is null) return;

        t.Fake = fakePlayer;
        _game.Attach(fakePlayer, anchor);  // sends S2C_Turret packet
        SendGunTurretPosition(anchor, t, fireWeapon: false);
    }

    private void DisableGunTurret(GunTurretEntry t)
    {
        // Wave 5: null Fake BEFORE EndFaked so re-entrant callback finds null
        // and skips the freed Player ref.
        Player? fake = t.Fake;
        t.Fake = null;
        if (fake is not null) _fake.EndFaked(fake);
    }

    private void DestroyGunTurret(Player anchor, GunTurretEntry t) => DisableGunTurret(t);

    private void DisableAllGunTurrets(Player p)
    {
        if (!p.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd)) return;
        lock (_gunTurretGlobalLock)
        {
            foreach (var t in pd.Turrets)
                if (t.Fake is not null) DisableGunTurret(t);
        }
    }

    private void ActivateAllGunTurrets(Player p)
    {
        if (!p.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd)) return;
        lock (_gunTurretGlobalLock)
        {
            foreach (var t in pd.Turrets)
                if (t.Fake is null) ActivateGunTurret(p, t);
        }
    }

    // -------------------------------------------------------------------------
    // POSITION SYNC
    // -------------------------------------------------------------------------

    private void SendGunTurretPosition(Player anchor, GunTurretEntry t, bool fireWeapon)
    {
        if (t.Fake is null) return;

        ref readonly var anchorPos = ref anchor.Position;

        var pkt = new C2S_PositionPacket
        {
            Type = 0x03,
            Rotation = (sbyte)anchorPos.Rotation,
        };
        pkt.Status = PlayerPositionStatus.Ufo | PlayerPositionStatus.Cloak | PlayerPositionStatus.Stealth;

        int rot = anchorPos.Rotation + t.Info.RotationOffset;
        rot %= 40;
        if (rot < 0) rot += 40;

        if (t.Coast)
        {
            // Off-map sentinel for visual drift — matches ASSS coast behavior.
            pkt.X = 1;
            pkt.Y = 1;
            pkt.Time = (uint)Environment.TickCount;
        }
        else
        {
            // 2D rotation: dx = X*cos - Y*sin, dy = X*sin + Y*cos.
            // sin((θ+π/2)) = cos(θ) — the (rot+10) lookup IS the cos term.
            int dx = (int)Math.Round(t.Info.OffsetX * GunTurretSinTab[(rot + 10) % 40]
                                   - t.Info.OffsetY * GunTurretSinTab[rot % 40]);
            int dy = (int)Math.Round(t.Info.OffsetX * GunTurretSinTab[rot % 40]
                                   + t.Info.OffsetY * GunTurretSinTab[(rot + 10) % 40]);

            pkt.X = (short)(anchorPos.X + dx);
            pkt.Y = (short)(anchorPos.Y + dy);
            pkt.Rotation = (sbyte)rot;
            pkt.Time = (uint)anchorPos.Time;
        }

        pkt.XSpeed = (short)anchorPos.XSpeed;
        pkt.YSpeed = (short)anchorPos.YSpeed;
        pkt.Energy = (short)anchorPos.Energy;

        if (fireWeapon)
        {
            pkt.Weapon = new WeaponData { Type = t.Info.Weapon, Level = t.Info.WeaponLevel };
        }
        else
        {
            pkt.Weapon = new WeaponData { Type = WeaponCodes.Null };
        }

        _game.FakePosition(t.Fake, ref pkt);
        t.LastPacketTickMs = Environment.TickCount;
    }

    private void SendGunTurretPositionFromAnchorFire(Player anchor, GunTurretEntry t,
        in C2S_PositionPacket anchorFirePkt)
    {
        if (t.Fake is null) return;

        var pkt = new C2S_PositionPacket { Type = 0x03 };
        pkt.Status = PlayerPositionStatus.Ufo | PlayerPositionStatus.Cloak
            | PlayerPositionStatus.Stealth;

        int rot = anchorFirePkt.Rotation + t.Info.RotationOffset;
        rot %= 40;
        if (rot < 0) rot += 40;

        int dx = (int)Math.Round(t.Info.OffsetX * GunTurretSinTab[(rot + 10) % 40]
                               - t.Info.OffsetY * GunTurretSinTab[rot % 40]);
        int dy = (int)Math.Round(t.Info.OffsetX * GunTurretSinTab[rot % 40]
                               + t.Info.OffsetY * GunTurretSinTab[(rot + 10) % 40]);

        pkt.X = (short)(anchorFirePkt.X + dx);
        pkt.Y = (short)(anchorFirePkt.Y + dy);
        pkt.Rotation = (sbyte)rot;
        pkt.Time = anchorFirePkt.Time;
        pkt.XSpeed = anchorFirePkt.XSpeed;
        pkt.YSpeed = anchorFirePkt.YSpeed;
        pkt.Energy = anchorFirePkt.Energy;
        pkt.Weapon = new WeaponData { Type = t.Info.Weapon, Level = t.Info.WeaponLevel };

        _game.FakePosition(t.Fake, ref pkt);
        t.LastPacketTickMs = Environment.TickCount;
    }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    private void OnPlayerAction_GunTurret(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.Disconnect)
            ((IGunTurret)this).RemoveAllTurrets(player);
        else if (action == PlayerAction.LeaveArena)
            DisableAllGunTurrets(player);
        else if (action == PlayerAction.EnterGame && player.Ship != ShipType.Spec)
            ActivateAllGunTurrets(player);
    }

    private void OnShipFreqChange_GunTurret(Player player, ShipType newShip,
        ShipType oldShip, short newFreq, short oldFreq)
    {
        if (newShip == ShipType.Spec && oldShip != ShipType.Spec)
            DisableAllGunTurrets(player);
        else if (oldShip == ShipType.Spec && newShip != ShipType.Spec)
            ActivateAllGunTurrets(player);
        else if (newShip != ShipType.Spec && newFreq != oldFreq)
        {
            // Freq change while flying — turrets follow.
            if (!player.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd)) return;
            lock (_gunTurretGlobalLock)
            {
                foreach (var t in pd.Turrets)
                {
                    if (t.Fake is null) continue;
                    _game.SetFreq(t.Fake, newFreq);
                    SendGunTurretPosition(player, t, fireWeapon: false);
                }
            }
        }
    }

    private void OnPlayerPosition_GunTurret(Player player,
        ref readonly C2S_PositionPacket pos, ref readonly ExtraPositionData extra, bool hasExtra)
    {
        WeaponCodes anchorWeapon = pos.Weapon.Type;
        bool anchorIsBullet = anchorWeapon == WeaponCodes.Bullet
            || anchorWeapon == WeaponCodes.BounceBullet;
        bool anchorIsBomb = anchorWeapon == WeaponCodes.Bomb
            || anchorWeapon == WeaponCodes.ProxBomb;
        if (!anchorIsBullet && !anchorIsBomb) return;

        if (!player.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd)) return;

        lock (_gunTurretGlobalLock)
        {
            foreach (var t in pd.Turrets)
            {
                if (t.Fake is null || t.Coast) continue;

                WeaponCodes tw = t.Info.Weapon;
                bool turretIsBullet = tw == WeaponCodes.Bullet || tw == WeaponCodes.BounceBullet;
                bool turretIsBomb = tw == WeaponCodes.Bomb || tw == WeaponCodes.ProxBomb;

                // Match anchor's shot class — bullets fire bullet turrets, bombs fire bomb turrets.
                if (anchorIsBullet && !turretIsBullet) continue;
                if (anchorIsBomb && !turretIsBomb) continue;

                SendGunTurretPositionFromAnchorFire(player, t, pos);
            }
        }
    }

    private void OnKill_GunTurret(Arena arena, Player killer, Player killed,
        short bounty, short flagCount, short points, Prize green)
    {
        if (!killed.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd)) return;
        lock (_gunTurretGlobalLock)
        {
            foreach (var t in pd.Turrets)
            {
                t.Coast = true;
                t.StartCoastTickMs = Environment.TickCount;
                if (t.Fake is not null) _game.Attach(t.Fake, null);  // detach during coast
            }
        }
    }

    private void OnNewPlayer_GunTurret(Player newPlayer, bool isNew)
    {
        if (isNew) return;

        // Player object freed. Scrub any turret entries pointing at this fake.
        _playerData.Lock();
        try
        {
            foreach (Player p in _playerData.Players)
            {
                if (!p.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd)) continue;
                lock (_gunTurretGlobalLock)
                {
                    for (int i = pd.Turrets.Count - 1; i >= 0; i--)
                    {
                        if (ReferenceEquals(pd.Turrets[i].Fake, newPlayer))
                        {
                            pd.Turrets[i].Fake = null;
                            pd.Turrets.RemoveAt(i);
                        }
                    }
                }
            }
        }
        finally { _playerData.Unlock(); }
    }

    // -------------------------------------------------------------------------
    // TIMER — coast resolution + PPK refresh
    // -------------------------------------------------------------------------

    private bool OnTick_GunTurret()
    {
        int now = Environment.TickCount;

        _playerData.Lock();
        try
        {
            foreach (Player p in _playerData.Players)
            {
                if (!p.TryGetExtraData(_gunTurretPdKey, out GunTurretPlayerData? pd)) continue;
                if (p.Ship == ShipType.Spec) continue;

                lock (_gunTurretGlobalLock)
                {
                    foreach (var t in pd.Turrets)
                    {
                        if (t.Coast)
                        {
                            if (now - t.StartCoastTickMs > GunTurretCoastTimeMs)
                            {
                                t.Coast = false;
                                if (t.Fake is not null) _game.Attach(t.Fake, p);
                            }
                        }
                        if (!t.Coast && t.Fake is not null
                            && now - t.LastPacketTickMs > GunTurretPpkIntervalMs)
                        {
                            SendGunTurretPosition(p, t, fireWeapon: false);
                        }
                    }
                }
            }
        }
        finally { _playerData.Unlock(); }
        return true;
    }

    // -------------------------------------------------------------------------
    // COMMANDS
    // -------------------------------------------------------------------------

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Sysop: clear all turrets in this arena.")]
    private void Command_GunTurretReset(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        Arena? arena = player.Arena;
        if (arena is null) return;

        _playerData.Lock();
        try
        {
            foreach (Player p in _playerData.Players)
            {
                if (p.Arena == arena) ((IGunTurret)this).RemoveAllTurrets(p);
            }
        }
        finally { _playerData.Unlock(); }

        _chat.SendMessage(player, "All turrets in this arena cleared.");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = "<ship> <hardpoint> <weapon> <level>",
        Description = "Sysop debug: add a wing-mounted gun turret. " +
                      "ship=Warbird/etc, hardpoint=LeftWing/RightWing, " +
                      "weapon=Bullet/BounceBullet/Bomb/ProxBomb/Burst/Thor, level=0-3.")]
    private void Command_GunTurretAdd(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        Span<Range> ranges = stackalloc Range[4];
        int n = parameters.Split(ranges, ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (n < 4)
        {
            _chat.SendMessage(player, "Usage: ?addturret <ship> <hardpoint> <weapon> <level>");
            return;
        }

        if (!Enum.TryParse(parameters[ranges[0]], ignoreCase: true, out ShipType ship)
            || ship == ShipType.Spec)
        { _chat.SendMessage(player, "Bad ship."); return; }

        if (!Enum.TryParse(parameters[ranges[1]], ignoreCase: true, out Hardpoint hp))
        { _chat.SendMessage(player, "Hardpoint must be LeftWing or RightWing."); return; }

        if (!Enum.TryParse(parameters[ranges[2]], ignoreCase: true, out WeaponCodes weapon))
        { _chat.SendMessage(player, "Bad weapon."); return; }

        if (!byte.TryParse(parameters[ranges[3]], out byte level) || level > 3)
        { _chat.SendMessage(player, "Level must be 0-3."); return; }

        (int ox, int oy) = Hardpoints.Offset(ship, hp);
        var info = new GunTurretInfo($"{hp}-{Environment.TickCount & 0xFFFF}",
            ship, ox, oy, 0, weapon, level);
        if (((IGunTurret)this).AddTurret(player, info))
            _chat.SendMessage(player,
                $"Added {ship} turret on {hp} firing {weapon} L{level} at offset ({ox},{oy}).");
        else _chat.SendMessage(player, "Failed to add turret.");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Remove all gun turrets from yourself.")]
    private void Command_GunTurretClear(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        ((IGunTurret)this).RemoveAllTurrets(player);
        _chat.SendMessage(player, "Your turrets cleared.");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "List your active gun turrets.")]
    private void Command_GunTurretList(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        var turrets = ((IGunTurret)this).GetTurrets(player);
        if (turrets.Count == 0) { _chat.SendMessage(player, "No turrets."); return; }
        _chat.SendMessage(player, $"--- Your turrets ({turrets.Count}) ---");
        foreach (var t in turrets)
            _chat.SendMessage(player,
                $"  {t.Name}: {t.Ship} weapon={t.Weapon} L{t.WeaponLevel} " +
                $"offset=({t.OffsetX},{t.OffsetY}) rot+{t.RotationOffset}");
    }
}
