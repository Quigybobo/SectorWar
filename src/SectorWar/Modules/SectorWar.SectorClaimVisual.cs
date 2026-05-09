using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — SectorClaimVisual subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Mini-map LVZ indicator showing per-arena ownership across the linked sector
// arenas. Three small colored boxes anchored above the radar:
//   - row 0 = home arena
//   - row 1 = mid arena
//   - row 2 = end arena
// Color: grey = unclaimed, cyan = controlled, yellow = contested.
//
// Subscribes to ISectorClaim.ArenaOwnerChanged and refreshes the LVZ state in
// EVERY linked arena (each arena's instance shows the same N-arena status to
// its players).
//
// SOURCE
// ------
// Standalone module `Modules/SectorClaimVisual.cs` stays in place as a library
// copy. This partial preserves identical behaviour.
//
// 1-ARENA COLLAPSE NOTE
// ---------------------
// The hardcoded `SectorClaimVisualLinkedArenas` array currently lists the
// 3-arena topology (sectorwarhome/mid/end). Once the consolidated umbrella's
// `[SectorWar] LinkedArenas` conf-driven list lands (Phase 1 final), this
// array becomes a fallback default. For pure 1-arena setups, the array
// shrinks to a single element and the visual indicator collapses to one tile.
//
// LVZ ART CONTRACT
// ----------------
// The LVZ images bake in arena letters (H/M/E) at indices ClaimImageBase + N.
// If the linked-arena list and the LVZ image set disagree, the indicators
// show wrong letters. SectorClaimVisual logs a `Warn` if it detects drift
// against `ISectorWar.LinkedArenaNames` at attach time.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: cached ISectorClaim handle (subsystem-level field).
//   - Conf keys read: NONE directly (consumes ISectorWar.LinkedArenaNames).
//   - Persisted data: NONE.
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE.
//   - Commands registered: NONE.
//
// CALLBACKS HOOKED (zone-wide)
//   - ISectorClaim.ArenaOwnerChanged event → OnArenaOwnerChanged_SectorClaimVisual
//   - ArenaActionCallback                  → OnArenaAction_SectorClaimVisual
//   - PlayerActionCallback                 → OnPlayerAction_SectorClaimVisual
//
// THREADING
// ---------
// All callbacks fire on the mainloop. <see cref="ILvzObjects.Toggle"/>/
// SetImage/SetPosition are mainloop-safe.
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave 12: drift-warn against ISectorWar.LinkedArenaNames.
// Existing behaviour: also iterate existing arenas at Load (covers hot-reload
// where ArenaAction.Create won't re-fire for already-running arenas).
// =============================================================================

public sealed partial class SectorWar
{
    // -------------------------------------------------------------------------
    // CONSTANTS — visual layout
    //
    // The LVZ object IDs and image-index layout MUST match the LVZ generator.
    // Anyone editing these has to also re-run the generator (see tools/) and
    // re-package the LVZ.
    // -------------------------------------------------------------------------

    /// <summary>First LVZ object id in the contiguous claim-indicator pool.
    /// Three IDs are used: ClaimSlotStart, +1, +2 — one per linked arena.</summary>
    private const short SectorClaimVisualSlotStart = 9200;

    /// <summary>Image-index base for the 3×3 grid of (arena, state) tiles.
    /// Index = ClaimImageBase + row*3 + state. Each tile is 24×24, with the
    /// arena letter (H/M/E) baked in and color encoding the state.</summary>
    private const byte SectorClaimVisualImageBase = 6;

    private const byte SectorClaimVisualStateUnclaimed = 0;
    private const byte SectorClaimVisualStateControlled = 1;
    private const byte SectorClaimVisualStateContested = 2;

    /// <summary>Position: anchor to top-left of radar ("R") and stack the 3
    /// boxes HORIZONTALLY in a strip ABOVE the radar (negative Y).</summary>
    private const short SectorClaimVisualColumnX0 = 0;
    private const short SectorClaimVisualColumnXStep = 26;  // 24px tile + 2px gap
    private const short SectorClaimVisualRowY = -30;        // small breathing gap above coords

    /// <summary>Hardcoded linked-arena list. Phase later: read from
    /// [SectorWar] LinkedArenas. Order MATTERS — the LVZ images bake in
    /// the H/M/E letters matching this list's index. Drift between this
    /// list and the LVZ generator's letter set produces wrong-letter tiles.</summary>
    private static readonly string[] SectorClaimVisualLinkedArenas =
    {
        "sectorwarhome",
        "sectorwarmid",
        "sectorwarend",
    };

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    /// <summary>Cached ISectorClaim handle (acquired in Load, released in
    /// Unload). Held so we can also unsubscribe from
    /// <see cref="ISectorClaim.ArenaOwnerChanged"/> on the same instance we
    /// subscribed to. Nullable because ISectorClaim may not be registered yet
    /// when we load (the standalone SectorClaim is still the one registering it
    /// during the parallel-coexistence period; the umbrella will eventually
    /// register it via the SectorClaim subsystem).</summary>
    private ISectorClaim? _sectorClaimVisualClaim;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Cache ISectorClaim, subscribe to ArenaOwnerChanged, hook
    /// Arena/Player action callbacks. If ISectorClaim isn't yet on the broker
    /// (load-order edge case), we degrade gracefully — visuals just won't
    /// update on claim flips until the next zone restart.
    /// </summary>
    private void LoadSectorClaimVisual(IComponentBroker broker)
    {
        _sectorClaimVisualClaim = broker.GetInterface<ISectorClaim>();
        if (_sectorClaimVisualClaim is null)
        {
            _logManager.LogM(LogLevel.Warn, LogCategory,
                "SectorClaimVisual: ISectorClaim not registered — visuals won't update on claim flips.");
        }
        else
        {
            _sectorClaimVisualClaim.ArenaOwnerChanged += OnArenaOwnerChanged_SectorClaimVisual;
        }

        // Drift check: warn if our hardcoded list disagrees with what
        // SectorWar publishes via ISectorWar. The LVZ tile letters are baked
        // in to match THIS list, so a mismatch shows wrong tile + label combos.
        ISectorWar? sw = broker.GetInterface<ISectorWar>();
        try
        {
            if (sw is not null)
            {
                var canon = sw.LinkedArenaNames;
                if (canon.Count != SectorClaimVisualLinkedArenas.Length
                    || !canon.SequenceEqual(SectorClaimVisualLinkedArenas, StringComparer.OrdinalIgnoreCase))
                {
                    _logManager.LogM(LogLevel.Warn, LogCategory,
                        $"Linked-arena mismatch with SectorWar. SectorClaimVisual: " +
                        $"[{string.Join(",", SectorClaimVisualLinkedArenas)}]; " +
                        $"SectorWar: [{string.Join(",", canon)}]. " +
                        "Mini-map indicators will likely show wrong arena letters until LVZ + this list are aligned.");
                }
            }
        }
        finally
        {
            if (sw is not null) broker.ReleaseInterface(ref sw);
        }

        ArenaActionCallback.Register(broker, OnArenaAction_SectorClaimVisual);
        PlayerActionCallback.Register(broker, OnPlayerAction_SectorClaimVisual);

        // Hot-reload coverage: iterate existing arenas at load so the
        // indicators populate even when ArenaAction.Create won't re-fire for
        // already-running arenas (umbrella loaded after arenas exist).
        _arenaManager.Lock();
        try
        {
            foreach (var arena in _arenaManager.Arenas)
            {
                if (arena.Name is null) continue;
                if (!IsSectorClaimVisualLinked(arena.Name)) continue;
                RefreshAllSectorClaimVisualSlots(arena);
            }
        }
        finally
        {
            _arenaManager.Unlock();
        }

        _logManager.LogM(LogLevel.Info, LogCategory, "SectorClaimVisual subsystem loaded.");
    }

    /// <summary>Reverse of Load. Unsubscribes from the event, releases the
    /// cached interface, unregisters callbacks.</summary>
    private void UnloadSectorClaimVisual(IComponentBroker broker)
    {
        if (_sectorClaimVisualClaim is not null)
        {
            _sectorClaimVisualClaim.ArenaOwnerChanged -= OnArenaOwnerChanged_SectorClaimVisual;
            broker.ReleaseInterface(ref _sectorClaimVisualClaim);
        }
        ArenaActionCallback.Unregister(broker, OnArenaAction_SectorClaimVisual);
        PlayerActionCallback.Unregister(broker, OnPlayerAction_SectorClaimVisual);
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH (no-ops — zone-wide subsystem)
    // -------------------------------------------------------------------------

    private void AttachSectorClaimVisual(Arena arena) { /* zone-wide */ }
    private void DetachSectorClaimVisual(Arena arena) { /* zone-wide */ }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Player joined a linked arena → blast the slot state to them so they
    /// see current claim info even if no events fired since arena Create.
    /// ILvzObjects's per-arena Toggle/SetImage records the arena state and
    /// re-sends on EnterGame, but a freshly-loaded subsystem on a hot-reload
    /// would otherwise miss arenas that were already running.
    /// </summary>
    private void OnPlayerAction_SectorClaimVisual(Player player, PlayerAction action, Arena? arena)
    {
        if (action != PlayerAction.EnterArena) return;
        if (arena?.Name is null) return;
        if (!IsSectorClaimVisualLinked(arena.Name)) return;
        RefreshAllSectorClaimVisualSlots(arena);
    }

    /// <summary>When a linked arena comes online, render its 3 boxes.</summary>
    private void OnArenaAction_SectorClaimVisual(Arena arena, ArenaAction action)
    {
        if (arena.Name is null) return;
        if (action != ArenaAction.Create) return;
        if (!IsSectorClaimVisualLinked(arena.Name)) return;
        RefreshAllSectorClaimVisualSlots(arena);
    }

    /// <summary>The owner of <paramref name="arenaName"/> flipped. Refresh that
    /// ROW in EVERY linked arena's LVZ so all players (in any sector arena)
    /// see the updated state.</summary>
    private void OnArenaOwnerChanged_SectorClaimVisual(string arenaName, short? oldDom, short? newDom)
    {
        for (int row = 0; row < SectorClaimVisualLinkedArenas.Length; row++)
        {
            if (!string.Equals(SectorClaimVisualLinkedArenas[row], arenaName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var arenaInstance in _arenaManager.Arenas)
            {
                if (arenaInstance.Name is null) continue;
                if (!IsSectorClaimVisualLinked(arenaInstance.Name)) continue;
                RefreshSectorClaimVisualSlot(arenaInstance, row);
            }
        }
    }

    // -------------------------------------------------------------------------
    // INTERNAL HELPERS
    // -------------------------------------------------------------------------

    /// <summary>Re-render all rows for a given arena instance.</summary>
    private void RefreshAllSectorClaimVisualSlots(Arena arenaInstance)
    {
        for (int row = 0; row < SectorClaimVisualLinkedArenas.Length; row++)
            RefreshSectorClaimVisualSlot(arenaInstance, row);
    }

    /// <summary>Compute current state for the given row's arena, look up the
    /// matching tile image, position the LVZ object, toggle it visible.</summary>
    private void RefreshSectorClaimVisualSlot(Arena arenaInstance, int row)
    {
        if (_sectorClaimVisualClaim is null) return;
        string slotArenaName = SectorClaimVisualLinkedArenas[row];
        var snap = _sectorClaimVisualClaim.GetSnapshot(slotArenaName);
        byte state = StateForSnapshot(snap);

        // 2D index: row = which arena, state = which color. Each arena has its
        // own letter baked in, so we always pick from this arena's 3 tiles.
        byte image = (byte)(SectorClaimVisualImageBase + row * 3 + state);
        short id = (short)(SectorClaimVisualSlotStart + row);

        // Anchor each box to the top-left of the radar ("R"), stack
        // horizontally with ColumnXStep spacing, and pin them above the
        // radar via the negative RowY. Screen-relative so they stay put as
        // the player flies.
        short x = (short)(SectorClaimVisualColumnX0 + row * SectorClaimVisualColumnXStep);
        short y = SectorClaimVisualRowY;
        _lvzObjects.SetPosition(arenaInstance, id, x, y, ScreenOffset.R, ScreenOffset.R);
        _lvzObjects.SetImage(arenaInstance, id, image);
        _lvzObjects.Toggle(arenaInstance, id, true);
    }

    /// <summary>Snapshot → tile state. Empty/null → unclaimed; otherwise
    /// IsControlled or IsContested or fallback unclaimed.</summary>
    private static byte StateForSnapshot(SectorClaimSnapshot? snap)
    {
        if (snap is null || snap.ClaimByFreq.Count == 0) return SectorClaimVisualStateUnclaimed;
        if (snap.IsControlled) return SectorClaimVisualStateControlled;
        if (snap.IsContested) return SectorClaimVisualStateContested;
        return SectorClaimVisualStateUnclaimed;
    }

    /// <summary>Case-insensitive membership test against the linked-arena list.</summary>
    private static bool IsSectorClaimVisualLinked(string arenaName)
    {
        foreach (var n in SectorClaimVisualLinkedArenas)
            if (string.Equals(n, arenaName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
