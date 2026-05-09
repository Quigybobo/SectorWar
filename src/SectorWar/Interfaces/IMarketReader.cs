using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

public sealed record TickerSnapshot(string Symbol, string DisplayName, double Price, long Bid, long Ask);

public interface IMarketReader : IComponentInterface
{
    IReadOnlyList<TickerSnapshot> GetTickers();

    // Returns the player's holdings keyed by symbol â†’ quantity.
    IReadOnlyDictionary<string, long> GetHoldings(Player player);
}
