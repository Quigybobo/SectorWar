using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

/// <summary>
/// Composite multi-turret deployable structure (Outpost / Fortress / Factory /
/// Relay / Nexus / WarStation).
///
/// Each instance is N turrets (and later: M escort fakes) arranged at fixed
/// offsets around a center point. PowerGrid gates whether the structure is
/// "operational" (turrets fire) or "powered down" (turrets present but
/// unable to attack — Phase 2.5+ feature).
///
/// Phase 2 (this slice): just turret-cluster spawn. Power gating logged
/// only. Escorts and runtime freeze come in Phase 2.5.
/// </summary>
public interface IStationDeployer : IComponentInterface
{
    /// Deploy a structure of `typeKey` at world (pixelX, pixelY) on `freq`,
    /// owned by `deployer`. Returns the new instance, or null on failure.
    StructureInstance? Deploy(Arena arena, string typeKey, int pixelX, int pixelY, short freq, Player deployer);

    /// Despawn a deployed structure (cleanup all turrets).
    void Despawn(Arena arena, StructureInstance structure);

    /// Snapshot of all structures in `arena`.
    IReadOnlyList<StructureInstance> GetStructures(Arena arena);
}

public sealed class StructureInstance
{
    public required string TypeKey;          // "outpost", "fortress", ...
    public required short OwnerFreq;
    public required string OwnerName;
    public int CenterPixelX;
    public int CenterPixelY;
    public DateTime DeployedAt;
    /// Reserved for Phase 2.5+ — power state from PowerGrid.
    public bool IsPowered;

    /// Upgrade level (0 = base). Future phases will scale weapon range /
    /// damage / fire rate per level. Phase 2.5+ tracks the level only.
    public int UpgradeLevel;

    /// PowerGrid subscription token. Stored so Despawn can call
    /// IPowerGrid.Unsubscribe — without it every despawned structure
    /// permanently leaks a tick callback. Internal to StationDeployer's
    /// teardown path.
    public PowerSubscription? PowerSub;
}
