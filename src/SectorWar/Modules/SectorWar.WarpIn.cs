using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — WarpInEffect subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Reusable "thing materializes" LVZ animation primitive. Pylon, StationDeployer,
// and any other deployable can call IWarpInEffect.Play(arena, x, y, durationMs,
// flavor) to flash a brief warp-in visual at a world coordinate. Phase 1 is a
// STUB: the call is recorded at Drivel log level only — actual LVZ object
// playback wires in during Phase 2 of the LVZ work.
//
// SOURCE
// ------
// Original standalone module: Modules/WarpInEffect.cs (kept in place as a
// library copy). This partial preserves identical behaviour.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: NONE (Phase 1 stub).
//   - Conf keys read: NONE.
//   - Persisted data: NONE.
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE.
//   - Broker interfaces published: IWarpInEffect (registered in
//     LoadWarpInEffect, unregistered in UnloadWarpInEffect).
//
// CALLBACKS HOOKED: NONE in Phase 1. Phase 2 will hook ArenaActionCallback
// to allocate per-arena LVZ object pools at arena Create.
//
// THREADING
// ---------
// `IWarpInEffect.Play` is called from the mainloop by deployer modules. The
// stub-level log call is mainloop-safe. Phase 2 will keep the same contract.
//
// DESIGN NOTES — INTERFACE IMPLEMENTATION SPREAD ACROSS PARTIAL FILES
// -------------------------------------------------------------------
// C# allows partial classes to accumulate the base/interface list across
// files. The umbrella declares `IModule, IArenaAttachableModule`; this partial
// extends it with `IWarpInEffect`. Any partial file that implements another
// broker-registered interface uses the same pattern. Reviewers can grep for
// `partial class SectorWar :` to see which file owns which interface.
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave 13: log level lowered from Info → Drivel so the per-deploy log line
// doesn't spam normal play. Preserved here.
// =============================================================================

public sealed partial class SectorWar : IWarpInEffect
{
    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    /// <summary>
    /// Token returned by <see cref="IComponentBroker.RegisterInterface"/>.
    /// Held so we can unregister cleanly in
    /// <see cref="UnloadWarpInEffect"/>. Nullable because before Load runs
    /// the registration hasn't happened.
    /// </summary>
    private InterfaceRegistrationToken<IWarpInEffect>? _warpInEffectToken;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers <see cref="IWarpInEffect"/> on the broker so other subsystems
    /// (Pylon, StationDeployer) can resolve it via
    /// <c>broker.GetInterface&lt;IWarpInEffect&gt;()</c>.
    /// </summary>
    private void LoadWarpInEffect(IComponentBroker broker)
    {
        _warpInEffectToken = broker.RegisterInterface<IWarpInEffect>(this);
        _logManager.LogM(LogLevel.Info, LogCategory,
            "WarpInEffect subsystem loaded (Phase 1 stub — actual LVZ playback wires in Phase 2).");
    }

    /// <summary>
    /// Unregisters <see cref="IWarpInEffect"/>. Safe to call even if Load
    /// failed — the token is null-checked.
    /// </summary>
    private void UnloadWarpInEffect(IComponentBroker broker)
    {
        if (_warpInEffectToken is not null)
            broker.UnregisterInterface(ref _warpInEffectToken);
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    //
    // Phase 1 stub has no per-arena state. Phase 2 will allocate the LVZ
    // object-id pool for this arena in AttachWarpInEffect and release it in
    // DetachWarpInEffect. Stubs exist now for symmetry with other subsystems.
    // -------------------------------------------------------------------------

    private void AttachWarpInEffect(Arena arena) { /* Phase 2: allocate LVZ pool */ }
    private void DetachWarpInEffect(Arena arena) { /* Phase 2: release LVZ pool */ }

    // -------------------------------------------------------------------------
    // IWarpInEffect IMPLEMENTATION
    // -------------------------------------------------------------------------

    /// <summary>
    /// Phase 1 stub. Logs the call at Drivel level (Wave-13 fix: Info was too
    /// loud — every pylon deploy was spamming the log). Phase 2 implementation
    /// will pick the next free LVZ object id from a per-flavor pool, position
    /// it at (x, y), toggle on, and schedule a toggle-off timer.
    /// </summary>
    /// <param name="arena">Arena to play the effect in.</param>
    /// <param name="pixelX">World X in pixels (NOT tiles — multiply tile by 16).</param>
    /// <param name="pixelY">World Y in pixels.</param>
    /// <param name="durationMs">Time in ms before the effect auto-hides.</param>
    /// <param name="flavor">Visual variant. <see cref="WarpInFlavor.Default"/>
    /// is the only one with art today; the rest are reserved for Phase 2.</param>
    /// <remarks>
    /// Threading: mainloop only (no thread-safety on the LVZ object pool). All
    /// known callers (Pylon, StationDeployer) drive from mainloop already.
    /// </remarks>
    void IWarpInEffect.Play(Arena arena, int pixelX, int pixelY, int durationMs, WarpInFlavor flavor)
    {
        // Wave-13: Drivel level keeps deploy spam out of normal logs. Operators
        // who actually want to debug the warp-in pipeline can ?logsetlevel up.
        _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
            $"Warp-in: flavor={flavor} at ({pixelX},{pixelY}) for {durationMs}ms.");
    }
}
