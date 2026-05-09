using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — State subsystem (the per-arena gate-state tracker).
// =============================================================================
//
// PURPOSE
// -------
// Zone-level sector-state tracker for the multi-arena Sector War game mode.
// Owns the per-arena `ArenaSectorState` (boss alive flag, gate unlocked flag,
// loot lockout list, lane index) and implements ISectorWar — the broker
// interface that BossEncounter, MobSpawner, ArenaLink etc. consume to gate
// player movement.
//
// SOURCE
// ------
// This is the consolidated version of the existing standalone module
// `Modules/SectorWarState.cs` (which itself was renamed from `SectorWar.cs`
// in Step 0a to free up the umbrella name). Behaviour is preserved.
//
// LINKED ARENAS
// -------------
// Loaded from global conf `[SectorWar] LinkedArenas` (comma-separated, lane
// order). Falls back to a hardcoded `sectorwarhome / sectorwarmid / sectorwarend`
// list when conf is missing — same default as the standalone module.
//
// 1-ARENA COLLAPSE NOTE
// ---------------------
// In the consolidated 1-arena world, LinkedArenas degenerates to a single-
// element list. The X-coordinate region bands (Home/Contested/AI) take over
// as the "lane" concept. EvaluateGate switches from arena-name lookup to
// X-region lookup at that point. Phase 1 final will rewrite the gate path.
//
// COMPOSITE GATE RULE
// -------------------
//   - Retreat / lateral (toLane <= fromLane): ALWAYS open.
//   - Pushing deeper requires BOTH:
//     a) fromArena's boss is dead.
//     b) fromArena is NOT enemy-controlled (uncontrolled + contested both
//        traversable; only enemy-50%+ blocks).
//
// THREADING
// ---------
// _stateLock guards the dictionary. EvaluateGate is on the per-position-
// packet hot path so the lock is held only for the snapshot read. ISectorClaim
// is cached at Load to avoid GetInterface/ReleaseInterface per-packet.
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave 11: ISectorClaim cached, lazy-resolved as a fallback if Load missed it
// (handles either load order between this subsystem and SectorClaim).
// Wave 12: ISectorWar.LinkedArenaNames property added; conf-driven list.
//
// WeekReset EVENT
// ---------------
// Declared as part of ISectorWar but currently unused — will fire when the
// weekly reset subsystem lands. Keeping the event in the interface (and on
// this class) preserves API stability for downstream consumers. The CS0067
// "never used" warning is suppressed via the no-op self-assignment in
// SuppressWeekResetUnused() below — ugly but the cleanest way to retain a
// public event that's wired to nothing yet.
// =============================================================================

public sealed partial class SectorWar : ISectorWar
{
    // Conf surface read by the SectorWarState subsystem — GLOBAL scope, not
    // per-arena. See docs/ARENA_SETTINGS.md (out-of-scope section).
    // Pinned to a field; the framework's Help scanner only walks members.
    [ConfigHelp<int>("SectorWar", "BossesEnabled", ConfigScope.Global,
        Default = 0, Min = 0, Max = 1,
        Description = "Bool-as-int. Master gate for boss encounters across the zone.")]
    [ConfigHelp("SectorWar", "LinkedArenas", ConfigScope.Global,
        Default = "",
        Description = "Comma-separated arena names tracked by SectorWarState. Empty falls back to the built-in list.")]
    private const string SectorWarStateStatusCommand = "sectorstatus";

    /// <summary>
    /// Hardcoded default sector composition. The conf path takes precedence
    /// when present. Naming constraints:
    ///   - Continuum strips '-' from arena names (sectorwar-a → sectorwara).
    ///   - SubspaceServer's Arena.BaseName trims trailing digits, so a name
    ///     like sectorwar1 would load arenas/sectorwar/arena.conf as a template
    ///     instance — bad.
    /// Workaround: word-suffix names without trailing digits or '-'.
    /// </summary>
    private static readonly (string Name, int LaneIndex)[] SectorWarStateLinkedArenas =
    {
        ("sectorwarhome", 0),
        ("sectorwarmid",  1),
        ("sectorwarend",  2),
    };

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private InterfaceRegistrationToken<ISectorWar>? _sectorWarStateToken;

    /// <summary>Cached at Load. Lazy-resolves on first gate-eval if Load
    /// missed it (handles either load order between subsystems).</summary>
    private ISectorClaim? _sectorWarStateClaim;

    /// <summary>Cached broker for the lazy-resolve fallback above.</summary>
    private IComponentBroker? _sectorWarStateBroker;

    /// <summary>Per-arena state, keyed by arena name (case-insensitive).</summary>
    private readonly Dictionary<string, ArenaSectorState> _sectorWarStateState =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guards <see cref="_sectorWarStateState"/>. Leaf lock.</summary>
    private readonly Lock _sectorWarStateStateLock = new();

    /// <summary>Snapshot of linked-arena names. Populated at Load.</summary>
    private string[] _sectorWarStateLinkedNames = Array.Empty<string>();

    public event Action<string>? BossKilled;
    public event Action<string>? GateUnlocked;

    // Public ISectorWar contract event. Currently unused (WeeklyReset
    // subsystem will fire it). Pragma silences CS0067 until then.
#pragma warning disable CS0067
    public event Action? WeekReset;
#pragma warning restore CS0067

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadSectorWarState(IComponentBroker broker)
    {
        _sectorWarStateBroker = broker;

        // BossesEnabled gate (global). 0 = bosses parked (no-op gate path);
        // 1 = enabled. Default 0 keeps the environment runnable while bosses
        // are still being tuned.
        bool bossesEnabled = _configManager.GetInt(_configManager.Global,
            ConfSection, "BossesEnabled", 0) != 0;

        var (names, fromConf) = ResolveSectorWarStateLinkedArenas();
        _sectorWarStateLinkedNames = names;

        lock (_sectorWarStateStateLock)
        {
            for (int i = 0; i < names.Length; i++)
            {
                _sectorWarStateState[names[i]] = new ArenaSectorState
                {
                    Name = names[i],
                    LaneIndex = i,
                    BossAlive = bossesEnabled,
                };
            }
        }

        ArenaActionCallback.Register(broker, OnArenaAction_SectorWarState);
        _commandManager.AddCommand(SectorWarStateStatusCommand, Command_SectorWarStateStatus);

        // Eager-cache ISectorClaim. The hot-path EvaluateGate falls back to a
        // lazy resolve if this is null, but caching here saves the per-packet
        // GetInterface dance.
        _sectorWarStateClaim = broker.GetInterface<ISectorClaim>();

        _sectorWarStateToken = broker.RegisterInterface<ISectorWar>(this);

        _logManager.LogM(LogLevel.Info, LogCategory,
            $"SectorWarState subsystem loaded — tracking {names.Length} linked arenas " +
            $"({(fromConf ? "from conf" : "from defaults")}): {string.Join(", ", names)}");
    }

    private void UnloadSectorWarState(IComponentBroker broker)
    {
        if (_sectorWarStateToken is not null)
            broker.UnregisterInterface(ref _sectorWarStateToken);

        _commandManager.RemoveCommand(SectorWarStateStatusCommand, Command_SectorWarStateStatus);
        ArenaActionCallback.Unregister(broker, OnArenaAction_SectorWarState);

        if (_sectorWarStateClaim is not null)
            broker.ReleaseInterface(ref _sectorWarStateClaim);

        lock (_sectorWarStateStateLock) { _sectorWarStateState.Clear(); }
        _sectorWarStateBroker = null;
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH (no-ops — zone-wide subsystem)
    // -------------------------------------------------------------------------

    private void AttachSectorWarState(Arena arena) { /* zone-wide */ }
    private void DetachSectorWarState(Arena arena) { /* zone-wide */ }

    // -------------------------------------------------------------------------
    // CONF READ
    // -------------------------------------------------------------------------

    /// <summary>Read the linked-arena list from global `[SectorWar] LinkedArenas`,
    /// or fall back to the hardcoded default.</summary>
    private (string[] names, bool fromConf) ResolveSectorWarStateLinkedArenas()
    {
        string? raw = _configManager.GetStr(_configManager.Global, ConfSection, "LinkedArenas");
        if (string.IsNullOrWhiteSpace(raw))
            return (SectorWarStateLinkedArenas.Select(a => a.Name).ToArray(), false);

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return (SectorWarStateLinkedArenas.Select(a => a.Name).ToArray(), false);
        return (parts, true);
    }

    // -------------------------------------------------------------------------
    // CALLBACK
    // -------------------------------------------------------------------------

    private void OnArenaAction_SectorWarState(Arena arena, ArenaAction action)
    {
        if (arena.Name is null) return;
        if (!_sectorWarStateState.ContainsKey(arena.Name)) return;

        switch (action)
        {
            case ArenaAction.Create:
                _logManager.LogA(LogLevel.Info, LogCategory, arena,
                    $"Sector arena online (lane {_sectorWarStateState[arena.Name].LaneIndex}).");
                break;
            case ArenaAction.Destroy:
                _logManager.LogA(LogLevel.Info, LogCategory, arena, "Sector arena offline.");
                break;
        }
    }

    // -------------------------------------------------------------------------
    // ISectorWar IMPLEMENTATION
    // -------------------------------------------------------------------------

    ArenaSectorState? ISectorWar.GetArenaState(string arenaName)
    {
        lock (_sectorWarStateStateLock)
        {
            return _sectorWarStateState.TryGetValue(arenaName, out var s) ? s : null;
        }
    }

    IReadOnlyList<string> ISectorWar.LinkedArenaNames => _sectorWarStateLinkedNames;

    bool ISectorWar.IsGateOpenForPlayer(Player player, string fromArena, string toArena)
        => EvaluateSectorWarStateGate(player, fromArena, toArena, out _);

    bool ISectorWar.TryGate(Player player, string fromArena, string toArena, out string? blockReason)
        => EvaluateSectorWarStateGate(player, fromArena, toArena, out blockReason);

    void ISectorWar.RegisterBossKill(string arenaName, IEnumerable<Player> killers)
    {
        ArenaSectorState? s;
        lock (_sectorWarStateStateLock)
        {
            if (!_sectorWarStateState.TryGetValue(arenaName, out s)) return;
            if (!s.BossAlive) return;  // already dead (concurrent kill credit guard)
            s.BossAlive = false;
            s.BossEntity = null;
            foreach (var p in killers)
            {
                if (p.Name is not null) s.LootLockoutPlayers.Add(p.Name);
            }
        }

        _logManager.LogM(LogLevel.Info, LogCategory,
            $"Boss killed in {arenaName} — gate unlocked.");
        BossKilled?.Invoke(arenaName);
        GateUnlocked?.Invoke(arenaName);
    }

    double ISectorWar.GetMobDifficultyMultiplier(string arenaName) => 1.0;

    // -------------------------------------------------------------------------
    // INTERNAL — gate evaluation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Composite gate rule (boss alive + claim status). Caller MUST NOT hold
    /// any other umbrella lock. Lock acquisition order: stateLock first.
    /// </summary>
    private bool EvaluateSectorWarStateGate(Player player, string fromArena, string toArena,
        out string? blockReason)
    {
        blockReason = null;

        ArenaSectorState? fromState;
        ArenaSectorState? toState;
        lock (_sectorWarStateStateLock)
        {
            _sectorWarStateState.TryGetValue(fromArena, out fromState);
            _sectorWarStateState.TryGetValue(toArena, out toState);
        }

        // Untracked arenas (e.g. dev "sectorwar") bypass the gate entirely.
        if (fromState is null || toState is null) return true;

        // Retreat / lateral — always open.
        if (toState.LaneIndex <= fromState.LaneIndex) return true;

        // Boss check.
        if (fromState.BossAlive)
        {
            blockReason = $"Defeat the boss in {fromArena} first";
            return false;
        }

        // Claim check. Lazy-resolve ISectorClaim if Load missed it.
        if (_sectorWarStateClaim is null && _sectorWarStateBroker is not null)
            _sectorWarStateClaim = _sectorWarStateBroker.GetInterface<ISectorClaim>();

        if (_sectorWarStateClaim is not null)
        {
            var snap = _sectorWarStateClaim.GetSnapshot(fromArena);
            if (snap is not null
                && snap.IsControlled
                && snap.DominantFreq is short dom
                && dom != player.Freq)
            {
                blockReason = $"Freq {dom} controls {fromArena}";
                return false;
            }
        }

        return true;
    }

    // -------------------------------------------------------------------------
    // COMMAND
    // -------------------------------------------------------------------------

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Prints current Sector War state to chat (sysop only).")]
    private void Command_SectorWarStateStatus(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        lock (_sectorWarStateStateLock)
        {
            if (_sectorWarStateState.Count == 0)
            {
                _chat.SendMessage(player, "No linked arenas configured.");
                return;
            }

            _chat.SendMessage(player, "--- Sector War status ---");
            foreach (var s in _sectorWarStateState.Values.OrderBy(s => s.LaneIndex))
            {
                string boss = s.BossAlive ? "alive" : "DEAD";
                string gate = s.GateUnlocked ? "OPEN" : "locked";
                int lockouts = s.LootLockoutPlayers.Count;
                _chat.SendMessage(player,
                    $"  Lane {s.LaneIndex}: {s.Name} — boss {boss}, gate {gate}, lockouts: {lockouts}");
            }
        }
    }
}
