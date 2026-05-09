using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

/// <summary>
/// One deployed pylon. Static turret + power radius + claim weight.
/// Owned by a freq, attackable by enemies, projects power to in-range
/// structures (PowerGrid module reads this to drive structure on/off state).
/// </summary>
public sealed class PylonInstance
{
    /// The static-turret fake-player that IS the pylon. Nullable because
    /// restore-from-persist paths don't have a Player handle (the original
    /// deployer may be offline; the new fake-player is owned by IStaticTurret
    /// and not exposed through that interface). Downstream consumers
    /// (PowerGrid, SectorClaim, etc.) read OwnerFreq + position only.
    public Player? Anchor;
    /// The arena this pylon lives in. Always set (both on initial deploy and
    /// on persistence-restore). Use this instead of Anchor.Arena to avoid
    /// NREs when the Anchor is null after a restore.
    public required Arena Arena;
    public required short OwnerFreq;
    public required string OwnerName;    // who deployed it (for logs / persistence)
    public int CenterPixelX;
    public int CenterPixelY;
    public int PowerRadiusPixels = 24 * 16;  // 24 tiles default
    public int ClaimWeight = 1;
    public DateTime DeployedAt;

    /// Upgrade level (0 = base). Future phases will scale damage / range /
    /// fire rate per level. Phase 2.5+ tracks the level only.
    public int UpgradeLevel;
}

/// <summary>
/// Pylon registry + deployment API. Pylons are the foundation of the
/// power+claim infrastructure. They:
///   - Project power (PowerGrid uses this to gate structure operation)
///   - Hold claim weight (SectorClaim uses this to compute arena ownership)
///   - Are themselves destroyable static turrets
/// </summary>
public interface IPylon : IComponentInterface
{
    /// <summary>
    /// Deploy a pylon at world (pixelX, pixelY) on `freq`, owned by `deployer`.
    /// Returns the new PylonInstance, or null on failure (e.g. wall, max-per-arena).
    /// </summary>
    PylonInstance? Deploy(Arena arena, int pixelX, int pixelY, short freq, Player deployer);

    /// <summary>
    /// Remove a pylon (e.g. on destruction or expire). Despawns the static
    /// turret, removes from registry.
    /// </summary>
    void Despawn(Arena arena, PylonInstance pylon);

    /// <summary>
    /// All currently-active pylons in the arena (read-only snapshot).
    /// PowerGrid + SectorClaim iterate this each tick.
    /// </summary>
    IReadOnlyList<PylonInstance> GetPylons(Arena arena);

    /// <summary>
    /// Returns true if (pixelX, pixelY) is within power radius of any
    /// friendly (matching freq) pylon. Used by structures to determine
    /// power state. Phase 1 stub: just checks distance to all pylons of
    /// matching freq.
    /// </summary>
    bool IsPowered(Arena arena, int pixelX, int pixelY, short freq);

    /// Fired when a new pylon deploys.
    event Action<PylonInstance>? PylonDeployed;

    /// Fired when a pylon is removed (destroyed or expired).
    event Action<PylonInstance>? PylonDespawned;
}
