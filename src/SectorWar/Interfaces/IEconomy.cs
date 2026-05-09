using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

public interface IEconomy : IComponentInterface
{
    long GetBalance(Player player);

    bool TryEarn(Player player, long amount, string reason);

    bool TrySpend(Player player, long amount, string reason);
}
