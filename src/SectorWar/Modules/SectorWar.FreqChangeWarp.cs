using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — FreqChangeWarp subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// When a player changes frequency while staying in a ship, gift them a Warp
// prize so they're teleported to a fresh spawn position. Useful in arenas
// where teams own segregated zones — without the warp, the freq-changer would
// be standing inside enemy territory.
//
// SOURCE
// ------
// Verbatim port of JoWie's freqchangewarp.c (ASSS):
//   bitbucket.org/jowie/asss-freqchangewarp
// The whole original is ~30 lines of meaningful code; this partial preserves
// the original behaviour and just folds it under the umbrella's lifecycle.
//
// RELATIONSHIP TO STANDALONE `FreqChangeWarp.cs`
// ----------------------------------------------
// The standalone module at Modules/FreqChangeWarp.cs stays in place as a
// library copy (per the user's "keep originals available individually for
// other projects" rule). DURING the consolidation, it's still the one being
// loaded by Modules.config — this partial-class subsystem is dormant until
// Phase 1 flips Modules.config to load the umbrella `SectorWar` instead.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: NONE. This subsystem is pure-callback (`ShipFreqChangeCallback`).
//   - Conf keys read: NONE.
//   - Persisted data: NONE.
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE.
//
// CALLBACKS HOOKED (per arena, in AttachFreqChangeWarp / DetachFreqChangeWarp)
//   - ShipFreqChangeCallback → OnShipFreqChange_FreqChangeWarp
//
// THREADING
// ---------
// `ShipFreqChangeCallback` fires on the mainloop. `IGame.GivePrize` is mainloop-
// safe. No locks needed.
//
// WAVE-FIXES PRESERVED
// --------------------
// None — this module had no Wave-1..13 corrections (it's too small to need any).
// =============================================================================

public sealed partial class SectorWar
{
    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    //
    // Called from the umbrella's IModule.Load / Unload (in SectorWar.cs). Phase 1
    // pattern: each subsystem owns four entry points — Load, Unload, Attach,
    // Detach — that the umbrella drives in lockstep with the SS.NET lifecycle.
    // -------------------------------------------------------------------------

    /// <summary>
    /// FreqChangeWarp has no zone-wide setup work. The whole subsystem is a
    /// per-arena callback subscription, which lives in
    /// <see cref="AttachFreqChangeWarp"/>. We log presence here purely to keep
    /// the load-order trail visible to operators.
    /// </summary>
    private void LoadFreqChangeWarp(IComponentBroker broker)
    {
        _logManager.LogM(LogLevel.Info, LogCategory,
            "FreqChangeWarp subsystem ready (no zone-wide state).");
    }

    /// <summary>
    /// Reverse of <see cref="LoadFreqChangeWarp"/>. No state to release.
    /// </summary>
    private void UnloadFreqChangeWarp(IComponentBroker broker)
    {
        // Intentionally empty — nothing to tear down. Method exists for symmetry
        // with the umbrella's Load/Unload pattern so future readers don't wonder
        // why FreqChangeWarp is missing from the lifecycle list.
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    //
    // SS.NET serialises arena lifecycle on the mainloop, so subscribing the
    // ShipFreqChangeCallback in Attach and unregistering in Detach is race-free.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Subscribes the per-arena <see cref="ShipFreqChangeCallback"/> so this
    /// arena's freq-changers receive the Warp prize.
    /// </summary>
    /// <remarks>
    /// Threading: mainloop only. Multiple AttachModule calls (e.g. arena
    /// recycle) are inherently serialised by SS.NET's arena state machine so
    /// we don't double-subscribe.
    /// </remarks>
    private void AttachFreqChangeWarp(Arena arena)
    {
        ShipFreqChangeCallback.Register(arena, OnShipFreqChange_FreqChangeWarp);
        _logManager.LogA(LogLevel.Info, LogCategory, arena, "FreqChangeWarp attached.");
    }

    /// <summary>
    /// Unsubscribes the per-arena callback. Symmetric with Attach. Safe to
    /// call even if Attach failed (Unregister is a no-op for a callback that
    /// wasn't registered).
    /// </summary>
    private void DetachFreqChangeWarp(Arena arena)
    {
        ShipFreqChangeCallback.Unregister(arena, OnShipFreqChange_FreqChangeWarp);
    }

    // -------------------------------------------------------------------------
    // CALLBACK
    // -------------------------------------------------------------------------

    /// <summary>
    /// Handler for <see cref="ShipFreqChangeCallback"/>. Gives the player a
    /// Warp prize whenever their frequency actually changed (ship-only changes
    /// don't warp). Naming convention: <c>OnSomething_Subsystem</c> so partial-
    /// file callback handlers stay grep-able and don't collide with other
    /// subsystems that subscribe the same callback (e.g. WarpInEffect, Pylon).
    /// </summary>
    /// <param name="player">Subject whose ship/freq state just changed.</param>
    /// <param name="newShip">New ship type (irrelevant to this subsystem).</param>
    /// <param name="oldShip">Old ship type (irrelevant to this subsystem).</param>
    /// <param name="newFreq">New frequency.</param>
    /// <param name="oldFreq">Old frequency. Equal to <paramref name="newFreq"/> on
    /// pure ship changes — the guard below filters those out so the warp only
    /// fires when the freq genuinely flipped.</param>
    /// <remarks>
    /// Threading: mainloop. <see cref="IGame.GivePrize"/> is mainloop-safe.
    /// </remarks>
    private void OnShipFreqChange_FreqChangeWarp(
        Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
    {
        // Guard against ship-only changes (Continuum reports those through the
        // same callback). Without this check, every ship swap would warp the
        // player — annoying and not what JoWie's original module did.
        if (newFreq != oldFreq)
            _game.GivePrize(player, Prize.Warp, 1);
    }
}
