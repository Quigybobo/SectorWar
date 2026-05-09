using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System.Threading;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — HullVisuals subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Per-player Warbird-Capital LVZ overlay tracking. Each tracked player gets a
// pre-allocated LVZ object that follows their ship at every position update,
// using a 40-frame rotation atlas to match the ship's heading. Proof-of-
// concept for "hull-tier visuals" — hull upgrades reveal a larger, more
// elaborate sprite without changing the underlying ship class.
//
// SOURCE
// ------
// Standalone module `Modules/HullVisuals.cs` stays as a library copy.
//
// LVZ ART CONTRACT (must match tools/lvz_warbird_capital.py generator)
// --------------------------------------------------------------------
//   - Object IDs 6000..6063 are the player pool (one slot per player).
//   - Image IDs 5..44 are the 40 rotation frames (9° steps), one per
//     Continuum native rotation.
//   - Image IDs 0..4 are the hull rings reserved for backward compat with the
//     earlier PoC; we don't touch them here.
//   - The rotated sprite is drawn on a 164×164 canvas (post-rotate padding).
//     The visual center should sit on the ship, so positioning subtracts half
//     the canvas size in both axes (CanvasHalfPixels = 82).
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: 64-slot pool (Player?[], short[], byte[], bool[] arrays)
//                  + Player→slot reverse-lookup dictionary, all guarded by
//                  one Lock.
//   - Conf keys read: NONE.
//   - Persisted data: NONE (overlays are session-only).
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE (driven by PlayerPositionPacketCallback).
//   - Commands registered: cmd_capitalon, cmd_capitaloff.
//
// CALLBACKS HOOKED (zone-wide via broker)
//   - PlayerActionCallback         → OnPlayerAction_HullVisuals (release on
//                                                                  Leave/Disconnect)
//   - ShipFreqChangeCallback       → OnShipFreqChange_HullVisuals (release on Spec)
//   - PlayerPositionPacketCallback → OnPlayerPosition_HullVisuals (drive overlay)
//
// HOT PATH
// --------
// PlayerPositionPacketCallback fires for every position packet from every
// player. We do an early-out check under lock to skip players we're not
// tracking. Tracked players go through ApplyHullVisualsState which sends
// at most 3 LVZ packets (Toggle, SetPosition, SetImage) and only when the
// state actually changed.
//
// Estimated traffic: 10Hz position rate × 64 tracked players × 2 packets
// average per update = ~1280 pkt/sec at full capacity. Well within Continuum's
// LVZ bandwidth budget.
//
// THREADING
// ---------
// Position packet callback runs on the network worker thread (NOT mainloop).
// All shared state goes through the pool lock. ILvzObjects toggles are
// thread-safe per the SS.NET contract.
//
// WAVE-FIXES PRESERVED
// --------------------
// Slot release pattern: capture (slot, wasVisible) under lock, then send the
// Toggle outside the lock. Avoids holding the pool lock across an LVZ packet
// dispatch (which could block on network I/O).
// =============================================================================

public sealed partial class SectorWar
{
    /// <summary>First LVZ object id in the player pool. Must match the
    /// generator's reservation.</summary>
    private const short HullVisualsPoolBase = 6000;

    /// <summary>Player pool size. Match the generator's reservation count.
    /// 64 fits typical zone player counts; raise if needed (and update the
    /// LVZ).</summary>
    private const int HullVisualsPoolSize = 64;

    /// <summary>First image-id of the 40-frame rotation atlas. Frames 0..4
    /// are hull rings; rotation frames live at 5..44.</summary>
    private const byte HullVisualsRotationImageBase = 5;

    /// <summary>One frame per Continuum rotation step (9° each). The
    /// position packet's <c>pos.Rotation</c> field is in the same units, so
    /// frame = pos.Rotation directly.</summary>
    private const int HullVisualsRotationFrameCount = 40;

    /// <summary>The rotated sprite's canvas is 164×164 after Pillow's
    /// rotate-with-pad. CanvasHalfPixels = 82 centers the sprite on the
    /// ship's coordinates. MUST match make_rotation_frames in
    /// tools/lvz_warbird_capital.py.</summary>
    private const int HullVisualsCanvasHalfPixels = 82;

    private const string HullVisualsCapitalOnCommand = "capitalon";
    private const string HullVisualsCapitalOffCommand = "capitaloff";

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    //
    // All arrays are PoolSize-long. Slot index is the LVZ object offset from
    // PoolBase. _slotOwner[i] is null when the slot is free.
    //
    // _last* arrays implement change-detection so we only resend a Toggle/
    // SetPosition/SetImage when the value actually changed. Forced refreshes
    // (initial Allocate, on-Toggle re-show) push sentinel values into _lastX/
    // _lastY/_lastRotation so the next ApplyState always sends.
    // -------------------------------------------------------------------------

    private readonly Player?[] _hullVisualsSlotOwner = new Player?[HullVisualsPoolSize];
    private readonly short[] _hullVisualsLastX = new short[HullVisualsPoolSize];
    private readonly short[] _hullVisualsLastY = new short[HullVisualsPoolSize];
    private readonly byte[] _hullVisualsLastRotation = new byte[HullVisualsPoolSize];
    private readonly bool[] _hullVisualsLastVisible = new bool[HullVisualsPoolSize];

    /// <summary>Reverse lookup so the position-packet hot path can early-out
    /// in O(1) for untracked players.</summary>
    private readonly Dictionary<Player, int> _hullVisualsPlayerToSlot = new();

    /// <summary>Guards all four arrays + the dictionary. Held briefly inside
    /// every Apply/Release/Allocate path; LVZ dispatches happen OUTSIDE the
    /// lock so we don't block the network thread.</summary>
    private readonly Lock _hullVisualsPoolLock = new();

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    /// <summary>Register commands + the three callbacks that drive the
    /// overlay (lifecycle, ship-change-to-spec, position packet).</summary>
    private void LoadHullVisuals(IComponentBroker broker)
    {
        _commandManager.AddCommand(HullVisualsCapitalOnCommand, Command_HullVisualsCapitalOn);
        _commandManager.AddCommand(HullVisualsCapitalOffCommand, Command_HullVisualsCapitalOff);

        PlayerActionCallback.Register(broker, OnPlayerAction_HullVisuals);
        ShipFreqChangeCallback.Register(broker, OnShipFreqChange_HullVisuals);
        PlayerPositionPacketCallback.Register(broker, OnPlayerPosition_HullVisuals);

        _logManager.LogM(LogLevel.Info, LogCategory,
            $"HullVisuals subsystem loaded (pool size {HullVisualsPoolSize}).");
    }

    /// <summary>Reverse of Load. Hides every active overlay on the way out so
    /// no zombie sprites linger if the umbrella is hot-reloaded.</summary>
    private void UnloadHullVisuals(IComponentBroker broker)
    {
        _commandManager.RemoveCommand(HullVisualsCapitalOnCommand, Command_HullVisualsCapitalOn);
        _commandManager.RemoveCommand(HullVisualsCapitalOffCommand, Command_HullVisualsCapitalOff);

        PlayerActionCallback.Unregister(broker, OnPlayerAction_HullVisuals);
        ShipFreqChangeCallback.Unregister(broker, OnShipFreqChange_HullVisuals);
        PlayerPositionPacketCallback.Unregister(broker, OnPlayerPosition_HullVisuals);

        // Walk active slots and toggle off the LVZ in their respective arenas.
        // Snapshot under lock; dispatch outside (avoid holding lock across
        // network I/O — same pattern as ReleasePlayer below).
        var toHide = new List<(Arena Arena, short ObjId)>();
        lock (_hullVisualsPoolLock)
        {
            for (int i = 0; i < HullVisualsPoolSize; i++)
            {
                if (_hullVisualsSlotOwner[i] is Player p
                    && p.Arena is Arena a
                    && _hullVisualsLastVisible[i])
                {
                    toHide.Add((a, (short)(HullVisualsPoolBase + i)));
                }
                _hullVisualsSlotOwner[i] = null;
                _hullVisualsLastVisible[i] = false;
            }
            _hullVisualsPlayerToSlot.Clear();
        }
        foreach (var (arena, objId) in toHide)
            _lvzObjects.Toggle(arena, objId, false);
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH (no-ops — zone-wide pool)
    // -------------------------------------------------------------------------

    private void AttachHullVisuals(Arena arena) { /* zone-wide */ }
    private void DetachHullVisuals(Arena arena) { /* zone-wide */ }

    // -------------------------------------------------------------------------
    // COMMANDS
    // -------------------------------------------------------------------------

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Start tracking yourself with a capital-ship LVZ overlay.")]
    private void Command_HullVisualsCapitalOn(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is null)
        {
            _chat.SendMessage(player, "Must be in an arena.");
            return;
        }

        int slot;
        lock (_hullVisualsPoolLock)
        {
            if (_hullVisualsPlayerToSlot.TryGetValue(player, out int existing))
            {
                _chat.SendMessage(player, $"Capital overlay already on (slot {existing}).");
                return;
            }
            slot = AllocateHullVisualsSlot(player);
            if (slot < 0)
            {
                _chat.SendMessage(player, "All capital overlay slots are in use.");
                return;
            }
        }

        // Force an immediate update so the overlay appears on the next tick
        // without waiting for a real position packet.
        ApplyHullVisualsState(player, slot, force: true);
        _chat.SendMessage(player, $"Capital overlay ON (slot {slot}). Move/rotate to see it track.");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Stop tracking yourself.")]
    private void Command_HullVisualsCapitalOff(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        ReleaseHullVisualsPlayer(player);
        _chat.SendMessage(player, "Capital overlay OFF.");
    }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    /// <summary>Release the player's slot on disconnect or arena exit so the
    /// LVZ object is free for another player.</summary>
    private void OnPlayerAction_HullVisuals(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.Disconnect || action == PlayerAction.LeaveArena)
            ReleaseHullVisualsPlayer(player);
    }

    /// <summary>Hide overlay when the player enters Spec — no ship to track.</summary>
    private void OnShipFreqChange_HullVisuals(
        Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
    {
        if (newShip == ShipType.Spec)
            ReleaseHullVisualsPlayer(player);
    }

    /// <summary>Hot-path. Look up the player's slot under lock; if they're
    /// not tracked, return immediately. Otherwise drive ApplyHullVisualsState.</summary>
    /// <remarks>
    /// Threading: this fires on the network worker thread, not mainloop. Pool
    /// lookup is under the pool lock. ApplyHullVisualsState then takes the
    /// lock briefly for change-detection updates and dispatches LVZ packets
    /// outside the lock.
    /// </remarks>
    private void OnPlayerPosition_HullVisuals(Player player,
        ref readonly C2S_PositionPacket pos, ref readonly ExtraPositionData extra, bool hasExtra)
    {
        int slot;
        lock (_hullVisualsPoolLock)
        {
            if (!_hullVisualsPlayerToSlot.TryGetValue(player, out slot)) return;
        }
        ApplyHullVisualsState(player, slot, force: false);
    }

    // -------------------------------------------------------------------------
    // INTERNAL HELPERS
    // -------------------------------------------------------------------------

    /// <summary>Caller MUST hold the pool lock. Linear scan for the first free
    /// slot (PoolSize is small, no need for a free-list).</summary>
    private int AllocateHullVisualsSlot(Player player)
    {
        for (int i = 0; i < HullVisualsPoolSize; i++)
        {
            if (_hullVisualsSlotOwner[i] is null)
            {
                _hullVisualsSlotOwner[i] = player;
                _hullVisualsPlayerToSlot[player] = i;
                _hullVisualsLastVisible[i] = false;       // forces toggle-on
                _hullVisualsLastX[i] = short.MinValue;     // forces SetPosition
                _hullVisualsLastY[i] = short.MinValue;
                _hullVisualsLastRotation[i] = 255;         // forces SetImage
                return i;
            }
        }
        return -1;
    }

    /// <summary>Free the player's slot and toggle off the LVZ. Snapshot the
    /// arena + visibility under lock, then dispatch outside the lock.</summary>
    private void ReleaseHullVisualsPlayer(Player player)
    {
        Arena? arena = player.Arena;
        int slot;
        bool wasVisible;
        lock (_hullVisualsPoolLock)
        {
            if (!_hullVisualsPlayerToSlot.TryGetValue(player, out slot)) return;
            _hullVisualsPlayerToSlot.Remove(player);
            _hullVisualsSlotOwner[slot] = null;
            wasVisible = _hullVisualsLastVisible[slot];
            _hullVisualsLastVisible[slot] = false;
        }
        if (wasVisible && arena is not null)
            _lvzObjects.Toggle(arena, (short)(HullVisualsPoolBase + slot), false);
    }

    /// <summary>
    /// Drive the overlay for one slot: position follows the ship, image
    /// updates when rotation changes, toggle on the first frame. Change-
    /// detected so steady state generates zero traffic.
    /// </summary>
    /// <param name="force">If true, suppresses change detection and forces
    /// a full re-send (used on initial Allocate and on a re-Toggle path).</param>
    private void ApplyHullVisualsState(Player player, int slot, bool force)
    {
        Arena? arena = player.Arena;
        if (arena is null) return;
        if (player.Ship == ShipType.Spec) return;

        ref readonly var pos = ref player.Position;

        // Continuum's rotation field is already in 9° steps (0..39); add 40
        // to handle any negative wraparound, modulo 40 to wrap the top.
        byte rotFrame = (byte)((pos.Rotation + 40) % 40);
        byte imageId = (byte)(HullVisualsRotationImageBase + rotFrame);

        // Center-on-ship offset: LVZ x/y is the top-left of the sprite, but
        // the visual center should sit on the ship's pixel position. Subtract
        // half the canvas in both axes.
        short ovX = (short)(pos.X - HullVisualsCanvasHalfPixels);
        short ovY = (short)(pos.Y - HullVisualsCanvasHalfPixels);

        short objId = (short)(HullVisualsPoolBase + slot);

        // Compute the change-detected dispatch decisions UNDER lock, then
        // send packets OUTSIDE the lock. Holds the pool lock for tens of
        // nanoseconds at most.
        bool needToggle, needPos, needImage;
        lock (_hullVisualsPoolLock)
        {
            needToggle = !_hullVisualsLastVisible[slot];
            needPos = force || _hullVisualsLastX[slot] != ovX || _hullVisualsLastY[slot] != ovY;
            needImage = force || _hullVisualsLastRotation[slot] != rotFrame;

            if (needToggle) _hullVisualsLastVisible[slot] = true;
            if (needPos)
            {
                _hullVisualsLastX[slot] = ovX;
                _hullVisualsLastY[slot] = ovY;
            }
            if (needImage) _hullVisualsLastRotation[slot] = rotFrame;
        }

        if (needToggle) _lvzObjects.Toggle(arena, objId, true);
        if (needPos) _lvzObjects.SetPosition(arena, objId, ovX, ovY,
            ScreenOffset.Normal, ScreenOffset.Normal);
        if (needImage) _lvzObjects.SetImage(arena, objId, imageId);
    }
}
