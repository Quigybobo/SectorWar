using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — PowerGrid subsystem (pylon-power gating).
// =============================================================================
//
// PURPOSE
// -------
// Per-tick scans subscribed structures and fires their OnPowerChanged callback
// when their pylon-power state flips. Used by stations + minions to enable/
// disable themselves when their nearest friendly pylon is alive vs dead.
//
// Phase 2 (current): simple linear distance scan via IPylon.IsPowered. Cheap
// because arena pylon + structure counts are small (<50). Future phases may
// add hysteresis (grace period when pylon dies) and a spatial index.
//
// SOURCE
// ------
// Standalone module `Modules/PowerGrid.cs` stays as a library copy.
//
// TICK CADENCE
// ------------
// 250ms — pylon power doesn't change rapidly (pylon spawn/destroy events are
// seconds apart). Faster cadence would just burn cycles.
//
// RUNTIME OWNERSHIP
//   - Owned state: subscription list (lock-protected).
//   - Conf keys read: NONE.
//   - Persisted data: NONE (subscriptions rebuild from station spawns).
//   - Fakes registered: NONE.
//   - Timers scheduled: 250ms IMainloopTimer poll.
//   - Commands registered: NONE.
//   - Broker interfaces published: IPowerGrid.
//
// CALLBACKS HOOKED: NONE (subscription model, no global callbacks).
//
// THREADING
// ---------
// IMainloopTimer fires on the mainloop. Subscription list is lock-protected;
// snapshot under lock then fire callbacks outside the lock so a long-running
// OnPowerChanged handler can't block subscriber additions.
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave 3: subscription removal on station despawn (caller must call Unsubscribe).
// Wave 7: try/catch around the OnPowerChanged callback so a misbehaving
// subscriber can't take down the whole tick.
// Initial state evaluation in Subscribe so subscribers know power state
// without waiting for the first tick.
// =============================================================================

public sealed partial class SectorWar : IPowerGrid
{
    private const int PowerGridTickIntervalMs = 250;

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private InterfaceRegistrationToken<IPowerGrid>? _powerGridToken;

    /// <summary>Cached broker for IPylon lookups (per-tick).</summary>
    private IComponentBroker? _powerGridBroker;

    /// <summary>Global subscription list. Linear scan per tick is fine for
    /// the small structure counts we expect (<50 per arena).</summary>
    private readonly List<PowerSubscription> _powerGridSubs = new();

    /// <summary>Guards the subscription list. Leaf lock.</summary>
    private readonly Lock _powerGridLock = new();

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadPowerGrid(IComponentBroker broker)
    {
        _powerGridBroker = broker;
        _mainloopTimer.SetTimer(OnTick_PowerGrid, PowerGridTickIntervalMs,
            PowerGridTickIntervalMs, this);
        _powerGridToken = broker.RegisterInterface<IPowerGrid>(this);
        _logManager.LogM(LogLevel.Info, LogCategory, "PowerGrid subsystem loaded.");
    }

    private void UnloadPowerGrid(IComponentBroker broker)
    {
        if (_powerGridToken is not null)
            broker.UnregisterInterface(ref _powerGridToken);
        _mainloopTimer.ClearTimer(OnTick_PowerGrid, this);
        lock (_powerGridLock) { _powerGridSubs.Clear(); }
        _powerGridBroker = null;
    }

    private void AttachPowerGrid(Arena arena) { /* zone-wide */ }
    private void DetachPowerGrid(Arena arena) { /* zone-wide */ }

    // -------------------------------------------------------------------------
    // IPowerGrid IMPLEMENTATION
    // -------------------------------------------------------------------------

    PowerSubscription IPowerGrid.Subscribe(Arena arena, int pixelX, int pixelY,
        short freq, Action<bool> onPowerChanged)
    {
        var sub = new PowerSubscription
        {
            Arena = arena,
            PixelX = pixelX,
            PixelY = pixelY,
            Freq = freq,
            OnPowerChanged = onPowerChanged,
            LastKnownPowered = false,
        };

        // Initial state evaluation so subscribers know power state without
        // waiting for the first tick.
        if (_powerGridBroker is not null)
        {
            IPylon? pylon = _powerGridBroker.GetInterface<IPylon>();
            try
            {
                bool isPowered = pylon?.IsPowered(arena, pixelX, pixelY, freq) ?? false;
                sub.LastKnownPowered = isPowered;
                onPowerChanged(isPowered);
            }
            finally
            {
                if (pylon is not null) _powerGridBroker.ReleaseInterface(ref pylon);
            }
        }

        lock (_powerGridLock) { _powerGridSubs.Add(sub); }
        return sub;
    }

    void IPowerGrid.Unsubscribe(PowerSubscription subscription)
    {
        lock (_powerGridLock) { _powerGridSubs.Remove(subscription); }
    }

    bool IPowerGrid.IsPowered(PowerSubscription subscription) => subscription.LastKnownPowered;

    // -------------------------------------------------------------------------
    // TIMER CALLBACK
    // -------------------------------------------------------------------------

    private bool OnTick_PowerGrid()
    {
        if (_powerGridBroker is null) return true;
        IPylon? pylon = _powerGridBroker.GetInterface<IPylon>();
        if (pylon is null) return true;

        try
        {
            // Snapshot to avoid holding the lock during callback dispatch.
            PowerSubscription[] snap;
            lock (_powerGridLock) { snap = _powerGridSubs.ToArray(); }

            foreach (var sub in snap)
            {
                bool nowPowered = pylon.IsPowered(sub.Arena, sub.PixelX, sub.PixelY, sub.Freq);
                if (nowPowered != sub.LastKnownPowered)
                {
                    sub.LastKnownPowered = nowPowered;
                    try { sub.OnPowerChanged(nowPowered); }
                    catch (Exception ex)
                    {
                        // Wave 7: a misbehaving subscriber callback can't
                        // crash the tick.
                        _logManager.LogM(LogLevel.Warn, LogCategory,
                            $"OnPowerChanged callback threw: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            _powerGridBroker.ReleaseInterface(ref pylon);
        }

        return true;
    }
}
