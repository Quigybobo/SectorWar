using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

public interface IRpg : IComponentInterface
{
    // Read snapshot of a player's RPG state.
    bool TryGetStats(Player player, out int level, out long xp, out int prestigeTier);

    // Attempt to prestige. Returns true if successful with the new tier; false with a failureReason
    // suitable for chat (e.g. "Need to reach level 100 first").
    bool TryPrestige(Player player, out int newTier, out string failureReason);
}
