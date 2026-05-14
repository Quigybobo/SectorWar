using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System.Threading;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — ModularShip subsystem (Layer 1 of Modular Capital Ship).
// =============================================================================
//
// PURPOSE
// -------
// One large LVZ capital sprite per active player, tracking position + rotation
// every position packet plus a 100Hz extrapolation tick. The sprite has a
// transparent bridge cutout where the player's actual ship sprite renders
// through — illusion of being the bridge of a much larger vessel.
//
// SOURCE
// ------
// Standalone module `Modules/ModularShip.cs` stays as a library copy. See
// docs/MODULAR_CAPITAL_SHIP.md for the three-layer architecture (visual /
// hitbox / damage decals).
//
// LVZ ART CONTRACT (must match tools/lvz_warbird_capital.py)
// ----------------------------------------------------------
//   - Image IDs 0..4   : 5 hull-ring demos (PoC, not used by this subsystem)
//   - Image IDs 5..44  : 40 rotation frames (9° each, frame 0 = north)
//   - Object pool 7000..7063 : 64 player slots, mode=ServerControlled
//
// PACKET BUDGET
// -------------
//   - Stationary ship: 0 packets per tick (change detection).
//   - Moving + rotating: ~20 pkt/sec (Toggle once, SetPosition + SetImage on
//     integer-pixel position changes / per-9° rotation step).
//   - 64 active capitals: ~1280 pkt/sec total. Within bandwidth budget.
//
// EXTRAPOLATION STRATEGY
// ----------------------
// Position packets arrive at ~10 Hz. Between them we extrapolate from the
// packet's velocity, with a small LeadMillis ahead-of-render offset to mask
// network latency. MaxExtrapolateMs caps overshoot during lag spikes.
// 100 Hz tick + change detection = smooth motion for moving ships, zero
// traffic for parked ones.
//
// HITBOX INTEGRATION (Phase 3)
// ----------------------------
// `?modulebuild` ALSO calls ICompositeHitbox.BuildCapital so bullets crossing
// the visible silhouette deal damage. ICompositeHitbox is optional — visual
// works without it (just no real hitbox).
//
// RUNTIME OWNERSHIP
//   - Owned state: 64-slot Player?[] pool + per-player PlayerState dict
//                  (lock-protected).
//   - Conf keys read: NONE (defaults hardcoded; tunable via inventory items).
//   - Persisted data: NONE (sessions reset on disconnect).
//   - Fakes registered: NONE (Layer 2 CompositeHitbox owns the fakes).
//   - Timers scheduled: 100 Hz IMainloopTimer.
//   - Commands registered: cmd_modulebuild, cmd_moduleclear.
//   - Broker interfaces published: NONE.
//
// CALLBACKS HOOKED (zone-wide)
//   - PlayerActionCallback         → cleanup on Leave/Disconnect
//   - ShipFreqChangeCallback       → cleanup on Spec
//   - PlayerPositionPacketCallback → drive overlay
//
// THREADING
// ---------
// Position-packet callback fires on the network worker thread; everything
// else is mainloop. _poolLock guards shared state. ApplyState releases the
// lock before sending LVZ packets to avoid blocking the network thread.
// =============================================================================

public sealed partial class SectorWar
{
    private const string ModularShipBuildCommand = "modulebuild";
    private const string ModularShipClearCommand = "moduleclear";

    private const short ModularShipPoolBase = 7000;
    private const int ModularShipPoolSize = 64;
    private const byte ModularShipCapitalImageBase = 5;
    private const int ModularShipCapitalCanvasHalf = 82;

    /// <summary>Lead-ahead-of-render offset masking network latency.</summary>
    private const int ModularShipLeadMillis = 50;

    /// <summary>Cap on extrapolation distance (lag-spike safety).</summary>
    private const int ModularShipMaxExtrapolateMs = 500;

    /// <summary>100 Hz tick — matches Continuum's native cadence.</summary>
    private const int ModularShipTimerCadenceMs = 10;

    /// <summary>Default capital HP. Inventory hull-tier items will modify
    /// this in a later phase.</summary>
    private const int ModularShipDefaultCapitalHp = 5000;

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private readonly Player?[] _modularShipSlotOwner = new Player?[ModularShipPoolSize];
    private readonly Lock _modularShipPoolLock = new();

    private sealed class ModularShipPlayerState
    {
        public int Slot;
        public short LastX = short.MinValue;
        public short LastY = short.MinValue;
        public byte LastImageId = 0xFF;
        public bool LastVisible;
    }

    private readonly Dictionary<Player, ModularShipPlayerState> _modularShipPlayerState = new();

    /// <summary>Cached broker for ICompositeHitbox lookup in build/clear.</summary>
    private IComponentBroker? _modularShipBroker;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadModularShip(IComponentBroker broker)
    {
        _modularShipBroker = broker;

        PlayerActionCallback.Register(broker, OnPlayerAction_ModularShip);
        ShipFreqChangeCallback.Register(broker, OnShipFreqChange_ModularShip);
        PlayerPositionPacketCallback.Register(broker, OnPlayerPosition_ModularShip);

        _mainloopTimer.SetTimer(OnTick_ModularShip, ModularShipTimerCadenceMs,
            ModularShipTimerCadenceMs, this);

        _logManager.LogM(LogLevel.Info, LogCategory,
            $"ModularShip subsystem loaded (pool {ModularShipPoolSize}, tick {ModularShipTimerCadenceMs}ms).");
    }

    private void UnloadModularShip(IComponentBroker broker)
    {
        _mainloopTimer.ClearTimer(OnTick_ModularShip, this);

        PlayerActionCallback.Unregister(broker, OnPlayerAction_ModularShip);
        ShipFreqChangeCallback.Unregister(broker, OnShipFreqChange_ModularShip);
        PlayerPositionPacketCallback.Unregister(broker, OnPlayerPosition_ModularShip);

        // Hide all active capitals on the way out.
        var toHide = new List<(Arena Arena, short ObjId)>();
        lock (_modularShipPoolLock)
        {
            foreach (var (player, state) in _modularShipPlayerState)
            {
                if (state.LastVisible && player.Arena is Arena a)
                    toHide.Add((a, (short)(ModularShipPoolBase + state.Slot)));
            }
            _modularShipPlayerState.Clear();
            for (int i = 0; i < ModularShipPoolSize; i++) _modularShipSlotOwner[i] = null;
        }
        foreach (var (arena, objId) in toHide)
            _lvzObjects.Toggle(arena, objId, false);

        _modularShipBroker = null;
    }

    private void AttachModularShip(Arena arena)
    {
        _commandManager.AddCommand(ModularShipBuildCommand, Command_ModularShipBuild, arena);
        _commandManager.AddCommand(ModularShipClearCommand, Command_ModularShipClear, arena);
    }

    private void DetachModularShip(Arena arena)
    {
        _commandManager.RemoveCommand(ModularShipBuildCommand, Command_ModularShipBuild, arena);
        _commandManager.RemoveCommand(ModularShipClearCommand, Command_ModularShipClear, arena);
    }

    // -------------------------------------------------------------------------
    // COMMANDS
    // -------------------------------------------------------------------------

    private void Command_ModularShipBuild(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null) { _chat.SendMessage(player, "Must be in an arena."); return; }
        if (player.Ship == ShipType.Spec)
        {
            _chat.SendMessage(player, "Must be in a ship (not spec)."); return;
        }

        bool hadExisting;
        int slot;
        lock (_modularShipPoolLock)
        {
            hadExisting = _modularShipPlayerState.ContainsKey(player);
            if (hadExisting) ReleaseModularShipPlayerInternal(player, hideOverlays: true);

            slot = AllocateModularShipSlotInternal(player);
            if (slot < 0)
            {
                _chat.SendMessage(player, "All capital ship slots are in use.");
                return;
            }
            _modularShipPlayerState[player] = new ModularShipPlayerState { Slot = slot };
        }

        // Force-render the first frame so the capital appears immediately.
        ApplyModularShipState(player, force: true);

        // Phase 3 hitbox integration. CompositeHitbox is optional.
        ICompositeHitbox? hitbox = _modularShipBroker?.GetInterface<ICompositeHitbox>();
        try { hitbox?.BuildCapital(player, ModularShipDefaultCapitalHp); }
        finally { _modularShipBroker?.ReleaseInterface(ref hitbox); }

        _chat.SendMessage(player,
            hadExisting ? $"Capital ship rebuilt (slot {slot})."
                        : $"Capital ship active (slot {slot}).");
    }

    private void Command_ModularShipClear(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        bool removed;
        lock (_modularShipPoolLock)
        {
            removed = _modularShipPlayerState.ContainsKey(player);
            if (removed) ReleaseModularShipPlayerInternal(player, hideOverlays: true);
        }

        ICompositeHitbox? hitbox = _modularShipBroker?.GetInterface<ICompositeHitbox>();
        try { hitbox?.ClearCapital(player, killAnchor: false); }
        finally { _modularShipBroker?.ReleaseInterface(ref hitbox); }

        _chat.SendMessage(player, removed ? "Capital ship cleared." : "No capital ship active.");
    }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    private void OnPlayerAction_ModularShip(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.Disconnect || action == PlayerAction.LeaveArena)
        {
            lock (_modularShipPoolLock)
            {
                if (_modularShipPlayerState.ContainsKey(player))
                    ReleaseModularShipPlayerInternal(player, hideOverlays: arena is not null);
            }
        }
    }

    private void OnShipFreqChange_ModularShip(
        Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
    {
        if (newShip == ShipType.Spec)
        {
            lock (_modularShipPoolLock)
            {
                if (_modularShipPlayerState.ContainsKey(player))
                    ReleaseModularShipPlayerInternal(player, hideOverlays: true);
            }
        }
    }

    private void OnPlayerPosition_ModularShip(Player player,
        ref readonly C2S_PositionPacket pos, ref readonly ExtraPositionData extra, bool hasExtra)
    {
        bool active;
        lock (_modularShipPoolLock) { active = _modularShipPlayerState.ContainsKey(player); }
        if (!active) return;
        ApplyModularShipState(player, force: false);
    }

    private bool OnTick_ModularShip()
    {
        // Snapshot active players under lock; ApplyState dispatches outside.
        Player[] activePlayers;
        lock (_modularShipPoolLock)
        {
            if (_modularShipPlayerState.Count == 0) return true;
            activePlayers = new Player[_modularShipPlayerState.Count];
            int i = 0;
            foreach (var kv in _modularShipPlayerState) activePlayers[i++] = kv.Key;
        }
        foreach (var p in activePlayers) ApplyModularShipState(p, force: false);
        return true;
    }

    // -------------------------------------------------------------------------
    // INTERNAL HELPERS
    // -------------------------------------------------------------------------

    /// <summary>Caller MUST hold pool lock.</summary>
    private int AllocateModularShipSlotInternal(Player player)
    {
        for (int i = 0; i < ModularShipPoolSize; i++)
        {
            if (_modularShipSlotOwner[i] is null)
            {
                _modularShipSlotOwner[i] = player;
                return i;
            }
        }
        return -1;
    }

    /// <summary>Caller MUST hold pool lock.</summary>
    private void ReleaseModularShipPlayerInternal(Player player, bool hideOverlays)
    {
        if (!_modularShipPlayerState.TryGetValue(player, out ModularShipPlayerState? state)) return;
        Arena? arena = player.Arena;
        int slot = state.Slot;
        if (slot >= 0 && slot < ModularShipPoolSize) _modularShipSlotOwner[slot] = null;
        if (hideOverlays && arena is not null && state.LastVisible)
            _lvzObjects.Toggle(arena, (short)(ModularShipPoolBase + slot), false);
        _modularShipPlayerState.Remove(player);
    }

    /// <summary>
    /// Compute extrapolated position + rotation, change-detect, dispatch only
    /// changed packets. ServerTick is 1/100s = 10ms units.
    /// </summary>
    private void ApplyModularShipState(Player player, bool force)
    {
        Arena? arena = player.Arena;
        if (arena is null) return;
        if (player.Ship == ShipType.Spec) return;

        ModularShipPlayerState? state;
        lock (_modularShipPoolLock)
        {
            if (!_modularShipPlayerState.TryGetValue(player, out state)) return;
        }

        ref readonly var pos = ref player.Position;

        // Time since the last received position packet, plus a small lead.
        ServerTick now = ServerTick.Now;
        int ticksElapsed = now - pos.Time;
        if (ticksElapsed < 0) ticksElapsed = 0;
        int extrapMs = ticksElapsed * 10 + ModularShipLeadMillis;
        if (extrapMs > ModularShipMaxExtrapolateMs) extrapMs = ModularShipMaxExtrapolateMs;

        // pos.X/YSpeed are pixels per 10 seconds; (speed * ms) / 10000 = pixels.
        int anchorX = pos.X + (pos.XSpeed * extrapMs) / 10_000;
        int anchorY = pos.Y + (pos.YSpeed * extrapMs) / 10_000;

        int rot = ((pos.Rotation % 40) + 40) % 40;
        byte imageId = (byte)(ModularShipCapitalImageBase + rot);

        // 164×164 sprite canvas; subtract half (82) in both axes to center.
        short lvzX = (short)(anchorX - ModularShipCapitalCanvasHalf);
        short lvzY = (short)(anchorY - ModularShipCapitalCanvasHalf);
        short objId = (short)(ModularShipPoolBase + state.Slot);

        bool needToggle = force || !state.LastVisible;
        bool needPos = force || state.LastX != lvzX || state.LastY != lvzY;
        bool needImage = force || state.LastImageId != imageId;

        if (needToggle)
        {
            _lvzObjects.Toggle(arena, objId, true);
            state.LastVisible = true;
        }
        if (needPos)
        {
            _lvzObjects.SetPosition(arena, objId, lvzX, lvzY,
                ScreenOffset.Normal, ScreenOffset.Normal);
            state.LastX = lvzX;
            state.LastY = lvzY;
        }
        if (needImage)
        {
            _lvzObjects.SetImage(arena, objId, imageId);
            state.LastImageId = imageId;
        }
    }
}
