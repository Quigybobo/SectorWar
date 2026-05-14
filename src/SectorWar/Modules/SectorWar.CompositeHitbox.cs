using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System.Threading;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — CompositeHitbox subsystem (Layer 2 of Modular Capital Ship).
// =============================================================================
//
// PURPOSE
// -------
// Spawns invisible turret fake-players at offsets around an active capital
// ship's anchor. Each fake provides a Radius collision circle. Damage to
// ANY turret routes via IDamage's FakeDamageFunc to a SHARED HP pool. When
// HP <= 0, the central anchor is killed via IGame.FakeKill.
//
// SOURCE
// ------
// Standalone module `Modules/CompositeHitbox.cs` stays as a library copy.
// Pairs with ModularShip (visual layer, already merged) and Damage (server-
// side bullet collision, merged later in this batch).
//
// LAYOUT
// ------
// Default 6 turrets matching the v2 lozenge capital silhouette: forward
// sensor + 2 wings + 2 hull + aft engine. Each 16-px radius.
//
// LOCKSTEP POSITION SYNC
// ----------------------
// 100Hz tick repositions each turret-fake to anchor + rotate(offset). Time-
// based extrapolation matches ModularShip's smoothness fix.
//
// FAKE INVISIBILITY
// -----------------
// Cloak | Stealth | UFO status bits hide the ship sprite + radar blip.
//
// SAME-FREQ TRICK
// ---------------
// Turrets spawn on the anchor's freq so the anchor's own bullets pass
// through (friendly-fire skip). Enemy bullets register because of the
// freq mismatch. On freq change, OnShipFreqChange_CompositeHitbox calls
// IGame.SetFreq on every turret-fake to maintain the invariant.
//
// COMMANDS
//   ?capitaltest [hp]   — spawn 6-turret hitbox shell (default HP=1000)
//   ?capitalstatus      — report HP / max HP for active capital
//   ?capitalclear       — tear down the hitbox shell
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave 7: freq-change hook re-syncs turret freqs via IGame.SetFreq;
// BuildCapital partial-failure rollback; turret-fake teardown clears Fake
// field BEFORE EndFaked; timer-based FakeKill guards disconnected anchor.
// =============================================================================

public sealed partial class SectorWar : ICompositeHitbox
{
    private const string CompositeHitboxTestCommand = "capitaltest";
    private const string CompositeHitboxStatusCommand = "capitalstatus";
    private const string CompositeHitboxClearCommand = "capitalclear";
    private const int CompositeHitboxTickIntervalMs = 10;
    private const int CompositeHitboxDefaultTurretRadius = 16;
    private const int CompositeHitboxLeadMillis = 50;
    private const int CompositeHitboxMaxExtrapolateMs = 500;

    /// <summary>(Slot, OffsetX, OffsetY, Radius). Forward = -Y. Match the
    /// v2 lozenge capital LVZ silhouette.</summary>
    private record struct CompositeHitboxNode(byte Slot, int OffsetX, int OffsetY, int Radius);

    private static readonly CompositeHitboxNode[] CompositeHitboxDefaultLayout =
    {
        new(0,   0, -64, CompositeHitboxDefaultTurretRadius),  // forward sensor
        new(1, -48, -24, CompositeHitboxDefaultTurretRadius),  // port wing
        new(2, +48, -24, CompositeHitboxDefaultTurretRadius),  // starboard wing
        new(3, -48, +20, CompositeHitboxDefaultTurretRadius),  // port hull
        new(4, +48, +20, CompositeHitboxDefaultTurretRadius),  // starboard hull
        new(5,   0, +64, CompositeHitboxDefaultTurretRadius),  // engine
    };

    internal sealed class CompositeHitboxCapitalShip
    {
        public Player Anchor = null!;
        public List<CompositeHitboxTurretFake> Turrets = new();
        public int Hp;
        public int MaxHp;
        /// <summary>Set true after the kill timer fires; prevents double-kill
        /// from concurrent damage callbacks.</summary>
        public bool Dying;
    }

    internal sealed class CompositeHitboxTurretFake
    {
        public Player? Fake;
        public int OffsetX;
        public int OffsetY;
        public C2S_PositionPacket LastPos;
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private InterfaceRegistrationToken<ICompositeHitbox>? _compositeHitboxToken;
    private IComponentBroker? _compositeHitboxBroker;

    private readonly Dictionary<Player, CompositeHitboxCapitalShip> _compositeHitboxActiveShips = new();
    private readonly Lock _compositeHitboxShipsLock = new();

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadCompositeHitbox(IComponentBroker broker)
    {
        _compositeHitboxBroker = broker;

        PlayerActionCallback.Register(broker, OnPlayerAction_CompositeHitbox);
        ShipFreqChangeCallback.Register(broker, OnShipFreqChange_CompositeHitbox);

        _mainloopTimer.SetTimer(OnTick_CompositeHitbox, CompositeHitboxTickIntervalMs,
            CompositeHitboxTickIntervalMs, this);

        _compositeHitboxToken = broker.RegisterInterface<ICompositeHitbox>(this);

        _logManager.LogM(LogLevel.Info, LogCategory,
            $"CompositeHitbox subsystem loaded ({CompositeHitboxDefaultLayout.Length} turrets/capital).");
    }

    private void UnloadCompositeHitbox(IComponentBroker broker)
    {
        if (_compositeHitboxToken is not null)
            broker.UnregisterInterface(ref _compositeHitboxToken);

        _mainloopTimer.ClearTimer(OnTick_CompositeHitbox, this);

        PlayerActionCallback.Unregister(broker, OnPlayerAction_CompositeHitbox);
        ShipFreqChangeCallback.Unregister(broker, OnShipFreqChange_CompositeHitbox);

        // Tear down all active capitals on the way out.
        Player[] activePlayers;
        lock (_compositeHitboxShipsLock)
        {
            activePlayers = _compositeHitboxActiveShips.Keys.ToArray();
        }
        foreach (var p in activePlayers) ClearCompositeHitboxCapital(p, killAnchor: false);

        _compositeHitboxBroker = null;
    }

    private void AttachCompositeHitbox(Arena arena)
    {
        _commandManager.AddCommand(CompositeHitboxTestCommand, Command_CompositeHitboxTest, arena);
        _commandManager.AddCommand(CompositeHitboxStatusCommand, Command_CompositeHitboxStatus, arena);
        _commandManager.AddCommand(CompositeHitboxClearCommand, Command_CompositeHitboxClear, arena);
    }

    private void DetachCompositeHitbox(Arena arena)
    {
        _commandManager.RemoveCommand(CompositeHitboxTestCommand, Command_CompositeHitboxTest, arena);
        _commandManager.RemoveCommand(CompositeHitboxStatusCommand, Command_CompositeHitboxStatus, arena);
        _commandManager.RemoveCommand(CompositeHitboxClearCommand, Command_CompositeHitboxClear, arena);
    }

    // -------------------------------------------------------------------------
    // COMMANDS
    // -------------------------------------------------------------------------

    private void Command_CompositeHitboxTest(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null) { _chat.SendMessage(player, "Must be in an arena."); return; }
        if (player.Ship == ShipType.Spec)
        {
            _chat.SendMessage(player, "Get in a ship first."); return;
        }
        int hp = 1000;
        if (!parameters.IsEmpty && int.TryParse(parameters.Trim(), out int parsedHp))
            hp = Math.Clamp(parsedHp, 100, 100000);
        BuildCompositeHitboxCapital(player, hp);
    }

    private void Command_CompositeHitboxStatus(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        CompositeHitboxCapitalShip? ship;
        lock (_compositeHitboxShipsLock)
        {
            _compositeHitboxActiveShips.TryGetValue(player, out ship);
        }
        if (ship is null) { _chat.SendMessage(player, "No active capital."); return; }
        _chat.SendMessage(player,
            $"Capital: HP {ship.Hp}/{ship.MaxHp}, turrets {ship.Turrets.Count}/{CompositeHitboxDefaultLayout.Length}.");
    }

    private void Command_CompositeHitboxClear(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        bool removed = ClearCompositeHitboxCapital(player, killAnchor: false);
        _chat.SendMessage(player, removed ? "Capital hitbox cleared." : "No capital active.");
    }

    // -------------------------------------------------------------------------
    // ICompositeHitbox IMPLEMENTATION
    // -------------------------------------------------------------------------

    void ICompositeHitbox.BuildCapital(Player anchor, int hp) => BuildCompositeHitboxCapital(anchor, hp);
    bool ICompositeHitbox.ClearCapital(Player anchor, bool killAnchor)
        => ClearCompositeHitboxCapital(anchor, killAnchor);

    // -------------------------------------------------------------------------
    // CORE BUILD / CLEAR
    // -------------------------------------------------------------------------

    public void BuildCompositeHitboxCapital(Player anchor, int hp)
    {
        if (anchor.Arena is null) return;
        if (anchor.Ship == ShipType.Spec) return;

        IDamage? damage = _compositeHitboxBroker?.GetInterface<IDamage>();
        if (damage is null)
        {
            _chat.SendMessage(anchor, "Damage subsystem not loaded — can't build hitbox.");
            return;
        }

        CompositeHitboxCapitalShip? ship = null;
        try
        {
            // Tear down any existing capital for this anchor.
            ClearCompositeHitboxInternal(anchor, killAnchor: false, damage);

            ship = new CompositeHitboxCapitalShip { Anchor = anchor, Hp = hp, MaxHp = hp };
            short anchorFreq = anchor.Freq;

            foreach (var node in CompositeHitboxDefaultLayout)
            {
                string fakeName = $"~CapHB-{anchor.Name}-{node.Slot}";
                if (fakeName.Length > 19) fakeName = fakeName[..19];

                Player? fake = _fake.CreateFakePlayer(fakeName, anchor.Arena,
                    ShipType.Warbird, anchorFreq);
                if (fake is null) continue;

                if (_clientSettings.TryGetSettingsIdentifier("Warbird", "Radius",
                    out var radiusId))
                {
                    _clientSettings.OverrideSetting(fake, radiusId, node.Radius);
                    _clientSettings.SendClientSettings(fake);
                }

                var pos = ComputeCompositeHitboxFakePosition(anchor, node.OffsetX, node.OffsetY);

                var turret = new CompositeHitboxTurretFake
                {
                    Fake = fake,
                    OffsetX = node.OffsetX,
                    OffsetY = node.OffsetY,
                    LastPos = pos,
                };
                ship.Turrets.Add(turret);

                _game.FakePosition(fake, ref pos);

                damage.AddFake(fake, ref pos, manageEnergy: false,
                    killFunc: null,        // shared HP pool, no per-turret death
                    respawnFunc: null,
                    damageFunc: OnCompositeHitboxTurretDamaged,
                    closure: ship);
            }

            lock (_compositeHitboxShipsLock) { _compositeHitboxActiveShips[anchor] = ship; }

            _chat.SendMessage(anchor,
                $"Capital hitbox built: {ship.Turrets.Count} turrets, HP {ship.Hp}.");
        }
        catch (Exception ex)
        {
            // Wave-7 partial-failure rollback so we don't leak orphan fakes
            // or IDamage registrations.
            _logManager.LogP(LogLevel.Error, LogCategory, anchor,
                $"BuildCapital threw mid-loop: {ex}. Tearing down partial state.");
            if (ship is not null)
            {
                foreach (var t in ship.Turrets)
                {
                    if (t.Fake is null) continue;
                    Player f = t.Fake;
                    t.Fake = null;
                    try { damage.RemoveFake(f); } catch { }
                    try { _fake.EndFaked(f); } catch { }
                }
                ship.Turrets.Clear();
            }
            lock (_compositeHitboxShipsLock) { _compositeHitboxActiveShips.Remove(anchor); }
        }
        finally
        {
            _compositeHitboxBroker?.ReleaseInterface(ref damage);
        }
    }

    public bool ClearCompositeHitboxCapital(Player anchor, bool killAnchor)
    {
        IDamage? damage = _compositeHitboxBroker?.GetInterface<IDamage>();
        try { return ClearCompositeHitboxInternal(anchor, killAnchor, damage); }
        finally { _compositeHitboxBroker?.ReleaseInterface(ref damage); }
    }

    private bool ClearCompositeHitboxInternal(Player anchor, bool killAnchor, IDamage? damage)
    {
        CompositeHitboxCapitalShip? ship;
        lock (_compositeHitboxShipsLock)
        {
            if (!_compositeHitboxActiveShips.TryGetValue(anchor, out ship)) return false;
            _compositeHitboxActiveShips.Remove(anchor);
        }

        foreach (var t in ship.Turrets)
        {
            // Wave-7 snapshot-then-null pattern.
            Player? fake = t.Fake;
            t.Fake = null;
            if (fake is null) continue;
            damage?.RemoveFake(fake);
            _fake.EndFaked(fake);
        }
        ship.Turrets.Clear();

        if (killAnchor && anchor.Arena is not null)
            _game.FakeKill(anchor, anchor, pts: 0, flags: 0);
        return true;
    }

    // -------------------------------------------------------------------------
    // DAMAGE CALLBACK
    // -------------------------------------------------------------------------

    private void OnCompositeHitboxTurretDamaged(Player fake, Player firedBy, int dist,
        int damage, WeaponCodes wtype, int level, bool bouncing, int empTime, object? closure)
    {
        if (closure is not CompositeHitboxCapitalShip ship) return;
        if (ship.Dying) return;

        bool kill = false;
        lock (_compositeHitboxShipsLock)
        {
            if (ship.Dying) return;
            ship.Hp -= damage;
            if (ship.Hp <= 0)
            {
                ship.Dying = true;
                ship.Hp = 0;
                kill = true;
            }
        }

        _chat.SendMessage(ship.Anchor,
            $"Capital hit by {firedBy.Name}: -{damage} HP, remaining {ship.Hp}/{ship.MaxHp}.");

        if (kill)
        {
            _chat.SendMessage(ship.Anchor, "Capital hull breach — abandon ship.");
            // Defer the actual kill via a 1ms one-shot timer so we don't
            // re-enter damage processing inside the damage callback.
            CompositeHitboxCapitalShip dyingShip = ship;
            _mainloopTimer.SetTimer(() =>
            {
                Player anchor = dyingShip.Anchor;
                if (anchor.Status == PlayerState.Playing && anchor.Arena is not null)
                    ClearCompositeHitboxCapital(anchor, killAnchor: true);
                else
                    ClearCompositeHitboxCapital(anchor, killAnchor: false);
                return false;
            }, 1, 0, this);
        }
    }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    private void OnPlayerAction_CompositeHitbox(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.LeaveArena || action == PlayerAction.Disconnect)
            ClearCompositeHitboxCapital(player, killAnchor: false);
    }

    private void OnShipFreqChange_CompositeHitbox(
        Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
    {
        if (newShip == ShipType.Spec)
        {
            ClearCompositeHitboxCapital(player, killAnchor: false);
            return;
        }

        // Wave 7: turret-fakes were created on anchor's old freq. After the
        // change, anchor's own bullets would now register as enemy fire
        // against their own capital. Update each fake's freq to maintain the
        // friendly-fire skip in Damage.PointCollision.
        if (newFreq == oldFreq) return;
        CompositeHitboxCapitalShip? ship;
        lock (_compositeHitboxShipsLock)
        {
            if (!_compositeHitboxActiveShips.TryGetValue(player, out ship)) return;
        }
        foreach (var t in ship.Turrets)
        {
            if (t.Fake is null) continue;
            try { _game.SetFreq(t.Fake, newFreq); }
            catch (Exception ex)
            {
                _logManager.LogP(LogLevel.Warn, LogCategory, player,
                    $"Failed to update turret-fake freq: {ex.Message}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // POSITION TICK
    // -------------------------------------------------------------------------

    private bool OnTick_CompositeHitbox()
    {
        Player[] anchors;
        lock (_compositeHitboxShipsLock)
        {
            if (_compositeHitboxActiveShips.Count == 0) return true;
            anchors = _compositeHitboxActiveShips.Keys.ToArray();
        }

        foreach (var anchor in anchors)
        {
            if (anchor.Arena is null) continue;
            if (anchor.Ship == ShipType.Spec) continue;

            CompositeHitboxCapitalShip? ship;
            lock (_compositeHitboxShipsLock)
            {
                if (!_compositeHitboxActiveShips.TryGetValue(anchor, out ship)) continue;
            }

            foreach (var t in ship.Turrets)
            {
                if (t.Fake is null) continue;
                if (t.Fake.Arena != anchor.Arena) continue;
                t.LastPos = ComputeCompositeHitboxFakePosition(anchor, t.OffsetX, t.OffsetY);
                C2S_PositionPacket pos = t.LastPos;
                _game.FakePosition(t.Fake, ref pos);
            }
        }
        return true;
    }

    /// <summary>
    /// World position = anchor + rotate(offset, anchor.Rotation), with time-
    /// based extrapolation matching ModularShip's smoothness fix. Status bits
    /// (Cloak | Stealth | UFO) hide the fake.
    /// </summary>
    private static C2S_PositionPacket ComputeCompositeHitboxFakePosition(
        Player anchor, int offsetX, int offsetY)
    {
        ref readonly var apos = ref anchor.Position;

        ServerTick now = ServerTick.Now;
        int ticksElapsed = now - apos.Time;
        if (ticksElapsed < 0) ticksElapsed = 0;
        int extrapMs = ticksElapsed * 10 + CompositeHitboxLeadMillis;
        if (extrapMs > CompositeHitboxMaxExtrapolateMs)
            extrapMs = CompositeHitboxMaxExtrapolateMs;

        int extrapX = apos.X + (int)((apos.XSpeed * (long)extrapMs) / 10_000);
        int extrapY = apos.Y + (int)((apos.YSpeed * (long)extrapMs) / 10_000);

        int rot = ((apos.Rotation % 40) + 40) % 40;
        double theta = rot * (Math.PI * 2.0 / 40.0);
        double cosT = Math.Cos(theta);
        double sinT = Math.Sin(theta);

        double wxD = offsetX * cosT - offsetY * sinT;
        double wyD = offsetX * sinT + offsetY * cosT;

        int worldX = extrapX + (int)Math.Round(wxD);
        int worldY = extrapY + (int)Math.Round(wyD);

        C2S_PositionPacket pos = default;
        pos.Type = 0x03;
        pos.X = (short)Math.Clamp(worldX, 0, short.MaxValue);
        pos.Y = (short)Math.Clamp(worldY, 0, short.MaxValue);
        pos.Rotation = apos.Rotation;
        pos.XSpeed = (short)Math.Clamp(apos.XSpeed, short.MinValue, short.MaxValue);
        pos.YSpeed = (short)Math.Clamp(apos.YSpeed, short.MinValue, short.MaxValue);
        pos.Energy = 1000;
        pos.Time = ServerTick.Now;
        pos.Status = PlayerPositionStatus.Cloak | PlayerPositionStatus.Stealth
            | PlayerPositionStatus.Ufo;
        return pos;
    }
}
