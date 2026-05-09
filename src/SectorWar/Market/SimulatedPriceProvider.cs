namespace SS.SectorWar.Market;

// Geometric Brownian Motion price simulator.
// Per tick (dt = 1): newPrice = price * exp((drift - 0.5 * vol^2) + vol * randn)
public sealed class SimulatedPriceProvider : IPriceProvider
{
    private readonly Random _rng;
    private readonly object _lock = new();

    public SimulatedPriceProvider(int seed)
    {
        _rng = new Random(seed);
    }

    public void Tick(Ticker ticker)
    {
        double randn;
        lock (_lock)
        {
            // Box-Muller transform: two uniforms â†’ one standard normal.
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = 1.0 - _rng.NextDouble();
            randn = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        double drift = ticker.Drift;
        double sigma = ticker.Volatility;

        double exponent = (drift - 0.5 * sigma * sigma) + sigma * randn;
        double newPrice = ticker.Price * Math.Exp(exponent);

        // Floor to keep prices positive and tradeable.
        if (newPrice < 0.01) newPrice = 0.01;

        ticker.UpdatePrice(newPrice);
    }
}
