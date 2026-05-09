namespace SS.SectorWar.Market;

public interface IPriceProvider
{
    void Tick(Ticker ticker);
}
