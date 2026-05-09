using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

/// <summary>
/// Per-arena snapshot of which freqs hold how many pylons. Drives sector
/// ownership for the multi-arena war: the freq with the most pylon-claim
/// weight controls that arena. Below 50% of total = "contested".
/// </summary>
public sealed class SectorClaimSnapshot
{
    public required string ArenaName { get; init; }
    /// freq â†’ claim weight (sum of contributing pylons' ClaimWeight)
    public required IReadOnlyDictionary<short, int> ClaimByFreq { get; init; }
    /// The dominant freq (highest claim). Null when no pylons present.
    public short? DominantFreq { get; init; }
    /// True when DominantFreq holds majority (>= 50% of total claim).
    public bool IsControlled { get; init; }
    /// True when there's claim activity but no dominant freq.
    public bool IsContested { get; init; }
}

/// <summary>
/// Sector claim tracker. Listens to IPylon deploy/despawn events and
/// maintains per-arena per-freq pylon-count maps. Fires events when
/// dominant freq changes. Future-feeds: SectorWar gate logic, win
/// conditions, victory detection.
/// </summary>
public interface ISectorClaim : IComponentInterface
{
    /// Returns the current snapshot for `arenaName`, or null if the arena
    /// is not part of the linked sector.
    SectorClaimSnapshot? GetSnapshot(string arenaName);

    /// All arenas tracked.
    IEnumerable<SectorClaimSnapshot> GetAllSnapshots();

    /// Fires when an arena's dominant freq flips (or becomes null).
    /// Args: (arenaName, oldDominantFreq, newDominantFreq)
    event Action<string, short?, short?>? ArenaOwnerChanged;
}
