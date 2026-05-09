using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — DeployableShop subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Lets players spend credits to buy + deploy structures (Pylon / Outpost /
// WarStation) at their current position via chat commands. Routes to IPylon
// and IStationDeployer (which already enforce placement rules + power
// gating). Refunds on deploy failure to keep the player's credits whole.
//
// SOURCE
// ------
// Standalone module `Modules/DeployableShop.cs` stays as a library copy.
//
// COMMANDS
// --------
//   ?buypylon       — costs PylonCost. Pylon doesn't need power.
//   ?buyoutpost     — costs OutpostCost. Requires friendly pylon power.
//   ?buywarstation  — costs WarStationCost. Requires friendly pylon power
//                     (the WarStation's command core itself needs 3 pylons
//                     to fire — that gate is enforced inside StationDeployer).
//   ?deployshop     — list available deployables + prices + your balance.
//
// All four are in `groupdef.dir/default` (anyone can buy).
//
// CONF KEY HOISTING (deferred)
// ----------------------------
// Prices are still constants (PylonCost, OutpostCost, WarStationCost). Hoisting
// to `[SectorWar] DeployableShop*Cost` is queued for a follow-up — keeps this
// merge mechanical.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: cached IComponentBroker reference (used to look up
//                  IEconomy / IPylon / IStationDeployer per-call).
//   - Conf keys read: NONE (price hoisting deferred).
//   - Persisted data: NONE.
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE.
//   - Commands registered: cmd_buypylon, cmd_buyoutpost, cmd_buywarstation,
//                          cmd_deployshop.
//   - Broker interfaces published: IDeployableShop.
//
// CALLBACKS HOOKED: NONE — purely command-driven.
//
// BROKER LOOKUPS
// --------------
// IEconomy / IPylon / IStationDeployer are looked up per-call (not cached) so
// hot-reloads of those subsystems don't leave stale handles. Each lookup is
// bracketed by ReleaseInterface in a `finally` block.
//
// REFUND ON FAIL
// --------------
// Every TryBuy* path follows the pattern: pre-flight check (pylon power) →
// TrySpend → Deploy → refund-on-fail. This keeps the player's balance whole
// even when the underlying deployer rejects placement (collision, out-of-bounds,
// pool exhausted, etc.). The refund's transaction tag is suffixed with
// "refund" so audit logs can pair the spend + refund.
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave 3: pre-flight pylon-power check before TrySpend (was post-spend in an
// even earlier draft, leading to spurious refunds).
// =============================================================================

public sealed partial class SectorWar : IDeployableShop
{
    // -------------------------------------------------------------------------
    // CONSTANTS — prices + command names
    //
    // Public because the original module was public and other places (Inventory
    // dialogs, ?menu list) read them. Keep them public to preserve any external
    // references during the parallel-coexistence period.
    // -------------------------------------------------------------------------

    public const long DeployableShopPylonCost = 25_000;
    public const long DeployableShopOutpostCost = 50_000;
    public const long DeployableShopWarStationCost = 200_000;

    private const string DeployableShopBuyPylonCommand = "buypylon";
    private const string DeployableShopBuyOutpostCommand = "buyoutpost";
    private const string DeployableShopBuyWarStationCommand = "buywarstation";
    private const string DeployableShopShopCommand = "deployshop";
    /// <summary>Unified subcommand dispatcher: "?buy pylon" / "?buy outpost" /
    /// "?buy warstation" / "?buy" (lists shop). Registered arena-scoped so
    /// it doesn't collide with SS.Core.Modules.Buy in arenas that attach it.</summary>
    private const string DeployableShopBuyCommand = "buy";

    /// <summary>Single source of truth for what's in the shop. UI consumers
    /// (Inventory ?menu, ?deployshop) read from here so prices + descriptions
    /// can't drift away from the actual TryBuy implementation.</summary>
    private static readonly DeployableOffering[] _deployableShopOfferings =
    {
        new("pylon",      "Pylon",      DeployableShopPylonCost,
            "Power source + claim point. Place anywhere."),
        new("outpost",    "Outpost",    DeployableShopOutpostCost,
            "4 corner turrets + escort frigate. Needs pylon power."),
        new("warstation", "WarStation", DeployableShopWarStationCost,
            "8 perimeter guns + heavy command core. Command needs 3 pylons."),
    };

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    /// <summary>Cached broker handle so the per-call IEconomy / IPylon /
    /// IStationDeployer lookups have a target. Set in Load, cleared in Unload.</summary>
    private IComponentBroker? _deployableShopBroker;

    private InterfaceRegistrationToken<IDeployableShop>? _deployableShopToken;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadDeployableShop(IComponentBroker broker)
    {
        _deployableShopBroker = broker;
        _commandManager.AddCommand(DeployableShopBuyPylonCommand, Command_DeployableShopBuyPylon);
        _commandManager.AddCommand(DeployableShopBuyOutpostCommand, Command_DeployableShopBuyOutpost);
        _commandManager.AddCommand(DeployableShopBuyWarStationCommand, Command_DeployableShopBuyWarStation);
        _commandManager.AddCommand(DeployableShopShopCommand, Command_DeployableShopShop);
        _deployableShopToken = broker.RegisterInterface<IDeployableShop>(this);
        _logManager.LogM(LogLevel.Info, LogCategory, "DeployableShop subsystem loaded.");
    }

    private void UnloadDeployableShop(IComponentBroker broker)
    {
        if (_deployableShopToken is not null)
            broker.UnregisterInterface(ref _deployableShopToken);
        _commandManager.RemoveCommand(DeployableShopBuyPylonCommand, Command_DeployableShopBuyPylon);
        _commandManager.RemoveCommand(DeployableShopBuyOutpostCommand, Command_DeployableShopBuyOutpost);
        _commandManager.RemoveCommand(DeployableShopBuyWarStationCommand, Command_DeployableShopBuyWarStation);
        _commandManager.RemoveCommand(DeployableShopShopCommand, Command_DeployableShopShop);
        _deployableShopBroker = null;
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    //
    // The legacy buy* commands are zone-wide (registered in Load). The unified
    // "?buy <kind>" dispatcher is arena-scoped because SS.Core.Modules.Buy
    // also registers "?buy" arena-scoped — coexistence requires our handler
    // to only bind in arenas SectorWar attaches to.
    // -------------------------------------------------------------------------

    private void AttachDeployableShop(Arena arena)
    {
        _commandManager.AddCommand(DeployableShopBuyCommand, Command_DeployableShopBuy, arena);
    }

    private void DetachDeployableShop(Arena arena)
    {
        _commandManager.RemoveCommand(DeployableShopBuyCommand, Command_DeployableShopBuy, arena);
    }

    // -------------------------------------------------------------------------
    // IDeployableShop IMPLEMENTATION
    // -------------------------------------------------------------------------

    /// <summary>
    /// External callers (Inventory dialogs etc.) invoke this entry point with
    /// a `kind` string ("pylon" / "outpost" / "warstation"). Returns false +
    /// a user-facing message on any failure (not in arena, ship is Spec, kind
    /// unknown, insufficient credits, deploy-rejected).
    /// </summary>
    bool IDeployableShop.TryBuy(Player player, string kind, out string message)
    {
        if (player.Arena is null) { message = "Not in an arena."; return false; }
        if (player.Ship == ShipType.Spec) { message = "Get in a ship to deploy."; return false; }
        if (_deployableShopBroker is null) { message = "Shop temporarily offline."; return false; }

        // Trim + lowercase keeps the dispatch tolerant of casing variations
        // from external menus / chat commands.
        return (kind?.Trim().ToLowerInvariant()) switch
        {
            "pylon"      => TryBuyDeployableShopPylonImpl(player, out message),
            "outpost"    => TryBuyDeployableShopStructureImpl(player, "outpost", "Outpost",
                                DeployableShopOutpostCost, out message),
            "warstation" => TryBuyDeployableShopStructureImpl(player, "warstation", "WarStation",
                                DeployableShopWarStationCost, out message),
            _ => Fail(out message,
                $"Unknown deployable '{kind}'. Known: pylon, outpost, warstation."),
        };

        static bool Fail(out string message, string text) { message = text; return false; }
    }

    IReadOnlyList<DeployableOffering> IDeployableShop.GetOfferings() => _deployableShopOfferings;

    // -------------------------------------------------------------------------
    // INTERNAL HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pylon-specific buy path. Pylons don't need pre-flight pylon-power
    /// (they're the source!). Spend → Deploy → refund-on-fail.
    /// </summary>
    private bool TryBuyDeployableShopPylonImpl(Player player, out string message)
    {
        IEconomy? econ = _deployableShopBroker!.GetInterface<IEconomy>();
        IPylon? pylon = _deployableShopBroker.GetInterface<IPylon>();
        try
        {
            if (econ is null || pylon is null) { message = "Shop temporarily offline."; return false; }

            if (!econ.TrySpend(player, DeployableShopPylonCost, "DeployableShop:Pylon"))
            {
                message = $"Insufficient credits. Pylon costs {DeployableShopPylonCost:N0}; " +
                          $"balance is {econ.GetBalance(player):N0}.";
                return false;
            }

            var inst = pylon.Deploy(player.Arena!, player.Position.X, player.Position.Y, player.Freq, player);
            if (inst is null)
            {
                // Refund so the player isn't out 25k for a deploy the placement
                // rules rejected. The "refund" tag pairs with the spend in audit logs.
                econ.TryEarn(player, DeployableShopPylonCost, "DeployableShop:Pylon refund");
                message = "Pylon deploy failed (refunded). Check the server log.";
                return false;
            }

            message = $"Pylon deployed at ({inst.CenterPixelX},{inst.CenterPixelY}) " +
                      $"for {DeployableShopPylonCost:N0} cr. Balance: {econ.GetBalance(player):N0}.";
            return true;
        }
        finally
        {
            if (econ is not null) _deployableShopBroker.ReleaseInterface(ref econ);
            if (pylon is not null) _deployableShopBroker.ReleaseInterface(ref pylon);
        }
    }

    /// <summary>
    /// Generic structure-buy path used by outpost, warstation, and any
    /// future composite-turret structure. Pre-flight checks pylon power
    /// (Wave-3 fix: doing this BEFORE TrySpend avoids a refund cycle on the
    /// common "no pylon nearby" rejection), then spends, then deploys,
    /// refunding on failure.
    /// </summary>
    private bool TryBuyDeployableShopStructureImpl(Player player, string typeKey,
        string displayName, long cost, out string message)
    {
        IEconomy? econ = _deployableShopBroker!.GetInterface<IEconomy>();
        IPylon? pylonChk = _deployableShopBroker.GetInterface<IPylon>();
        IStationDeployer? deployer = _deployableShopBroker.GetInterface<IStationDeployer>();
        try
        {
            if (econ is null || pylonChk is null || deployer is null)
            {
                message = "Shop temporarily offline."; return false;
            }

            // Wave-3 pre-flight: check power BEFORE spending. Saves a refund
            // round-trip on the common "you're not in pylon range" path.
            if (!pylonChk.IsPowered(player.Arena!, player.Position.X, player.Position.Y, player.Freq))
            {
                message = "Out of pylon power range. Place a pylon first or move into one's ring.";
                return false;
            }

            if (!econ.TrySpend(player, cost, $"DeployableShop:{typeKey}"))
            {
                message = $"Insufficient credits. {displayName} costs {cost:N0}; " +
                          $"balance is {econ.GetBalance(player):N0}.";
                return false;
            }

            var inst = deployer.Deploy(player.Arena!, typeKey,
                player.Position.X, player.Position.Y, player.Freq, player);
            if (inst is null)
            {
                econ.TryEarn(player, cost, $"DeployableShop:{typeKey} refund");
                message = $"{displayName} deploy failed (refunded).";
                return false;
            }

            message = $"{displayName} deployed for {cost:N0} cr. Balance: {econ.GetBalance(player):N0}.";
            return true;
        }
        finally
        {
            if (econ is not null) _deployableShopBroker.ReleaseInterface(ref econ);
            if (pylonChk is not null) _deployableShopBroker.ReleaseInterface(ref pylonChk);
            if (deployer is not null) _deployableShopBroker.ReleaseInterface(ref deployer);
        }
    }

    // -------------------------------------------------------------------------
    // COMMAND HANDLERS
    //
    // All four use the IDeployableShop entry point so the chat-command path
    // and the Inventory-dialog path share one set of failure messages.
    // -------------------------------------------------------------------------

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Buy + deploy a pylon at your current position. Costs 25,000 credits.")]
    private void Command_DeployableShopBuyPylon(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        IDeployableShop self = this;
        self.TryBuy(player, "pylon", out string msg);
        _chat.SendMessage(player, msg);
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Buy + deploy an outpost. Costs 50,000 credits. Requires a friendly pylon nearby.")]
    private void Command_DeployableShopBuyOutpost(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        IDeployableShop self = this;
        self.TryBuy(player, "outpost", out string msg);
        _chat.SendMessage(player, msg);
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Buy + deploy a WarStation. Costs 200,000 credits. Requires a friendly pylon nearby.")]
    private void Command_DeployableShopBuyWarStation(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        IDeployableShop self = this;
        self.TryBuy(player, "warstation", out string msg);
        _chat.SendMessage(player, msg);
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Show available deployables, prices, and your credit balance.")]
    private void Command_DeployableShopShop(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (_deployableShopBroker is null) return;
        IEconomy? econ = _deployableShopBroker.GetInterface<IEconomy>();
        try
        {
            long balance = econ?.GetBalance(player) ?? 0;
            _chat.SendMessage(player, "--- Deployable Shop ---");
            // Pull from the offerings table so the listing stays in sync with
            // the TryBuy dispatch above.
            foreach (var o in _deployableShopOfferings)
            {
                _chat.SendMessage(player,
                    $"  ?buy {o.Kind}  {o.Cost:N0} cr - {o.Description}");
            }
            _chat.SendMessage(player, $"Your balance: {balance:N0} cr.");
        }
        finally
        {
            if (econ is not null) _deployableShopBroker.ReleaseInterface(ref econ);
        }
    }

    [CommandHelp(Targets = CommandTarget.None, Args = "[pylon|outpost|warstation]",
        Description = "Deploy from the shop. ?buy with no arg lists the catalog.")]
    private void Command_DeployableShopBuy(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        ReadOnlySpan<char> trimmed = parameters.Trim();

        // Bare "?buy" → shop listing. Reuses the same path as ?deployshop so
        // the formatting can't drift between the two help surfaces.
        if (trimmed.IsEmpty)
        {
            Command_DeployableShopShop(commandName, parameters, player, target);
            return;
        }

        // Take the first whitespace-delimited token as the deployable kind.
        // Anything after it is currently ignored (no per-kind options yet).
        int spaceIdx = trimmed.IndexOfAny(' ', '\t');
        ReadOnlySpan<char> kindSpan = spaceIdx < 0 ? trimmed : trimmed[..spaceIdx];
        string kind = kindSpan.ToString();

        IDeployableShop self = this;
        self.TryBuy(player, kind, out string msg);
        _chat.SendMessage(player, msg);
    }
}
