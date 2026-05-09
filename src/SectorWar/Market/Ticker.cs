namespace SS.SectorWar.Market;

public sealed class Ticker
{
    public string Symbol { get; }
    public string DisplayName { get; }
    public double Drift { get; }
    public double Volatility { get; }

    public double Price { get; private set; }

    private const int HistoryCapacity = 60;
    private readonly Queue<double> _history = new(HistoryCapacity);

    public Ticker(string symbol, string displayName, double initialPrice, double drift, double volatility)
    {
        Symbol = symbol;
        DisplayName = displayName;
        Price = initialPrice;
        Drift = drift;
        Volatility = volatility;
        _history.Enqueue(initialPrice);
    }

    public void UpdatePrice(double newPrice)
    {
        Price = newPrice;
        _history.Enqueue(newPrice);
        while (_history.Count > HistoryCapacity)
            _history.Dequeue();
    }

    // Spread defaults — 2% on each side, 4% round-trip cost (the second money sink).
    public long Bid => (long)Math.Floor(Price * 0.98);
    public long Ask => (long)Math.Ceiling(Price * 1.02);
}
