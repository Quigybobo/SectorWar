using Microsoft.Extensions.ObjectPool;
using SS.SectorWar.Interfaces;
using SS.SectorWar.Market;
using SS.SectorWar.Persist;
using SS.Core;
using SS.Core.ComponentInterfaces;
using System.Threading;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — Market subsystem.
// =============================================================================
//
// PURPOSE
// -------
// Five simulated stock tickers with GBM (Geometric Brownian Motion) price
// movement. Players ?invest / ?divest at bid/ask spread; the spread feeds
// the credit-sink model. Holdings persist via IPersist.
//
// SOURCE
// ------
// Standalone module `Modules/Market.cs` stays as a library copy. Async
// because IPersist.RegisterPersistentDataAsync is awaited in Load.
//
// PRICE PROVIDER
// --------------
// Seeded with Environment.TickCount each startup. Prices are NOT persisted —
// they regenerate from the seed on restart. This is intentional (no
// save-scumming the dip).
//
// RUNTIME OWNERSHIP
//   - Owned state: 5 in-memory tickers (lock-protected dict),
//                  per-player Holdings dict (lock-protected),
//                  cached IEconomy + IPersist + delegate-persist registration.
//   - Conf keys read: NONE.
//   - Persisted data: PerPlayer/Forever/Global Holdings (PersistKeys.Market).
//   - Fakes registered: NONE.
//   - Timers scheduled: 30s IServerTimer ticker tick.
//   - Commands registered: cmd_market, cmd_invest, cmd_divest, cmd_portfolio.
//   - Broker interfaces published: IMarketReader.
//
// THREADING
// ---------
// IServerTimer thread pool. Lock-protected ticker + holdings state.
// Persist GetData/SetData are called by IPersist on its own thread; we
// snapshot under the holdings lock then write/read outside the lock.
// =============================================================================

public sealed partial class SectorWar : IMarketReader
{
    private const string MarketCommand = "market";
    private const string MarketInvestCommand = "invest";
    private const string MarketDivestCommand = "divest";
    private const string MarketPortfolioCommand = "portfolio";

    private const int MarketTickIntervalMs = 30_000;
    private const byte MarketPersistVersion = 1;

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private IEconomy? _marketEconomy;
    private IPersist? _marketPersist;
    private DelegatePersistentData<Player>? _marketPersistRegistration;
    private InterfaceRegistrationToken<IMarketReader>? _marketToken;

    private PlayerDataKey<MarketPlayerStateData> _marketPdKey;
    private readonly Dictionary<string, Ticker> _marketTickers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _marketTickerLock = new();
    private SimulatedPriceProvider? _marketProvider;

    private TimerDelegate? _marketTickDelegate;

    // -------------------------------------------------------------------------
    // ASYNC LOAD / UNLOAD
    // -------------------------------------------------------------------------

    private async Task LoadMarketAsync(IComponentBroker broker, CancellationToken ct)
    {
        _marketPdKey = _playerData.AllocatePlayerData<MarketPlayerStateData>();

        _marketEconomy = broker.GetInterface<IEconomy>();
        if (_marketEconomy is null)
        {
            _logManager.LogM(LogLevel.Warn, LogCategory,
                "Market: IEconomy not available — Market disabled.");
            return;
        }

        // Register Holdings persistence. PersistKeys.Market is the wire-format
        // identifier; Forever/Global means saved across restarts and shared
        // across arenas (player has ONE portfolio zone-wide).
        _marketPersist = broker.GetInterface<IPersist>();
        if (_marketPersist is not null)
        {
            _marketPersistRegistration = new DelegatePersistentData<Player>(
                PersistKeys.Market,
                PersistInterval.Forever,
                PersistScope.Global,
                Persist_Market_GetData,
                Persist_Market_SetData,
                Persist_Market_ClearData);

            await _marketPersist.RegisterPersistentDataAsync(_marketPersistRegistration);
        }

        SeedMarketTickers();

        _marketProvider = new SimulatedPriceProvider(Environment.TickCount);

        _marketTickDelegate = OnTick_Market;
        _serverTimer.SetTimer(_marketTickDelegate, MarketTickIntervalMs,
            MarketTickIntervalMs, this);

        _commandManager.AddCommand(MarketCommand, Command_Market);
        _commandManager.AddCommand(MarketInvestCommand, Command_MarketInvest);
        _commandManager.AddCommand(MarketDivestCommand, Command_MarketDivest);
        _commandManager.AddCommand(MarketPortfolioCommand, Command_MarketPortfolio);

        _marketToken = broker.RegisterInterface<IMarketReader>(this);

        _logManager.LogM(LogLevel.Info, LogCategory,
            $"Market subsystem loaded with {_marketTickers.Count} tickers.");
    }

    private async Task UnloadMarketAsync(IComponentBroker broker, CancellationToken ct)
    {
        if (_marketToken is not null)
            broker.UnregisterInterface(ref _marketToken);

        _commandManager.RemoveCommand(MarketCommand, Command_Market);
        _commandManager.RemoveCommand(MarketInvestCommand, Command_MarketInvest);
        _commandManager.RemoveCommand(MarketDivestCommand, Command_MarketDivest);
        _commandManager.RemoveCommand(MarketPortfolioCommand, Command_MarketPortfolio);

        if (_marketTickDelegate is not null)
        {
            _serverTimer.ClearTimer(_marketTickDelegate, this);
            _marketTickDelegate = null;
        }

        if (_marketPersist is not null && _marketPersistRegistration is not null)
        {
            await _marketPersist.UnregisterPersistentDataAsync(_marketPersistRegistration);
            _marketPersistRegistration = null;
        }

        if (_marketPersist is not null) broker.ReleaseInterface(ref _marketPersist);
        if (_marketEconomy is not null) broker.ReleaseInterface(ref _marketEconomy);

        _playerData.FreePlayerData(ref _marketPdKey);
    }

    private void AttachMarket(Arena arena) { /* zone-wide */ }
    private void DetachMarket(Arena arena) { /* zone-wide */ }

    // -------------------------------------------------------------------------
    // IMarketReader IMPLEMENTATION
    // -------------------------------------------------------------------------

    IReadOnlyList<TickerSnapshot> IMarketReader.GetTickers()
    {
        var result = new List<TickerSnapshot>();
        lock (_marketTickerLock)
        {
            foreach (var t in _marketTickers.Values)
                result.Add(new TickerSnapshot(t.Symbol, t.DisplayName, t.Price, t.Bid, t.Ask));
        }
        return result;
    }

    IReadOnlyDictionary<string, long> IMarketReader.GetHoldings(Player player)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!player.TryGetExtraData(_marketPdKey, out MarketPlayerStateData? pd)) return result;
        lock (pd.Lock)
        {
            foreach (var (sym, qty) in pd.Holdings)
                result[sym] = qty;
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // TICKER SEEDING
    //
    // Five tickers with distinct risk profiles. drift = expected log-return
    // per tick; vol = std-dev of log-return. Sigma=0.05 means typical ±5%
    // moves per 30s tick.
    // -------------------------------------------------------------------------

    private void SeedMarketTickers()
    {
        AddMarketTicker("APH",  "SectorWar Industries", 100.0,  0.001, 0.020);  // safe blue chip
        AddMarketTicker("SLAS", "Slas SectorWar Sub",    50.0,  0.002, 0.040);  // medium
        AddMarketTicker("BNTY", "Bounty Holdings",      25.0,  0.000, 0.060);  // volatile, no drift
        AddMarketTicker("WARP", "Warp Drive Tech",     200.0,  0.005, 0.080);  // growth, high drift+vol
        AddMarketTicker("EMPB", "EMP Bombs Inc",        10.0, -0.002, 0.120);  // junk, negative drift
    }

    private void AddMarketTicker(string symbol, string name, double price, double drift, double vol)
    {
        _marketTickers[symbol] = new Ticker(symbol, name, price, drift, vol);
    }

    // -------------------------------------------------------------------------
    // TIMER CALLBACK
    // -------------------------------------------------------------------------

    private bool OnTick_Market()
    {
        if (_marketProvider is null) return true;
        lock (_marketTickerLock)
        {
            foreach (Ticker t in _marketTickers.Values) _marketProvider.Tick(t);
        }
        return true;
    }

    // -------------------------------------------------------------------------
    // COMMANDS
    // -------------------------------------------------------------------------

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Lists current market prices.")]
    private void Command_Market(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters,
        Player player, ITarget target)
    {
        _chat.SendMessage(player, "--- SectorWar Market ---");
        lock (_marketTickerLock)
        {
            foreach (Ticker t in _marketTickers.Values)
            {
                _chat.SendMessage(player,
                    $"  {t.Symbol,-5} buy {t.Ask,4} cr / sell {t.Bid,4} cr   ({t.DisplayName})");
            }
        }
        _chat.SendMessage(player, "Use ?invest <symbol> <qty> to buy, ?divest to sell.");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = "<symbol> <quantity>",
        Description = "Buy shares of a ticker at current ask price.")]
    private void Command_MarketInvest(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (_marketEconomy is null) return;
        if (!player.TryGetExtraData(_marketPdKey, out MarketPlayerStateData? pd)) return;

        if (!TryParseMarketTradeArgs(parameters, out string symbol, out long qty, out string error))
        {
            _chat.SendMessage(player, error); return;
        }

        long ask;
        lock (_marketTickerLock)
        {
            if (!_marketTickers.TryGetValue(symbol, out Ticker? ticker))
            {
                _chat.SendMessage(player, $"Unknown ticker '{symbol}'."); return;
            }
            ask = ticker.Ask;
        }

        long cost = ask * qty;
        if (!_marketEconomy.TrySpend(player, cost, $"market buy {qty} {symbol}"))
        {
            long balance = _marketEconomy.GetBalance(player);
            _chat.SendMessage(player,
                $"You need {cost} cr to buy {qty} {symbol}. You have {balance} cr.");
            return;
        }

        long newQty;
        lock (pd.Lock)
        {
            pd.Holdings.TryGetValue(symbol, out long current);
            newQty = current + qty;
            pd.Holdings[symbol] = newQty;
        }

        _chat.SendMessage(player, $"Bought {qty} {symbol} for {cost} cr. You now own {newQty}.");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = "<symbol> <quantity>",
        Description = "Sell shares of a ticker at current bid price.")]
    private void Command_MarketDivest(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (_marketEconomy is null) return;
        if (!player.TryGetExtraData(_marketPdKey, out MarketPlayerStateData? pd)) return;

        if (!TryParseMarketTradeArgs(parameters, out string symbol, out long qty, out string error))
        {
            _chat.SendMessage(player, error); return;
        }

        long bid;
        lock (_marketTickerLock)
        {
            if (!_marketTickers.TryGetValue(symbol, out Ticker? ticker))
            {
                _chat.SendMessage(player, $"Unknown ticker '{symbol}'."); return;
            }
            bid = ticker.Bid;
        }

        long newQty;
        lock (pd.Lock)
        {
            pd.Holdings.TryGetValue(symbol, out long current);
            if (current < qty)
            {
                _chat.SendMessage(player, $"You only have {current} shares of {symbol}.");
                return;
            }
            newQty = current - qty;
            if (newQty == 0) pd.Holdings.Remove(symbol);
            else pd.Holdings[symbol] = newQty;
        }

        long proceeds = bid * qty;
        _marketEconomy.TryEarn(player, proceeds, $"market sell {qty} {symbol}");
        _chat.SendMessage(player, $"Sold {qty} {symbol} for {proceeds} cr. You now own {newQty}.");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Lists your current ticker holdings and their bid value.")]
    private void Command_MarketPortfolio(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!player.TryGetExtraData(_marketPdKey, out MarketPlayerStateData? pd)) return;

        var entries = new List<(string Symbol, long Qty, long Bid)>();
        lock (pd.Lock)
        {
            foreach (var (sym, qty) in pd.Holdings)
            {
                long bid;
                lock (_marketTickerLock)
                {
                    bid = _marketTickers.TryGetValue(sym, out Ticker? t) ? t.Bid : 0;
                }
                entries.Add((sym, qty, bid));
            }
        }

        if (entries.Count == 0)
        {
            _chat.SendMessage(player, "You don't own any shares. Try ?market and ?invest.");
            return;
        }

        _chat.SendMessage(player, "--- Your Stocks ---");
        long totalValue = 0;
        foreach (var (sym, qty, bid) in entries)
        {
            long value = qty * bid;
            totalValue += value;
            _chat.SendMessage(player, $"  {sym,-5} x{qty} (worth {value} cr if sold now)");
        }
        _chat.SendMessage(player, $"Total: {totalValue} cr if you sold everything now.");
    }

    private static bool TryParseMarketTradeArgs(ReadOnlySpan<char> parameters,
        out string symbol, out long qty, out string error)
    {
        symbol = "";
        qty = 0;
        error = "Usage: ?invest <symbol> <quantity>";

        int spaceIdx = parameters.IndexOf(' ');
        if (spaceIdx < 1 || spaceIdx >= parameters.Length - 1) return false;

        symbol = parameters[..spaceIdx].Trim().ToString().ToUpperInvariant();
        ReadOnlySpan<char> qtyText = parameters[(spaceIdx + 1)..].Trim();

        if (!long.TryParse(qtyText, out qty) || qty <= 0)
        {
            error = "Quantity must be a positive integer."; return false;
        }
        return true;
    }

    // -------------------------------------------------------------------------
    // PERSIST
    // -------------------------------------------------------------------------

    private void Persist_Market_GetData(Player? player, Stream outStream)
    {
        if (player is null || !player.TryGetExtraData(_marketPdKey,
            out MarketPlayerStateData? pd)) return;

        Dictionary<string, long> snapshot;
        lock (pd.Lock)
        {
            if (pd.Holdings.Count == 0) return;
            snapshot = new Dictionary<string, long>(pd.Holdings);
        }

        using BinaryWriter writer = new(outStream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(MarketPersistVersion);
        writer.Write(snapshot.Count);
        foreach (var (sym, qty) in snapshot)
        {
            writer.Write(sym);
            writer.Write(qty);
        }
    }

    private void Persist_Market_SetData(Player? player, Stream inStream)
    {
        if (player is null || !player.TryGetExtraData(_marketPdKey,
            out MarketPlayerStateData? pd)) return;

        using BinaryReader reader = new(inStream, System.Text.Encoding.UTF8, leaveOpen: true);

        byte version = reader.ReadByte();
        if (version != MarketPersistVersion)
        {
            _logManager.LogP(LogLevel.Warn, LogCategory, player,
                $"Unknown market persist version {version}.");
            return;
        }

        int count = reader.ReadInt32();
        var loaded = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string sym = reader.ReadString();
            long qty = reader.ReadInt64();
            if (qty > 0) loaded[sym] = qty;
        }

        lock (pd.Lock)
        {
            pd.Holdings.Clear();
            foreach (var (sym, qty) in loaded) pd.Holdings[sym] = qty;
        }
    }

    private void Persist_Market_ClearData(Player? player)
    {
        if (player is null || !player.TryGetExtraData(_marketPdKey,
            out MarketPlayerStateData? pd)) return;
        lock (pd.Lock) { pd.Holdings.Clear(); }
    }

    // -------------------------------------------------------------------------
    // PER-PLAYER DATA
    // -------------------------------------------------------------------------

    private sealed class MarketPlayerStateData : IResettable
    {
        public Dictionary<string, long> Holdings = new(StringComparer.OrdinalIgnoreCase);
        public readonly Lock Lock = new();

        bool IResettable.TryReset()
        {
            lock (Lock) { Holdings.Clear(); }
            return true;
        }
    }
}
