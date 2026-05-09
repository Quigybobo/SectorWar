using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

/// <summary>
/// PowerGrid computes which deployed structures are inside any friendly
/// pylon's power radius. Per-tick. Notifies StationDeployer + other
/// power-aware modules when a structure transitions powered â†” unpowered.
///
/// Phase 2 scope: simple distance check against IPylon's registry. No
/// power *unit* accounting (each pylon supplies infinite power within its
/// radius). Future: track unit consumption + multi-pylon redundancy.
/// </summary>
public interface IPowerGrid : IComponentInterface
{
    /// <summary>
    /// Register a structure for power tracking. Returns a token used in
    /// later state queries / unregister.
    /// </summary>
    PowerSubscription Subscribe(Arena arena, int pixelX, int pixelY, short freq, Action<bool> onPowerChanged);

    /// <summary>Stop tracking a previously-subscribed structure.</summary>
    void Unsubscribe(PowerSubscription subscription);

    /// <summary>
    /// Returns the current power state of the given subscription.
    /// True = powered (within range of a friendly pylon), False = unpowered.
    /// </summary>
    bool IsPowered(PowerSubscription subscription);
}

/// <summary>
/// Opaque handle returned by PowerGrid.Subscribe. Carries enough info to
/// re-evaluate the subscription each tick. Don't construct directly.
/// </summary>
public sealed class PowerSubscription
{
    public required Arena Arena { get; init; }
    public required int PixelX { get; init; }
    public required int PixelY { get; init; }
    public required short Freq { get; init; }
    public required Action<bool> OnPowerChanged { get; init; }
    /// <summary>Mutable: PowerGrid stores the last-known power state here.</summary>
    public bool LastKnownPowered { get; set; }
}
