using Microsoft.Extensions.ObjectPool;
using SS.SectorWar.Interfaces;
using SS.SectorWar.Persist;
using SS.SectorWar.Util;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System.Threading;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — Rpg subsystem (XP / levels / credits / prestige).
// =============================================================================
//
// PURPOSE
// -------
// Core RPG progression: XP from kills+greens, levels via XpCurve, credit
// economy (IEconomy), prestige (reset level for permanent +10% multiplier).
// 11 chat commands. Async persist via IPersist + DelegatePersistentData.
//
// SOURCE
// ------
// Standalone module `Modules/Rpg.cs` stays as a library copy. Async because
// IPersist registration is awaited.
//
// CONF SECTION
// ------------
// Stays in `[SectorWar]` (not `[SectorWar]`) — this is the "RPG" feature
// surface, distinct from gameplay knobs that live under [SectorWar]. zone
// admins reading conf find RPG keys in the obvious place. Documented
// exception in `docs/SECTORWAR_CONF.md`.
//
// PERSISTENCE
// -----------
// PersistKeys.Rpg / PersistInterval.Forever / PersistScope.Global. Schema
// versioned; v3 currently:
//   v1 = { byte ver, long xp, int level }
//   v2 = + long credits
//   v3 = + int prestigeTier
//
// PRESTIGE
// --------
// At level 100, ?prestige resets level to 1 + xp to 0 in exchange for a
// permanent +10% XP/credit gain per prestige tier (stacking). Exposed via
// IRpg.TryPrestige for menu wiring.
//
// COMMANDS (11)
//   ?sectorwar / ?level / ?xp / ?shipinfo / ?bal / ?balance / ?pay /
//   ?baltop / ?top / ?prestige / ?give
//
// RUNTIME OWNERSHIP
//   - Owned state: per-player Xp/Level/Credits/PrestigeTier (lock-protected)
//   - Conf keys read: [SectorWar] XpPerKill / CreditsPerKill / XpPerGreen
//                     / CreditsPerGreen / BaseXpForLevel / TransferFeePercent
//   - Persisted: yes (Forever/Global)
//   - Broker interfaces published: IEconomy, IRpg
//
// CALLBACKS HOOKED (zone-wide)
//   - KillCallback / GreenCallback
//
// THREADING
// ---------
// Mainloop callbacks. ?pay uses stable lock-ordering (sender vs recipient by
// Player.Id) to avoid deadlock on cross-player credit transfer.
// =============================================================================

public sealed partial class SectorWar : IEconomy, IRpg
{
    // Conf surface owned by the Rpg subsystem — see docs/ARENA_SETTINGS.md.
    // Pinned to a field: the framework's Help scanner only walks
    // fields/properties/events, not class declarations.
    [ConfigHelp<int>("SectorWar", "XpPerKill", ConfigScope.Arena,
        Default = 100, Min = 0, Max = 1000000,
        Description = "XP awarded to the killer on each player kill.")]
    [ConfigHelp<int>("SectorWar", "XpPerGreen", ConfigScope.Arena,
        Default = 5, Min = 0, Max = 100000,
        Description = "XP awarded for each green prize picked up.")]
    [ConfigHelp<int>("SectorWar", "BaseXpForLevel", ConfigScope.Arena,
        Default = 250, Min = 1, Max = 1000000,
        Description = "XP-curve coefficient. XP needed to reach level N is BaseXpForLevel * (N-1)^2.")]
    [ConfigHelp<int>("SectorWar", "CreditsPerKill", ConfigScope.Arena,
        Default = 50, Min = 0, Max = 1000000,
        Description = "Credits awarded per kill.")]
    [ConfigHelp<int>("SectorWar", "CreditsPerGreen", ConfigScope.Arena,
        Default = 2, Min = 0, Max = 100000,
        Description = "Credits awarded per green.")]
    [ConfigHelp<int>("SectorWar", "TransferFeePercent", ConfigScope.Arena,
        Default = 5, Min = 0, Max = 100,
        Description = "Percent fee on ?pay transfers; vanishes (sink).")]
    private const string RpgConfSection = "SectorWar";

    private const string RpgSectorWarCommand = "sectorwar";
    private const string RpgLevelCommand = "level";
    private const string RpgXpCommand = "xp";
    private const string RpgShipInfoCommand = "shipinfo";
    private const string RpgPrestigeCommand = "prestige";
    private const string RpgGiveCommand = "give";
    private const string RpgBalCommand = "bal";
    private const string RpgBalanceCommand = "balance";
    private const string RpgPayCommand = "pay";
    private const string RpgBalTopCommand = "baltop";
    private const string RpgTopCommand = "top";

    private const int RpgPrestigeRequiredLevel = 100;
    private const byte RpgPersistVersion = 3;

    private static readonly string[] RpgShipSections =
    {
        "Warbird", "Javelin", "Spider", "Leviathan",
        "Terrier", "Weasel", "Lancaster", "Shark",
    };

    private static readonly string[] RpgPerShipSettings =
    {
        "MaximumEnergy", "MaximumRecharge", "MaximumThrust", "MaximumSpeed",
        "MaximumRotation", "BulletSpeed", "BombSpeed",
    };

    private static readonly (string Section, string Key)[] RpgGlobalSettings =
    {
        ("Bullet", "BulletDamageLevel"),
        ("Bomb",   "BombDamageLevel"),
        ("Bullet", "BulletAliveTime"),
        ("Bomb",   "BombAliveTime"),
    };

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private IPersist? _rpgPersist;
    private DelegatePersistentData<Player>? _rpgPersistRegistration;
    private InterfaceRegistrationToken<IEconomy>? _rpgEconomyToken;
    private InterfaceRegistrationToken<IRpg>? _rpgToken;
    private PlayerDataKey<RpgPlayerData> _rpgPdKey;

    private sealed class RpgPlayerData : IResettable
    {
        public long Xp;
        public int Level = 1;
        public long Credits;
        public int PrestigeTier;
        public readonly Lock Lock = new();

        bool IResettable.TryReset()
        {
            lock (Lock)
            {
                Xp = 0; Level = 1; Credits = 0; PrestigeTier = 0;
            }
            return true;
        }
    }

    // -------------------------------------------------------------------------
    // ASYNC LOAD / UNLOAD
    // -------------------------------------------------------------------------

    private async Task LoadRpgAsync(IComponentBroker broker, CancellationToken ct)
    {
        _rpgPdKey = _playerData.AllocatePlayerData<RpgPlayerData>();

        _rpgPersist = broker.GetInterface<IPersist>();
        if (_rpgPersist is not null)
        {
            _rpgPersistRegistration = new DelegatePersistentData<Player>(
                PersistKeys.Rpg,
                PersistInterval.Forever,
                PersistScope.Global,
                Persist_Rpg_GetData,
                Persist_Rpg_SetData,
                Persist_Rpg_ClearData);
            await _rpgPersist.RegisterPersistentDataAsync(_rpgPersistRegistration);
        }
        else
        {
            _logManager.LogM(LogLevel.Warn, LogCategory,
                "Rpg: IPersist not available — RPG progress will not persist.");
        }

        KillCallback.Register(broker, OnKill_Rpg);
        GreenCallback.Register(broker, OnGreen_Rpg);

        _rpgEconomyToken = broker.RegisterInterface<IEconomy>(this);
        _rpgToken = broker.RegisterInterface<IRpg>(this);

        _logManager.LogM(LogLevel.Info, LogCategory, "Rpg subsystem loaded.");
    }

    private async Task UnloadRpgAsync(IComponentBroker broker, CancellationToken ct)
    {
        if (_rpgToken is not null) broker.UnregisterInterface(ref _rpgToken);
        if (_rpgEconomyToken is not null) broker.UnregisterInterface(ref _rpgEconomyToken);

        KillCallback.Unregister(broker, OnKill_Rpg);
        GreenCallback.Unregister(broker, OnGreen_Rpg);

        if (_rpgPersist is not null && _rpgPersistRegistration is not null)
        {
            await _rpgPersist.UnregisterPersistentDataAsync(_rpgPersistRegistration);
            _rpgPersistRegistration = null;
        }
        if (_rpgPersist is not null) broker.ReleaseInterface(ref _rpgPersist);

        _playerData.FreePlayerData(ref _rpgPdKey);
    }

    private void AttachRpg(Arena arena)
    {
        _commandManager.AddCommand(RpgSectorWarCommand, Command_RpgSectorWar, arena);
        _commandManager.AddCommand(RpgLevelCommand, Command_RpgLevel, arena);
        _commandManager.AddCommand(RpgXpCommand, Command_RpgXp, arena);
        _commandManager.AddCommand(RpgShipInfoCommand, Command_RpgShipInfo, arena);
        _commandManager.AddCommand(RpgBalCommand, Command_RpgBal, arena);
        _commandManager.AddCommand(RpgBalanceCommand, Command_RpgBal, arena);
        _commandManager.AddCommand(RpgPayCommand, Command_RpgPay, arena);
        _commandManager.AddCommand(RpgBalTopCommand, Command_RpgBalTop, arena);
        _commandManager.AddCommand(RpgTopCommand, Command_RpgBalTop, arena);
        _commandManager.AddCommand(RpgPrestigeCommand, Command_RpgPrestige, arena);
        _commandManager.AddCommand(RpgGiveCommand, Command_RpgGive, arena);
    }

    private void DetachRpg(Arena arena)
    {
        _commandManager.RemoveCommand(RpgSectorWarCommand, Command_RpgSectorWar, arena);
        _commandManager.RemoveCommand(RpgLevelCommand, Command_RpgLevel, arena);
        _commandManager.RemoveCommand(RpgXpCommand, Command_RpgXp, arena);
        _commandManager.RemoveCommand(RpgShipInfoCommand, Command_RpgShipInfo, arena);
        _commandManager.RemoveCommand(RpgBalCommand, Command_RpgBal, arena);
        _commandManager.RemoveCommand(RpgBalanceCommand, Command_RpgBal, arena);
        _commandManager.RemoveCommand(RpgPayCommand, Command_RpgPay, arena);
        _commandManager.RemoveCommand(RpgBalTopCommand, Command_RpgBalTop, arena);
        _commandManager.RemoveCommand(RpgTopCommand, Command_RpgBalTop, arena);
        _commandManager.RemoveCommand(RpgPrestigeCommand, Command_RpgPrestige, arena);
        _commandManager.RemoveCommand(RpgGiveCommand, Command_RpgGive, arena);
    }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    private void OnKill_Rpg(Arena arena, Player killer, Player killed,
        short bounty, short flagCount, short points, Prize green)
    {
        if (killer is null) return;
        // Arena-attach guard: callbacks register zone-wide via broker, so this
        // fires for every arena. Skip arenas where SectorWar isn't attached so
        // we don't leak XP/credits/level messages into unrelated arenas.
        arena.TryGetExtraData(_adKey, out ArenaData? ad);
        if (ad?.Arena is null) return;
        if (!killer.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return;

        int xpPerKill = _configManager.GetInt(arena.Cfg!, RpgConfSection, "XpPerKill", 100);
        int creditsPerKill = _configManager.GetInt(arena.Cfg!, RpgConfSection, "CreditsPerKill", 50);

        AwardRpgXp(killer, pd, xpPerKill);
        EarnRpgCredits(killer, pd, creditsPerKill, "kill");
    }

    private void OnGreen_Rpg(Player player, int x, int y, Prize prize)
    {
        if (player is null || player.Arena is null) return;
        // Arena-attach guard: see OnKill_Rpg for rationale. Without this, every
        // green pickup in every arena triggers SectorWar XP/credits awards.
        player.Arena.TryGetExtraData(_adKey, out ArenaData? ad);
        if (ad?.Arena is null) return;
        if (!player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return;

        int xpPerGreen = _configManager.GetInt(player.Arena.Cfg!, RpgConfSection, "XpPerGreen", 5);
        int creditsPerGreen = _configManager.GetInt(player.Arena.Cfg!, RpgConfSection, "CreditsPerGreen", 2);

        AwardRpgXp(player, pd, xpPerGreen);
        EarnRpgCredits(player, pd, creditsPerGreen, "green");
    }

    // -------------------------------------------------------------------------
    // INTERNAL HELPERS
    // -------------------------------------------------------------------------

    private void EarnRpgCredits(Player player, RpgPlayerData pd, long amount, string reason)
    {
        if (amount <= 0) return;

        long newBalance, boosted;
        lock (pd.Lock)
        {
            // Prestige multiplier: +10% per tier, stacking.
            boosted = amount + (amount * pd.PrestigeTier / 10);
            pd.Credits += boosted;
            newBalance = pd.Credits;
        }
        // Drivel — fires on every credit grant including green pickups,
        // so several per second per active player. Significant credit
        // events (purchases, round rewards) fire their own descriptive
        // logs at the call site if needed.
        _logManager.LogP(LogLevel.Drivel, "Economy", player,
            $"+{boosted} cr ({reason}), balance={newBalance}");
    }

    private void AwardRpgXp(Player player, RpgPlayerData pd, long amount)
    {
        if (amount <= 0) return;
        Arena? arena = player.Arena;
        if (arena is null) return;

        long baseXp = _configManager.GetInt(arena.Cfg!, RpgConfSection, "BaseXpForLevel", 250);

        int startLevel, newLevel, prestige;
        lock (pd.Lock)
        {
            startLevel = pd.Level;
            prestige = pd.PrestigeTier;

            long boosted = amount + (amount * prestige / 10);
            pd.Xp += boosted;

            while (pd.Xp >= XpCurve.XpForLevel(pd.Level + 1, baseXp))
                pd.Level++;

            newLevel = pd.Level;
        }

        if (newLevel > startLevel)
        {
            string prefix = prestige > 0 ? $"[*{prestige}] " : "";
            _chat.SendArenaMessage(arena, ChatSound.Goal,
                $"{prefix}{player.Name} reached level {newLevel}!");
        }
    }

    // -------------------------------------------------------------------------
    // COMMANDS
    // -------------------------------------------------------------------------

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Replies with a heartbeat from the SectorWar RPG module.")]
    private void Command_RpgSectorWar(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        Arena? arena = player.Arena;
        string arenaName = arena is not null ? arena.Name : "(none)";
        _chat.SendMessage(player, $"SectorWar online. arena={arenaName}");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Shows your current SectorWar RPG level, prestige, and total XP.")]
    private void Command_RpgLevel(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return;

        long xp; int level; int prestige;
        lock (pd.Lock) { xp = pd.Xp; level = pd.Level; prestige = pd.PrestigeTier; }

        if (prestige > 0)
        {
            int bonusPct = prestige * 10;
            _chat.SendMessage(player,
                $"Level {level} | Prestige *{prestige} | {xp} XP | +{bonusPct}% gains");
        }
        else
        {
            _chat.SendMessage(player, $"Level {level} | {xp} XP");
        }
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Shows XP progress toward your next level.")]
    private void Command_RpgXp(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return;
        Arena? arena = player.Arena;
        if (arena is null) return;

        long baseXp = _configManager.GetInt(arena.Cfg!, RpgConfSection, "BaseXpForLevel", 250);

        long xp; int level;
        lock (pd.Lock) { xp = pd.Xp; level = pd.Level; }

        long nextLevelXp = XpCurve.XpForLevel(level + 1, baseXp);
        long need = nextLevelXp - xp;

        _chat.SendMessage(player, $"XP: {xp} / {nextLevelXp} (need {need} for level {level + 1})");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Shows your current credit balance. Aliases: ?balance.")]
    private void Command_RpgBal(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return;
        long credits;
        lock (pd.Lock) { credits = pd.Credits; }
        _chat.SendMessage(player, $"Balance: {credits} credits");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = "<player> <amount>",
        Description = "Transfer credits to another player. Sender pays a small fee.")]
    private void Command_RpgPay(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? senderPd)) return;
        Arena? arena = player.Arena;
        if (arena is null) return;

        int spaceIdx = parameters.IndexOf(' ');
        if (spaceIdx < 1 || spaceIdx >= parameters.Length - 1)
        {
            _chat.SendMessage(player, "Usage: ?pay <player> <amount>"); return;
        }

        ReadOnlySpan<char> recipientName = parameters[..spaceIdx].Trim();
        ReadOnlySpan<char> amountText = parameters[(spaceIdx + 1)..].Trim();

        if (!long.TryParse(amountText, out long amount) || amount <= 0)
        {
            _chat.SendMessage(player, "Amount must be a positive integer."); return;
        }

        Player? recipient = _playerData.FindPlayer(recipientName);
        if (recipient is null || recipient.Status != PlayerState.Playing)
        {
            _chat.SendMessage(player, $"No player named '{recipientName}' is online."); return;
        }
        if (recipient == player) { _chat.SendMessage(player, "Can't pay yourself."); return; }
        if (!recipient.TryGetExtraData(_rpgPdKey, out RpgPlayerData? recipientPd))
        { _chat.SendMessage(player, "Recipient has no RPG data yet."); return; }

        int feePercent = _configManager.GetInt(arena.Cfg!, RpgConfSection, "TransferFeePercent", 5);
        long fee = amount * feePercent / 100;
        long totalCost = amount + fee;

        // Stable lock-ordering by Player.Id avoids cross-transfer deadlock.
        Lock first = senderPd.Lock;
        Lock second = recipientPd.Lock;
        if (player.Id > recipient.Id) { first = recipientPd.Lock; second = senderPd.Lock; }

        bool success;
        long newSenderBalance = 0, newRecipientBalance = 0;

        lock (first)
        {
            lock (second)
            {
                if (senderPd.Credits < totalCost) { success = false; }
                else
                {
                    senderPd.Credits -= totalCost;
                    recipientPd.Credits += amount;
                    newSenderBalance = senderPd.Credits;
                    newRecipientBalance = recipientPd.Credits;
                    success = true;
                }
            }
        }

        if (!success)
        {
            _chat.SendMessage(player, $"Not enough credits. Need {totalCost} ({amount} + {fee} fee).");
            return;
        }

        _logManager.LogM(LogLevel.Info, "Economy",
            $"Transfer: {player.Name} -> {recipient.Name} amount={amount} fee={fee} " +
            $"sender_bal={newSenderBalance} recipient_bal={newRecipientBalance}");

        _chat.SendMessage(player,
            $"Sent {amount} cr to {recipient.Name} (fee: {fee} cr). Balance: {newSenderBalance}");
        _chat.SendMessage(recipient,
            $"Received {amount} cr from {player.Name}. Balance: {newRecipientBalance}");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Lists the top 10 wealthiest online players. Aliases: ?top, ?baltop.")]
    private void Command_RpgBalTop(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        var leaderboard = new List<(string Name, long Credits)>();

        _playerData.Lock();
        try
        {
            foreach (Player p in _playerData.Players)
            {
                if (p.Status != PlayerState.Playing) continue;
                if (p.Type == ClientType.Fake) continue;   // skip turret bots / HQ defenders / pylons
                if (!p.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) continue;

                long credits;
                lock (pd.Lock) { credits = pd.Credits; }
                leaderboard.Add((p.Name ?? "?", credits));
            }
        }
        finally { _playerData.Unlock(); }

        leaderboard.Sort((a, b) => b.Credits.CompareTo(a.Credits));

        _chat.SendMessage(player, "--- Top wealthy (online) ---");
        int rank = 1;
        foreach (var (name, credits) in leaderboard.Take(10))
        {
            _chat.SendMessage(player, $"  {rank}. {name}: {credits} cr");
            rank++;
        }
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Probes per-ship ClientSettings identifiers.")]
    private void Command_RpgShipInfo(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        Arena? arena = player.Arena;
        if (arena is null) { _chat.SendMessage(player, "Not in an arena."); return; }

        int perShipAttempted = 0, perShipResolved = 0;

        foreach (string ship in RpgShipSections)
        {
            int shipResolved = 0;
            int recharge = -1;

            foreach (string key in RpgPerShipSettings)
            {
                perShipAttempted++;
                if (_clientSettings.TryGetSettingsIdentifier(ship, key, out _))
                { perShipResolved++; shipResolved++; }
            }

            if (_clientSettings.TryGetSettingsIdentifier(ship, "MaximumRecharge", out var rechargeId))
                recharge = _clientSettings.GetSetting(arena, rechargeId);

            _chat.SendMessage(player,
                $"{ship}: {shipResolved}/{RpgPerShipSettings.Length} per-ship, MaxRecharge={recharge}");
        }

        _chat.SendMessage(player, "--- global settings ---");
        int globalResolved = 0;
        foreach ((string section, string key) in RpgGlobalSettings)
        {
            if (_clientSettings.TryGetSettingsIdentifier(section, key, out var id))
            {
                globalResolved++;
                int value = _clientSettings.GetSetting(arena, id);
                _chat.SendMessage(player, $"  {section}:{key} = {value}");
            }
            else _chat.SendMessage(player, $"  {section}:{key} = NOT FOUND");
        }

        _chat.SendMessage(player,
            $"Per-ship: {perShipResolved}/{perShipAttempted} | Global: {globalResolved}/{RpgGlobalSettings.Length}");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Reset to level 1 for permanent +10% gains. Requires level 100.")]
    private void Command_RpgPrestige(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return;
        Arena? arena = player.Arena;
        if (arena is null) return;

        int newTier; bool prestiged;
        lock (pd.Lock)
        {
            if (pd.Level < RpgPrestigeRequiredLevel)
            { prestiged = false; newTier = pd.PrestigeTier; }
            else
            {
                pd.PrestigeTier++;
                pd.Level = 1;
                pd.Xp = 0;
                newTier = pd.PrestigeTier;
                prestiged = true;
            }
        }

        if (!prestiged)
        {
            _chat.SendMessage(player,
                $"You need to reach level {RpgPrestigeRequiredLevel} before you can prestige.");
            return;
        }

        _chat.SendArenaMessage(arena, ChatSound.Goal,
            $"{player.Name} prestiged to *{newTier}! (+{newTier * 10}% gains forever)");
    }

    [CommandHelp(Targets = CommandTarget.None | CommandTarget.Player,
        Args = "<amount> | <player> <amount>",
        Description = "Sysop: grant or remove credits.")]
    private void Command_RpgGive(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        Player? recipient = null;
        long amount = 0;

        if (target.TryGetPlayerTarget(out Player? tp))
        {
            recipient = tp;
            if (parameters.IsWhiteSpace() || !long.TryParse(parameters.Trim(), out amount))
            { _chat.SendMessage(player, "Usage: :<player>:?give <amount>"); return; }
        }
        else
        {
            int spaceIdx = parameters.IndexOf(' ');
            if (spaceIdx < 1 || spaceIdx >= parameters.Length - 1)
            { _chat.SendMessage(player, "Usage: ?give <player> <amount>"); return; }

            ReadOnlySpan<char> nameText = parameters[..spaceIdx].Trim();
            ReadOnlySpan<char> amtText = parameters[(spaceIdx + 1)..].Trim();

            recipient = _playerData.FindPlayer(nameText);
            if (recipient is null || recipient.Status != PlayerState.Playing)
            { _chat.SendMessage(player, $"No player named '{nameText}' is online."); return; }

            if (!long.TryParse(amtText, out amount))
            { _chat.SendMessage(player, "Amount must be an integer."); return; }
        }

        if (amount == 0) { _chat.SendMessage(player, "Amount must be non-zero."); return; }
        if (recipient is null) return;
        if (!recipient.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd))
        { _chat.SendMessage(player, "Recipient has no RPG data yet."); return; }

        long newBalance;
        lock (pd.Lock)
        {
            pd.Credits += amount;
            if (pd.Credits < 0) pd.Credits = 0;
            newBalance = pd.Credits;
        }

        _logManager.LogM(LogLevel.Info, "Economy",
            $"SYSOP {player.Name} adjusted {recipient.Name} by {amount} cr. New balance: {newBalance}");

        if (amount > 0)
        {
            _chat.SendMessage(player, $"Gave {amount} cr to {recipient.Name}. Balance: {newBalance}");
            if (recipient != player)
                _chat.SendMessage(recipient,
                    $"Sysop {player.Name} gave you {amount} cr. Balance: {newBalance}");
        }
        else
        {
            _chat.SendMessage(player, $"Took {-amount} cr from {recipient.Name}. Balance: {newBalance}");
            if (recipient != player)
                _chat.SendMessage(recipient,
                    $"Sysop {player.Name} removed {-amount} cr. Balance: {newBalance}");
        }
    }

    // -------------------------------------------------------------------------
    // IRpg IMPLEMENTATION
    // -------------------------------------------------------------------------

    bool IRpg.TryGetStats(Player player, out int level, out long xp, out int prestigeTier)
    {
        level = 0; xp = 0; prestigeTier = 0;
        if (!player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return false;
        lock (pd.Lock)
        {
            level = pd.Level;
            xp = pd.Xp;
            prestigeTier = pd.PrestigeTier;
        }
        return true;
    }

    bool IRpg.TryPrestige(Player player, out int newTier, out string failureReason)
    {
        newTier = 0;
        failureReason = "";
        if (!player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd))
        { failureReason = "No RPG data for player."; return false; }
        Arena? arena = player.Arena;

        lock (pd.Lock)
        {
            if (pd.Level < RpgPrestigeRequiredLevel)
            {
                failureReason =
                    $"Need to reach level {RpgPrestigeRequiredLevel} first (you are level {pd.Level}).";
                return false;
            }
            pd.PrestigeTier++;
            pd.Level = 1;
            pd.Xp = 0;
            newTier = pd.PrestigeTier;
        }

        if (arena is not null)
            _chat.SendArenaMessage(arena, ChatSound.Goal,
                $"{player.Name} prestiged to *{newTier}! (+{newTier * 10}% gains forever)");
        return true;
    }

    // -------------------------------------------------------------------------
    // IEconomy IMPLEMENTATION
    // -------------------------------------------------------------------------

    long IEconomy.GetBalance(Player player)
    {
        if (!player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return 0;
        lock (pd.Lock) { return pd.Credits; }
    }

    bool IEconomy.TryEarn(Player player, long amount, string reason)
    {
        if (amount <= 0) return false;
        if (!player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return false;

        long newBalance;
        lock (pd.Lock) { pd.Credits += amount; newBalance = pd.Credits; }
        _logManager.LogP(LogLevel.Drivel, "Economy", player,
            $"+{amount} cr ({reason}), balance={newBalance}");
        return true;
    }

    bool IEconomy.TrySpend(Player player, long amount, string reason)
    {
        if (amount <= 0) return false;
        if (!player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return false;

        long newBalance;
        lock (pd.Lock)
        {
            if (pd.Credits < amount) return false;
            pd.Credits -= amount;
            newBalance = pd.Credits;
        }
        _logManager.LogP(LogLevel.Drivel, "Economy", player,
            $"-{amount} cr ({reason}), balance={newBalance}");
        return true;
    }

    // -------------------------------------------------------------------------
    // PERSIST
    // -------------------------------------------------------------------------

    private void Persist_Rpg_GetData(Player? player, Stream outStream)
    {
        if (player is null || !player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return;

        long xp; int level; long credits; int prestige;
        lock (pd.Lock)
        {
            xp = pd.Xp; level = pd.Level; credits = pd.Credits; prestige = pd.PrestigeTier;
        }

        // Skip writing default-state to save DB rows.
        if (xp == 0 && level == 1 && credits == 0 && prestige == 0) return;

        using BinaryWriter writer = new(outStream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(RpgPersistVersion);
        writer.Write(xp);
        writer.Write(level);
        writer.Write(credits);
        writer.Write(prestige);
    }

    private void Persist_Rpg_SetData(Player? player, Stream inStream)
    {
        if (player is null || !player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return;

        using BinaryReader reader = new(inStream, System.Text.Encoding.UTF8, leaveOpen: true);

        byte version = reader.ReadByte();
        if (version < 1 || version > RpgPersistVersion)
        {
            _logManager.LogP(LogLevel.Warn, LogCategory, player,
                $"Unknown persist version {version}; ignoring saved data.");
            return;
        }

        long xp = reader.ReadInt64();
        int level = reader.ReadInt32();
        long credits = 0;
        int prestige = 0;

        if (version >= 2) credits = reader.ReadInt64();
        if (version >= 3) prestige = reader.ReadInt32();

        if (level < 1) level = 1;
        if (xp < 0) xp = 0;
        if (credits < 0) credits = 0;
        if (prestige < 0) prestige = 0;

        lock (pd.Lock)
        {
            pd.Xp = xp; pd.Level = level; pd.Credits = credits; pd.PrestigeTier = prestige;
        }
    }

    private void Persist_Rpg_ClearData(Player? player)
    {
        if (player is null || !player.TryGetExtraData(_rpgPdKey, out RpgPlayerData? pd)) return;
        lock (pd.Lock)
        {
            pd.Xp = 0; pd.Level = 1; pd.Credits = 0; pd.PrestigeTier = 0;
        }
    }
}
