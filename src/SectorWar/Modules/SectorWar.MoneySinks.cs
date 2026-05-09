using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentInterfaces;
using System.Threading;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — MoneySinks subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Two credit-drains feeding a shared jackpot pool:
//   1. Wealth tax — periodic small percentage of every player's balance over
//      a configurable threshold.
//   2. ?dice — 50/50 gamble with a 5% house edge that funds the jackpot.
//
// Also exposes IMoneySinks so the Inventory ?menu can offer a dice-from-menu
// path that pre-bonds the bet.
//
// SOURCE
// ------
// Standalone module `Modules/MoneySinks.cs` stays as a library copy.
//
// CONF MIGRATION
// --------------
// Original used section `[SectorWar.MoneySinks]`. Under the consolidated
// umbrella those keys move to `[SectorWar]` with the `MoneySinks` prefix:
//
//   was [SectorWar.MoneySinks] WealthTaxIntervalSeconds = 3600
//   becomes [SectorWar] MoneySinksWealthTaxIntervalSeconds = 3600
//   was [SectorWar.MoneySinks] WealthTaxPercent = 1
//   becomes [SectorWar] MoneySinksWealthTaxPercent = 1
//   was [SectorWar.MoneySinks] WealthTaxThresholdCredits = 1000000
//   becomes [SectorWar] MoneySinksWealthTaxThresholdCredits = 1000000
//
// Documented in `docs/SECTORWAR_CONF.md`.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: jackpot pool (long, lock-protected),
//                  RNG instance (lock-protected),
//                  cached IEconomy handle.
//   - Conf keys read: 3 wealth-tax keys (interval / percent / threshold),
//                     read lazily inside the tax tick (no per-arena conf bind).
//   - Persisted data: NONE (jackpot resets on zone restart — TODO Phase later
//                     could persist via IPersist if jackpots get big).
//   - Fakes registered: NONE.
//   - Timers scheduled: 60-second IServerTimer poll for wealth-tax tick gate.
//   - Commands registered: cmd_dice, cmd_jackpot.
//   - Broker interfaces published: IMoneySinks.
//
// CALLBACKS HOOKED: NONE.
//
// THREADING
// ---------
// IServerTimer fires on the thread pool. _jackpotLock + _rngLock guard the
// shared mutable state. IEconomy.TrySpend / TryEarn / GetBalance are
// thread-safe per the SS.NET contract. _chat.SendMessage is thread-safe.
// _playerData.Lock/Unlock for the player-list snapshot.
//
// TIMER GATE PATTERN
// ------------------
// IServerTimer fires every 60 seconds, but the actual wealth-tax application
// only runs once every WealthTaxIntervalSeconds (default 3600 = 1h). The
// 60-second poll lets the conf-driven cadence change without re-scheduling
// the timer. Original module pattern preserved.
//
// WAVE-FIXES PRESERVED
// --------------------
// Snapshot the player list under `_playerData.Lock()`, then iterate OUTSIDE
// the lock — IEconomy.TrySpend on a busy player can take long enough to
// matter, and we don't want to block player-list mutations.
// =============================================================================

public sealed partial class SectorWar : IMoneySinks
{
    private const string MoneySinksDiceCommand = "dice";
    private const string MoneySinksJackpotCommand = "jackpot";

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    /// <summary>Cached IEconomy. Acquired in Load (required dependency —
    /// MoneySinks fails load if it can't bind). Released in Unload.</summary>
    private IEconomy? _moneySinksEconomy;

    /// <summary>Token for unregistering IMoneySinks on Unload.</summary>
    private InterfaceRegistrationToken<IMoneySinks>? _moneySinksToken;

    /// <summary>Cached delegate so SetTimer/ClearTimer use the same instance
    /// (the timer key matching is by delegate reference).</summary>
    private TimerDelegate? _moneySinksWealthTaxDelegate;

    /// <summary>Shared RNG for dice rolls.</summary>
    private readonly Random _moneySinksRng = new();

    /// <summary>Guards the RNG (Random isn't thread-safe).</summary>
    private readonly Lock _moneySinksRngLock = new();

    /// <summary>Shared jackpot pool. Wealth tax + dice losses fund it.
    /// Future slice will pay it out (e.g. KOTH-style or boss-kill bonus).</summary>
    private long _moneySinksJackpotPool;

    /// <summary>Guards the jackpot pool. Leaf lock — never held across
    /// IEconomy calls or other umbrella locks.</summary>
    private readonly Lock _moneySinksJackpotLock = new();

    /// <summary>Last wall-clock time the wealth-tax tick actually applied
    /// the tax. The 60s timer poll compares against
    /// WealthTaxIntervalSeconds and only runs when enough time has passed.</summary>
    private DateTime _moneySinksLastWealthTaxRun = DateTime.UtcNow;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Bind IEconomy (required), register commands + interface, schedule the
    /// 60-second wealth-tax poll. Returns false from caller if IEconomy
    /// can't be bound — MoneySinks doesn't function without it.
    /// </summary>
    /// <remarks>
    /// Note: in the partial-class umbrella, an individual subsystem's Load
    /// failing is handled by it logging a Warn and degrading; the overall
    /// IModule.Load only fails if the umbrella itself can't proceed. This
    /// preserves phong's "won't crash the zone" requirement at the cost of
    /// running with one subsystem disabled.
    /// </remarks>
    private void LoadMoneySinks(IComponentBroker broker)
    {
        _moneySinksEconomy = broker.GetInterface<IEconomy>();
        if (_moneySinksEconomy is null)
        {
            _logManager.LogM(LogLevel.Warn, LogCategory,
                "MoneySinks: IEconomy not available — wealth tax + ?dice will be disabled.");
            return;
        }

        _commandManager.AddCommand(MoneySinksDiceCommand, Command_MoneySinksDice);
        _commandManager.AddCommand(MoneySinksJackpotCommand, Command_MoneySinksJackpot);

        // Cache the delegate so the matched ClearTimer in Unload uses the
        // same reference. SS.NET's timer table is delegate-keyed.
        _moneySinksWealthTaxDelegate = OnMoneySinksWealthTaxTick;

        // 60-second poll cadence — the actual tax-application gate is inside
        // the tick callback, comparing against WealthTaxIntervalSeconds.
        _serverTimer.SetTimer(_moneySinksWealthTaxDelegate, 60_000, 60_000, this);

        _moneySinksToken = broker.RegisterInterface<IMoneySinks>(this);

        _logManager.LogM(LogLevel.Info, LogCategory, "MoneySinks subsystem loaded.");
    }

    /// <summary>Reverse of Load. Unregister + clear timer + release IEconomy.</summary>
    private void UnloadMoneySinks(IComponentBroker broker)
    {
        if (_moneySinksToken is not null)
            broker.UnregisterInterface(ref _moneySinksToken);

        _commandManager.RemoveCommand(MoneySinksDiceCommand, Command_MoneySinksDice);
        _commandManager.RemoveCommand(MoneySinksJackpotCommand, Command_MoneySinksJackpot);

        if (_moneySinksWealthTaxDelegate is not null)
        {
            _serverTimer.ClearTimer(_moneySinksWealthTaxDelegate, this);
            _moneySinksWealthTaxDelegate = null;
        }

        if (_moneySinksEconomy is not null)
            broker.ReleaseInterface(ref _moneySinksEconomy);
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH (no-ops — zone-wide)
    // -------------------------------------------------------------------------

    private void AttachMoneySinks(Arena arena) { /* zone-wide */ }
    private void DetachMoneySinks(Arena arena) { /* zone-wide */ }

    // -------------------------------------------------------------------------
    // IMoneySinks IMPLEMENTATION
    // -------------------------------------------------------------------------

    long IMoneySinks.GetJackpot()
    {
        lock (_moneySinksJackpotLock) { return _moneySinksJackpotPool; }
    }

    /// <summary>
    /// Dice-from-menu entry point. Same odds as the chat command (50/50,
    /// 5% house edge), but skips the chat dispatch — caller renders its
    /// own UI. Returns false on bad input (no economy, non-positive amount,
    /// insufficient balance); on a successful roll, <paramref name="win"/>
    /// + <paramref name="delta"/> describe the outcome.
    /// </summary>
    bool IMoneySinks.TryPlayDice(Player player, long amount, out bool win, out long delta)
    {
        win = false;
        delta = 0;
        if (_moneySinksEconomy is null || amount <= 0) return false;
        if (!_moneySinksEconomy.TrySpend(player, amount, "dice stake (menu)")) return false;

        bool gotWin;
        lock (_moneySinksRngLock) { gotWin = _moneySinksRng.NextDouble() < 0.5; }

        if (gotWin)
        {
            // Win path: stake * 1.9 returned (= 0.9x profit), 0.1x to jackpot.
            long payout = amount * 19 / 10;
            long houseCut = amount * 1 / 10;
            _moneySinksEconomy.TryEarn(player, payout, "dice win (menu)");
            AddToMoneySinksJackpot(houseCut);
            win = true;
            delta = payout - amount;
        }
        else
        {
            // Lose path: stake gone; 50% to jackpot, 50% vanishes.
            AddToMoneySinksJackpot(amount / 2);
            win = false;
            delta = -amount;
        }
        return true;
    }

    // -------------------------------------------------------------------------
    // WEALTH-TAX TIMER CALLBACK
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs every 60 seconds (IServerTimer poll). Applies the wealth tax only
    /// when WealthTaxIntervalSeconds has elapsed since last apply. This
    /// indirection lets `?reloadconf` change the cadence without re-scheduling
    /// the timer.
    /// </summary>
    private bool OnMoneySinksWealthTaxTick()
    {
        if (_moneySinksEconomy is null) return true;

        // Defaults match the original module so an unconfigured zone behaves
        // identically to the standalone build.
        int intervalSec = 3600;
        int taxPercent = 1;
        long threshold = 1_000_000;

        // Conf is per-arena; we don't have a target arena from the timer
        // tick. Cheap approach: snapshot the first online player's arena and
        // read from there. If no players online, defaults are used.
        Player? probe = null;
        _playerData.Lock();
        try
        {
            foreach (Player p in _playerData.Players)
            {
                if (p.Arena is not null) { probe = p; break; }
            }
        }
        finally
        {
            _playerData.Unlock();
        }

        if (probe?.Arena?.Cfg is { } cfg)
        {
            intervalSec = _configManager.GetInt(cfg, ConfSection,
                "MoneySinksWealthTaxIntervalSeconds", 3600);
            taxPercent = _configManager.GetInt(cfg, ConfSection,
                "MoneySinksWealthTaxPercent", 1);
            threshold = _configManager.GetInt(cfg, ConfSection,
                "MoneySinksWealthTaxThresholdCredits", 1_000_000);
        }

        if ((DateTime.UtcNow - _moneySinksLastWealthTaxRun).TotalSeconds < intervalSec)
            return true;  // not yet time — keep timer running

        _moneySinksLastWealthTaxRun = DateTime.UtcNow;
        ApplyMoneySinksWealthTax(taxPercent, threshold);

        return true;
    }

    /// <summary>
    /// Walks every Playing player, taxes their balance over threshold, sends
    /// a chat notice, accumulates the take into the jackpot. Snapshots the
    /// player list under lock then iterates outside the lock.
    /// </summary>
    private void ApplyMoneySinksWealthTax(int taxPercent, long threshold)
    {
        if (_moneySinksEconomy is null) return;
        if (taxPercent <= 0 || threshold <= 0) return;

        long totalTaxed = 0;
        int taxedCount = 0;

        // Snapshot under the player-data lock to avoid holding it during
        // IEconomy + IChat calls.
        var snapshot = new List<Player>();
        _playerData.Lock();
        try
        {
            foreach (Player p in _playerData.Players)
            {
                if (p.Status == PlayerState.Playing)
                    snapshot.Add(p);
            }
        }
        finally
        {
            _playerData.Unlock();
        }

        foreach (Player p in snapshot)
        {
            long balance = _moneySinksEconomy.GetBalance(p);
            if (balance <= threshold) continue;

            long taxableExcess = balance - threshold;
            long tax = taxableExcess * taxPercent / 100;
            if (tax <= 0) continue;

            if (_moneySinksEconomy.TrySpend(p, tax, "wealth tax"))
            {
                AddToMoneySinksJackpot(tax);
                totalTaxed += tax;
                taxedCount++;
                _chat.SendMessage(p,
                    $"Wealth tax: -{tax} cr (over {threshold} cr threshold). Goes to jackpot.");
            }
        }

        if (taxedCount > 0)
        {
            _logManager.LogM(LogLevel.Info, LogCategory,
                $"Wealth tax: {taxedCount} player(s) taxed, {totalTaxed} cr total → jackpot");
        }
    }

    /// <summary>Lock-protected accumulator. Negative or zero amounts are
    /// silently ignored (defensive — wouldn't change the pool but might
    /// indicate a bug elsewhere).</summary>
    private void AddToMoneySinksJackpot(long amount)
    {
        if (amount <= 0) return;
        lock (_moneySinksJackpotLock)
        {
            _moneySinksJackpotPool += amount;
        }
    }

    // -------------------------------------------------------------------------
    // COMMAND HANDLERS
    // -------------------------------------------------------------------------

    [CommandHelp(Targets = CommandTarget.None, Args = "<amount>",
        Description = "Gamble credits on a 50/50 dice roll. Win: +90% of stake. Lose: -100%. (5% house edge feeds the jackpot.)")]
    private void Command_MoneySinksDice(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (_moneySinksEconomy is null) return;

        ReadOnlySpan<char> amountText = parameters.Trim();
        if (amountText.IsEmpty || !long.TryParse(amountText, out long amount) || amount <= 0)
        {
            _chat.SendMessage(player, "Usage: ?dice <amount>");
            return;
        }

        if (!_moneySinksEconomy.TrySpend(player, amount, "dice stake"))
        {
            long balance = _moneySinksEconomy.GetBalance(player);
            _chat.SendMessage(player, $"You only have {balance} cr.");
            return;
        }

        bool win;
        lock (_moneySinksRngLock)
        {
            win = _moneySinksRng.NextDouble() < 0.5;
        }

        if (win)
        {
            // Win: stake * 1.9 returned, stake * 0.1 to jackpot (5% house edge
            // overall — the player gets 0.9x profit, the system keeps 0.1x).
            long payout = amount * 19 / 10;
            long houseCut = amount * 1 / 10;

            _moneySinksEconomy.TryEarn(player, payout, "dice win");
            AddToMoneySinksJackpot(houseCut);

            long balance = _moneySinksEconomy.GetBalance(player);
            _chat.SendMessage(player, $"Dice: WIN. +{payout - amount} cr profit. Balance: {balance}");
        }
        else
        {
            // Lose: 50% of stake to jackpot, 50% vanishes (lower jackpot share
            // than win-path's 10% to keep the pool growth balanced).
            long jackpotShare = amount / 2;
            AddToMoneySinksJackpot(jackpotShare);

            long balance = _moneySinksEconomy.GetBalance(player);
            _chat.SendMessage(player, $"Dice: LOSE. -{amount} cr. Balance: {balance}");
        }
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Shows the current jackpot pool (fed by wealth tax + dice losses).")]
    private void Command_MoneySinksJackpot(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        long pool;
        lock (_moneySinksJackpotLock)
        {
            pool = _moneySinksJackpotPool;
        }
        _chat.SendMessage(player, $"Jackpot pool: {pool} cr");
    }
}
