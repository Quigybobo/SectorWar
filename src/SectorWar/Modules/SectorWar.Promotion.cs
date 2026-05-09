using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — Promotion subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Kill-streak crown reward. When a player gets N kills without dying, they
// receive the KOTH crown (visual king-of-the-hill indicator) plus a bag of
// configured prizes. Crown drops on death, ship change, or freq change.
//
// NOT a level/prestige system — that's the Rpg subsystem. This is a "killing
// spree" reward complementing the level/XP/credits stack.
//
// SOURCE
// ------
// Port of JoWie's promotion.c (ASSS): bitbucket.org/jowie/asss-promotion.
// Standalone module `Modules/Promotion.cs` stays as a library copy.
//
// CONF MIGRATION
// --------------
// Original keys lived in `[Promotion]`; under the consolidated umbrella they
// move to `[SectorWar]` with the `Promotion` prefix:
//
//   was [Promotion] KillsForPromotion = 5
//   becomes [SectorWar] PromotionKillsForPromotion = 5
//
//   was [Promotion] Prizes = 1 7 9
//   becomes [SectorWar] PromotionPrizes = 1 7 9
//
// Same values, same parser (digits, '+', '-' kept; everything else is a
// separator). Documented in `docs/SECTORWAR_CONF.md`.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: ArenaData.PromotionKillsForPromotion +
//                  ArenaData.PromotionPrizes (per-arena);
//                  PromotionPlayerData.KillsWithoutDeath + .HasCrown
//                  (per-player).
//   - Conf keys read: [SectorWar] PromotionKillsForPromotion, PromotionPrizes
//                     (per-arena, refreshed on ConfChanged).
//   - Persisted data: NONE (streaks reset on disconnect by design).
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE.
//   - Commands registered: NONE.
//
// CALLBACKS HOOKED (per-arena via Attach/Detach)
//   - ArenaActionCallback     → OnArenaAction_Promotion (ConfChanged refresh)
//   - ShipFreqChangeCallback  → OnShipFreqChange_Promotion (clear streak/crown)
//   - KillCallback            → OnKill_Promotion (increment + award)
//   - PlayerActionCallback    → OnPlayerAction_Promotion (clear on EnterArena)
//
// THREADING
// ---------
// All callbacks fire on the mainloop. <see cref="ICrowns.ToggleOn"/>/Off and
// <see cref="IGame.GivePrize"/> are mainloop-safe.
//
// FAKE-PLAYER GUARD
// -----------------
// Kill credit is skipped when the killer is `ClientType.Fake` — turret kills,
// boss kills, etc. shouldn't count toward the player's spree streak. This
// matches the original ASSS behaviour and prevents a fake-driven bot from
// "earning" crowns it has no use for.
//
// WAVE-FIXES PRESERVED
// --------------------
// None Wave-specific.
// =============================================================================

public sealed partial class SectorWar
{
    /// <summary>Hard cap on prize-list length. Matches the original module.
    /// Prevents a runaway conf entry from allocating gigabytes.</summary>
    // Conf surface owned by the Promotion subsystem — see docs/ARENA_SETTINGS.md.
    // Pinned to a field; the framework's Help scanner only walks members.
    [ConfigHelp<int>("SectorWar", "PromotionKillsForPromotion", ConfigScope.Arena,
        Default = 5, Min = 1, Max = 999,
        Description = "Streak length needed to earn the kill-streak crown.")]
    [ConfigHelp("SectorWar", "PromotionPrizes", ConfigScope.Arena,
        Default = "",
        Description = "Space-separated Prize enum ints awarded on each promotion. Empty = no prizes.")]
    private const int PromotionMaxPrizes = 128;

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    /// <summary>Per-player tracker for kill streak + crown ownership.</summary>
    private PlayerDataKey<PromotionPlayerData> _promotionPdKey;

    // ArenaData extension: per-arena promotion settings.
    internal sealed partial class ArenaData
    {
        /// <summary>Streak length needed to earn a crown. Default 5 mirrors
        /// the original module's default. Refreshed on ConfChanged.</summary>
        public int PromotionKillsForPromotion = 5;

        /// <summary>Bag of prizes given on promotion. Empty list = crown only,
        /// no extra prize awards.</summary>
        public List<Prize> PromotionPrizes = new();
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    /// <summary>Allocate the per-player tracker slot.</summary>
    private void LoadPromotion(IComponentBroker broker)
    {
        _promotionPdKey = _playerData.AllocatePlayerData<PromotionPlayerData>();
        _logManager.LogM(LogLevel.Info, LogCategory, "Promotion subsystem loaded.");
    }

    /// <summary>Free the per-player tracker slot.</summary>
    private void UnloadPromotion(IComponentBroker broker)
    {
        _playerData.FreePlayerData(ref _promotionPdKey);
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    // -------------------------------------------------------------------------

    private void AttachPromotion(Arena arena)
    {
        ReadPromotionSettings(arena);

        ArenaActionCallback.Register(arena, OnArenaAction_Promotion);
        ShipFreqChangeCallback.Register(arena, OnShipFreqChange_Promotion);
        KillCallback.Register(arena, OnKill_Promotion);
        PlayerActionCallback.Register(arena, OnPlayerAction_Promotion);
    }

    private void DetachPromotion(Arena arena)
    {
        ArenaActionCallback.Unregister(arena, OnArenaAction_Promotion);
        ShipFreqChangeCallback.Unregister(arena, OnShipFreqChange_Promotion);
        KillCallback.Unregister(arena, OnKill_Promotion);
        PlayerActionCallback.Unregister(arena, OnPlayerAction_Promotion);
    }

    // -------------------------------------------------------------------------
    // CONF READ
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads `[SectorWar] PromotionKillsForPromotion` and `PromotionPrizes` into
    /// the per-arena ArenaData. Idempotent — called from both AttachPromotion
    /// and the ConfChanged refresh.
    /// </summary>
    private void ReadPromotionSettings(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        ConfigHandle? cfg = arena.Cfg;
        if (cfg is null) return;

        ad.PromotionKillsForPromotion =
            _configManager.GetInt(cfg, ConfSection, "PromotionKillsForPromotion", 5);

        ad.PromotionPrizes.Clear();
        string? prizeStr = _configManager.GetStr(cfg, ConfSection, "PromotionPrizes");
        if (string.IsNullOrWhiteSpace(prizeStr)) return;

        // Original parser is conservative: keeps digits + sign chars and uses
        // anything else as a separator. We mirror that here so the same conf
        // string parses identically across the standalone and umbrella builds.
        var current = new System.Text.StringBuilder();
        foreach (char c in prizeStr)
        {
            if (char.IsDigit(c) || c == '-' || c == '+')
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                if (short.TryParse(current.ToString(), out short val))
                    ad.PromotionPrizes.Add((Prize)val);
                current.Clear();
                if (ad.PromotionPrizes.Count >= PromotionMaxPrizes) break;
            }
        }
        // Trailing token (no terminator after the last number).
        if (current.Length > 0 && ad.PromotionPrizes.Count < PromotionMaxPrizes)
        {
            if (short.TryParse(current.ToString(), out short val))
                ad.PromotionPrizes.Add((Prize)val);
        }
    }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    /// <summary>Re-read settings on `?reloadconf` / config-file edits.</summary>
    private void OnArenaAction_Promotion(Arena arena, ArenaAction action)
    {
        if (action == ArenaAction.ConfChanged)
            ReadPromotionSettings(arena);
    }

    /// <summary>
    /// Kill-streak processor. Killed player loses streak and crown; killer
    /// gains a kill toward their streak and is awarded the crown when they
    /// reach the threshold. Fake-player killers (turrets, bosses) are
    /// excluded so AI kills don't count.
    /// </summary>
    private void OnKill_Promotion(Arena arena, Player killer, Player killed,
        short bounty, short flagCount, short points, Prize green)
    {
        // Killed: reset streak, drop crown if held.
        if (killed.TryGetExtraData(_promotionPdKey, out PromotionPlayerData? killedData))
        {
            killedData.KillsWithoutDeath = 0;
            if (killedData.HasCrown)
            {
                killedData.HasCrown = false;
                _crowns.ToggleOff(killed);
            }
        }

        // Killer: increment streak (real-player only).
        if (killer is null || killer.Type == ClientType.Fake) return;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (!killer.TryGetExtraData(_promotionPdKey, out PromotionPlayerData? killerData)) return;

        killerData.KillsWithoutDeath++;

        if (!killerData.HasCrown && killerData.KillsWithoutDeath >= ad.PromotionKillsForPromotion)
        {
            killerData.HasCrown = true;

            // TimeSpan.Zero matches ASSS `time=0` semantics: crown stays until
            // explicitly toggled off (death/ship/freq change).
            _crowns.ToggleOn(killer, TimeSpan.Zero);

            foreach (Prize p in ad.PromotionPrizes)
                _game.GivePrize(killer, p, 1);

            _logManager.LogP(LogLevel.Info, LogCategory, killer,
                $"Promoted to crown after {killerData.KillsWithoutDeath} kills without death.");
        }
    }

    /// <summary>Ship/freq change clears streak + crown (matches original).</summary>
    private void OnShipFreqChange_Promotion(
        Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
    {
        if (!player.TryGetExtraData(_promotionPdKey, out PromotionPlayerData? pd)) return;
        pd.KillsWithoutDeath = 0;
        if (pd.HasCrown)
        {
            pd.HasCrown = false;
            _crowns.ToggleOff(player);
        }
    }

    /// <summary>Reset streak + crown flag on EnterArena. The crown ToggleOff
    /// isn't needed here because EnterArena fires before any crown could
    /// have been visually applied; we just need to know the slot is clean.</summary>
    private void OnPlayerAction_Promotion(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.EnterArena)
        {
            if (player.TryGetExtraData(_promotionPdKey, out PromotionPlayerData? pd))
            {
                pd.KillsWithoutDeath = 0;
                pd.HasCrown = false;
            }
        }
    }

    // -------------------------------------------------------------------------
    // PER-PLAYER DATA
    // -------------------------------------------------------------------------

    private sealed class PromotionPlayerData : IResettable
    {
        /// <summary>Current kill streak length for this connection.</summary>
        public int KillsWithoutDeath;

        /// <summary>Whether the crown is currently shown for this player.
        /// Mirrors what was sent to <see cref="ICrowns"/> so we never
        /// double-toggle.</summary>
        public bool HasCrown;

        bool IResettable.TryReset()
        {
            KillsWithoutDeath = 0;
            HasCrown = false;
            return true;
        }
    }
}
