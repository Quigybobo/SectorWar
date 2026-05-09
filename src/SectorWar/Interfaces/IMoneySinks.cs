using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

public interface IMoneySinks : IComponentInterface
{
    long GetJackpot();

    // Try to gamble `amount` cr on a 50/50 dice roll.
    // Returns false if the player can't afford the stake; otherwise sets win/delta:
    //   win=true means net positive (+90% of stake), win=false means -100% of stake.
    bool TryPlayDice(Player player, long amount, out bool win, out long delta);
}
