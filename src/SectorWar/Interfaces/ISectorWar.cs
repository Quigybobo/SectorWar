using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

/// <summary>
/// Per-arena sector state tracked by the SectorWar zone-level module.
/// One entry per linked arena (sectorwar-a / sectorwar-b / sectorwar-c).
/// </summary>
public sealed class ArenaSectorState
{
    public required string Name { get; init; }

    /// 0 = home (player spawn), 1 = middle (contested), 2 = AI fortress (boss).
    public int LaneIndex { get; init; }

    /// Resets weekly. While true, the gate to deeper arenas stays locked.
    public bool BossAlive { get; set; } = true;

    /// Active boss fake-player entity. Null when dead, repopulated by WeeklyReset.
    public Player? BossEntity { get; set; }

    /// Players who have killed this arena's boss this week (loot lockout).
    /// Cleared by WeeklyReset. Used to decide if a given player can re-engage
    /// a boss that's already alive.
    public HashSet<string> LootLockoutPlayers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// Computed: gate is unlocked iff boss is dead.
    public bool GateUnlocked => !BossAlive;
}

/// <summary>
/// Zone-level sector-war state. Owns BossAlive flags, loot lockouts, and the
/// dynamic difficulty multiplier per arena.
///
/// Phase 1 (skeleton): only tracks state; downstream modules (BossEncounter,
/// MobSpawner, ArenaLink, WeeklyReset) plug in via this interface in later phases.
/// </summary>
public interface ISectorWar : IComponentInterface
{
    /// Returns the tracked state for `arenaName`, or null if the arena is not
    /// part of the linked sector.
    ArenaSectorState? GetArenaState(string arenaName);

    /// <summary>
    /// Ordered list of linked sector arena names (lane 0..N). Single source
    /// of truth so SectorClaimVisual / LinkedChat / SectorRoster /
    /// WarStationMinions don't each hardcode the same list.
    /// </summary>
    IReadOnlyList<string> LinkedArenaNames { get; }

    /// Returns true if the warp gate from `fromArena` to `toArena` is currently
    /// open for `player`. Phase 1: always returns true (no gating yet).
    bool IsGateOpenForPlayer(Player player, string fromArena, string toArena);

    /// Same as <see cref="IsGateOpenForPlayer"/> but on a closed gate also
    /// emits a short reason (e.g. "Boss alive" / "Enemy claim") suitable for
    /// chat feedback. <paramref name="blockReason"/> is null when open.
    bool TryGate(Player player, string fromArena, string toArena, out string? blockReason);

    /// Called by BossEncounter when an arena's boss dies. Updates state, fires
    /// BossKilled + GateUnlocked events. `killers` are the players credited
    /// with the kill (for loot lockout tracking).
    void RegisterBossKill(string arenaName, IEnumerable<Player> killers);

    /// Returns the current mob HP/damage multiplier for `arenaName`, scaled to
    /// the active player count. Phase 1: returns 1.0.
    double GetMobDifficultyMultiplier(string arenaName);

    /// Fires when an arena's boss dies (gate just unlocked).
    event Action<string>? BossKilled;

    /// Fires when a gate transitions from locked to open. Same event as
    /// BossKilled in Phase 1; kept separate so PvP-mode gating can fire
    /// it for non-boss reasons.
    event Action<string>? GateUnlocked;

    /// Fires on weekly reset (bosses respawn, lockouts clear).
    event Action? WeekReset;
}
