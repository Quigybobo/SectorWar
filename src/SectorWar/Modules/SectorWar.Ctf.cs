using SS.SectorWar.Callbacks;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — CtfGame subsystem.
// =============================================================================
//
// PURPOSE
// -------
// N-team Capture-the-Flag flag-game behavior. Each team has a flag and home
// region. Carry an enemy flag to your home (with your own flag at home) to
// score; reach WinCaptures captures to win. Per-team configurable spawn
// coords, region, and name in [CTF].
//
// SOURCE
// ------
// C# rewrite (NOT 1:1 port) of JoWie's asss-ctf (Python). Standalone module
// `Modules/CtfGame.cs` stays as a library copy.
//
// CONF SECTION
// ------------
// Stays in `[CTF]` (not `[SectorWar]`) — flag-game configuration is its own
// well-known SS.NET surface. Documented exception in
// `docs/SECTORWAR_CONF.md`.
//
// CUSTOM CALLBACKS
//   CtfScoreCallback(arena, scoredBy, freq, enemyFreq, score)
//   CtfSaveCallback(arena, player, freq)
//   CtfWinCallback(arena, freq, points)  — points is List<int>; subscribers add
//
// RUNTIME OWNERSHIP
//   - Owned state: ArenaData with TeamCount, WinCaptures, NeutAfterKill,
//                  TeamConfig[] homes, int[] scores, ICarryFlagBehavior
//                  registration token.
//   - Conf keys read: [CTF] Teams, WinCaptures, NeutAfterKill,
//                     Team{N}-X / -Y / -Region / -Name.
//   - Persisted data: NONE (per-game state is session-only).
//   - Fakes registered: NONE.
//   - Timers scheduled: 1s scoring tick (per-arena, IMainloopTimer).
//   - Commands registered: NONE.
//
// CALLBACKS HOOKED (per-arena)
//   - ArenaActionCallback     → ConfChanged refresh
//   - ShipFreqChangeCallback  → drop carried flags on team-hop
//   - ICarryFlagBehavior      → registered as arena interface for the core
//                               CarryFlags module to dispatch through.
//
// SAFEZONE-AS-WARP-PROXY
// ----------------------
// asss-ctf's original uses STATUS_FLASH to skip warping players in the
// scoring loop. SS.NET's PlayerPositionStatus doesn't expose FLASH directly;
// we use Safezone as a conservative proxy (close enough — flags shouldn't
// score from inside safe zones either).
// =============================================================================

public sealed partial class SectorWar
{
    private const int CtfMaxTeams = 9;
    private const int CtfTickIntervalMs = 1000;

    // -------------------------------------------------------------------------
    // ArenaData extension
    // -------------------------------------------------------------------------

    internal sealed class CtfTeamConfig
    {
        public short X = 502;
        public short Y = 512;
        public MapRegion? Region;
        public string Name = "";
    }

    internal sealed partial class ArenaData
    {
        public InterfaceRegistrationToken<ICarryFlagBehavior>? CtfBehaviorToken;
        public CtfBehavior? CtfBehaviorInstance;
        public short CtfTeamCount = 2;
        public int CtfWinCaptures = 3;
        public bool CtfNeutAfterKill;
        internal CtfTeamConfig[] CtfTeams = Array.Empty<CtfTeamConfig>();
        public int[] CtfScores = Array.Empty<int>();
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadCtf(IComponentBroker broker)
    {
        _logManager.LogM(LogLevel.Info, LogCategory, "CtfGame subsystem loaded.");
    }

    private void UnloadCtf(IComponentBroker broker) { /* per-arena state cleared by IResettable */ }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    // -------------------------------------------------------------------------

    private void AttachCtf(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        ReadCtfSettings(arena, ad);

        // Register our ICarryFlagBehavior on the arena interface table. The
        // core CarryFlags module picks it up on its next config refresh.
        ad.CtfBehaviorInstance = new CtfBehavior(this, arena);
        ad.CtfBehaviorToken = arena.RegisterInterface<ICarryFlagBehavior>(ad.CtfBehaviorInstance);

        ArenaActionCallback.Register(arena, OnArenaAction_Ctf);
        ShipFreqChangeCallback.Register(arena, OnShipFreqChange_Ctf);

        _mainloopTimer.SetTimer(OnTick_Ctf, CtfTickIntervalMs, CtfTickIntervalMs, arena, arena);
    }

    private void DetachCtf(Arena arena)
    {
        _mainloopTimer.ClearTimer<Arena>(OnTick_Ctf, arena);
        ArenaActionCallback.Unregister(arena, OnArenaAction_Ctf);
        ShipFreqChangeCallback.Unregister(arena, OnShipFreqChange_Ctf);

        if (arena.TryGetExtraData(_adKey, out ArenaData? ad))
        {
            if (ad.CtfBehaviorToken is not null)
                arena.UnregisterInterface(ref ad.CtfBehaviorToken);
            ad.CtfBehaviorInstance = null;
        }
    }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    private void OnArenaAction_Ctf(Arena arena, ArenaAction action)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (action == ArenaAction.ConfChanged) ReadCtfSettings(arena, ad);
    }

    /// <summary>Drop carried flags home on ship/freq change so an enemy can't
    /// hop teams to "save" their stolen flag.</summary>
    private void OnShipFreqChange_Ctf(Player p, ShipType newShip, ShipType oldShip,
        short newFreq, short oldFreq)
    {
        if (p.Arena is null) return;
        if (!p.Arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (oldFreq < 0 || oldFreq >= ad.CtfTeamCount) return;
        ResetCtfCarriedFlags(p.Arena, ad, p);
    }

    // -------------------------------------------------------------------------
    // SETTINGS
    // -------------------------------------------------------------------------

    private void ReadCtfSettings(Arena arena, ArenaData ad)
    {
        ConfigHandle? cfg = arena.Cfg;
        if (cfg is null) return;

        ad.CtfTeamCount = (short)Math.Clamp(_configManager.GetInt(cfg, "CTF", "Teams", 2),
            1, CtfMaxTeams);
        ad.CtfWinCaptures = Math.Max(1, _configManager.GetInt(cfg, "CTF", "WinCaptures", 3));
        ad.CtfNeutAfterKill = _configManager.GetInt(cfg, "CTF", "NeutAfterKill", 0) != 0;

        if (ad.CtfTeams.Length != ad.CtfTeamCount) ad.CtfTeams = new CtfTeamConfig[ad.CtfTeamCount];
        if (ad.CtfScores.Length != ad.CtfTeamCount) ad.CtfScores = new int[ad.CtfTeamCount];

        for (short i = 0; i < ad.CtfTeamCount; i++)
        {
            var t = ad.CtfTeams[i] ??= new CtfTeamConfig();
            t.X = (short)_configManager.GetInt(cfg, "CTF", $"Team{i}-X", 502);
            t.Y = (short)_configManager.GetInt(cfg, "CTF", $"Team{i}-Y", 512);
            string? regionName = _configManager.GetStr(cfg, "CTF", $"Team{i}-Region");
            t.Region = string.IsNullOrEmpty(regionName) ? null
                : _mapData.FindRegionByName(arena, regionName);
            t.Name = _configManager.GetStr(cfg, "CTF", $"Team{i}-Name") ?? $"Team {i}";
        }
    }

    // -------------------------------------------------------------------------
    // SCORING TICK
    // -------------------------------------------------------------------------

    private bool OnTick_Ctf(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.CtfTeamCount <= 0)
            return true;

        var carryGame = arena.GetInterface<ICarryFlagGame>();
        if (carryGame is null) return true;

        try
        {
            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.Players)
                {
                    if (p.Arena != arena) continue;

                    short freq = p.Freq;
                    if (freq < 0 || freq >= ad.CtfTeamCount) continue;

                    int carried = carryGame.GetFlagCount(p);
                    if (carried <= 0) continue;

                    ref readonly var pos = ref p.Position;
                    short tileX = (short)(pos.X / 16);
                    short tileY = (short)(pos.Y / 16);

                    // Safezone-as-warp-proxy.
                    if ((pos.Status & PlayerPositionStatus.Safezone) != 0) continue;

                    for (short enemyFreq = 0; enemyFreq < ad.CtfTeamCount; enemyFreq++)
                    {
                        if (enemyFreq == freq) continue;

                        if (IsCtfPositionAtHome(ad, freq, tileX, tileY) &&
                            IsCtfTeamFlagAtHome(arena, ad, carryGame, freq))
                        {
                            CtfTeamScore(arena, ad, carryGame, p, freq, enemyFreq);
                            break; // one score per tick per player
                        }
                    }
                }
            }
            finally { _playerData.Unlock(); }
        }
        finally { arena.ReleaseInterface(ref carryGame); }

        return true;
    }

    private static bool IsCtfPositionAtHome(ArenaData ad, short freq, short tileX, short tileY)
    {
        if (freq < 0 || freq >= ad.CtfTeamCount) return false;
        if (tileX < 0 || tileY < 0) return false;
        var team = ad.CtfTeams[freq];
        return team.Region is not null && team.Region.ContainsCoordinate(tileX, tileY);
    }

    private static bool IsCtfTeamFlagAtHome(Arena arena, ArenaData ad,
        ICarryFlagGame carryGame, short freq)
    {
        if (!carryGame.TryGetFlagInfo(arena, freq, out IFlagInfo? info)) return false;
        if (info.State != FlagState.OnMap || info.Location is null) return false;
        var loc = info.Location.Value;
        return IsCtfPositionAtHome(ad, freq, loc.X, loc.Y);
    }

    private void CtfTeamScore(Arena arena, ArenaData ad, ICarryFlagGame carryGame,
        Player scoredBy, short freq, short enemyFreq)
    {
        var enemyTeam = ad.CtfTeams[enemyFreq];
        carryGame.TrySetFlagOnMap(arena, enemyFreq,
            new TileCoordinates(enemyTeam.X, enemyTeam.Y), enemyFreq);

        ad.CtfScores[freq]++;

        CtfScoreCallback.Fire(arena, arena, scoredBy, freq, enemyFreq, ad.CtfScores[freq]);

        if (ad.CtfScores[freq] >= ad.CtfWinCaptures)
        {
            _chat.SendArenaMessage(arena, ChatSound.Ding,
                $"{scoredBy.Name} captured the flag for {ad.CtfTeams[freq].Name}!");
            CtfTeamWin(arena, ad, carryGame, freq, scoredBy);
        }
        else
        {
            int needed = ad.CtfWinCaptures - ad.CtfScores[freq];
            string s = needed == 1 ? "" : "s";
            _chat.SendArenaMessage(arena, ChatSound.Beep3,
                $"{scoredBy.Name} captured the flag for {ad.CtfTeams[freq].Name}! " +
                $"Needs {needed} more capture{s} to win.");
        }
    }

    private void CtfTeamWin(Arena arena, ArenaData ad, ICarryFlagGame carryGame,
        short freq, Player scoredBy)
    {
        var scoreList = new System.Text.StringBuilder();
        for (int i = 0; i < ad.CtfScores.Length; i++)
        {
            if (i > 0) scoreList.Append(" - ");
            scoreList.Append(ad.CtfScores[i]);
        }
        _chat.SendArenaMessage(arena, ChatSound.Ding,
            $"NOTICE: Game over. {ad.CtfTeams[freq].Name} wins! Final score: {scoreList}");

        var pts = new List<int>();
        CtfWinCallback.Fire(arena, arena, freq, pts);
        int totalPoints = 0;
        foreach (int p in pts) totalPoints += p;
        if (totalPoints == 0) totalPoints = 12345;  // matches asss-ctf default

        Array.Clear(ad.CtfScores);
        carryGame.ResetGame(arena, freq, totalPoints, true);
        ResetAllCtfFlags(arena, ad, carryGame);
    }

    // -------------------------------------------------------------------------
    // FLAG-BEHAVIOR HOOKS (called from CtfBehavior inner class)
    // -------------------------------------------------------------------------

    private void OnCtfInit(Arena arena, ICarryFlagGame carryGame)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        for (short i = 0; i < ad.CtfTeamCount; i++)
        {
            if (carryGame.TryAddFlag(arena, out short flagId))
            {
                var team = ad.CtfTeams[i];
                carryGame.TrySetFlagOnMap(arena, flagId,
                    new TileCoordinates(team.X, team.Y), i);
            }
        }
    }

    private void OnCtfSpawnFlags(Arena arena, ICarryFlagGame carryGame)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        ResetAllCtfFlags(arena, ad, carryGame);
    }

    private void OnCtfTouchFlag(Arena arena, Player p, short flagId, ICarryFlagGame carryGame)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        short flagFreq = flagId;

        if (flagFreq == p.Freq)
        {
            // Friendly pickup → return home + fire save callback.
            var team = ad.CtfTeams[flagFreq];
            carryGame.TrySetFlagOnMap(arena, flagId,
                new TileCoordinates(team.X, team.Y), flagFreq);
            CtfSaveCallback.Fire(arena, arena, p, flagFreq);
        }
        else
        {
            // Enemy pickup → assign carrier + announce.
            carryGame.TrySetFlagCarried(arena, flagId, p, FlagPickupReason.Pickup);
            string teamName = (flagFreq >= 0 && flagFreq < ad.CtfTeamCount)
                ? ad.CtfTeams[flagFreq].Name
                : $"Freq {flagFreq}";
            _chat.SendArenaMessage(arena, ChatSound.Beep3,
                $"The {teamName} flag was stolen by {p.Name}!");
        }
    }

    private void OnCtfPlayerKill(Arena arena, Player killed, Player killer, ReadOnlySpan<short> flagIds)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        var carryGame = arena.GetInterface<ICarryFlagGame>();
        if (carryGame is null) return;
        try
        {
            foreach (short flagId in flagIds)
            {
                short flagFreq = flagId;
                if (flagFreq < 0 || flagFreq >= ad.CtfTeamCount) continue;

                if (ad.CtfNeutAfterKill)
                {
                    ref readonly var pos = ref killed.Position;
                    short tx = (short)Math.Clamp(pos.X / 16, 0, 1023);
                    short ty = (short)Math.Clamp(pos.Y / 16, 0, 1023);
                    carryGame.TrySetFlagNeuted(arena, flagId, new TileCoordinates(tx, ty), -1);
                }
                else
                {
                    var team = ad.CtfTeams[flagFreq];
                    carryGame.TrySetFlagOnMap(arena, flagId,
                        new TileCoordinates(team.X, team.Y), flagFreq);
                }
            }
        }
        finally { arena.ReleaseInterface(ref carryGame); }
    }

    private void OnCtfAdjustFlags(Arena arena, ReadOnlySpan<short> flagIds, AdjustFlagReason reason,
        Player? oldCarrier, short oldFreq)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        var carryGame = arena.GetInterface<ICarryFlagGame>();
        if (carryGame is null) return;
        try
        {
            foreach (short flagId in flagIds)
            {
                short flagFreq = flagId;
                if (flagFreq < 0 || flagFreq >= ad.CtfTeamCount) continue;
                var team = ad.CtfTeams[flagFreq];
                carryGame.TrySetFlagOnMap(arena, flagId,
                    new TileCoordinates(team.X, team.Y), flagFreq);
            }
        }
        finally { arena.ReleaseInterface(ref carryGame); }
    }

    private void ResetAllCtfFlags(Arena arena, ArenaData ad, ICarryFlagGame carryGame)
    {
        for (short i = 0; i < ad.CtfTeamCount; i++)
        {
            var team = ad.CtfTeams[i];
            carryGame.TrySetFlagOnMap(arena, i, new TileCoordinates(team.X, team.Y), i);
        }
    }

    private void ResetCtfCarriedFlags(Arena arena, ArenaData ad, Player p)
    {
        var carryGame = arena.GetInterface<ICarryFlagGame>();
        if (carryGame is null) return;
        try
        {
            short flagCount = carryGame.GetFlagCount(arena);
            for (short flagId = 0; flagId < flagCount; flagId++)
            {
                if (!carryGame.TryGetFlagInfo(arena, flagId, out IFlagInfo? info)) continue;
                if (info.State == FlagState.Carried && info.Carrier == p)
                {
                    short flagFreq = flagId;
                    if (flagFreq < 0 || flagFreq >= ad.CtfTeamCount) continue;
                    var team = ad.CtfTeams[flagFreq];
                    carryGame.TrySetFlagOnMap(arena, flagId,
                        new TileCoordinates(team.X, team.Y), flagFreq);
                }
            }
        }
        finally { arena.ReleaseInterface(ref carryGame); }
    }

    // -------------------------------------------------------------------------
    // INNER ICarryFlagBehavior — registered per-arena, delegates back to umbrella.
    // -------------------------------------------------------------------------

    internal sealed class CtfBehavior : ICarryFlagBehavior
    {
        private readonly SectorWar _owner;
        private readonly Arena _arena;

        public CtfBehavior(SectorWar owner, Arena arena)
        {
            _owner = owner;
            _arena = arena;
        }

        void ICarryFlagBehavior.StartGame(Arena arena)
        {
            var carry = arena.GetInterface<ICarryFlagGame>();
            if (carry is null) return;
            try { _owner.OnCtfInit(arena, carry); }
            finally { arena.ReleaseInterface(ref carry); }
        }

        void ICarryFlagBehavior.SpawnFlags(Arena arena)
        {
            var carry = arena.GetInterface<ICarryFlagGame>();
            if (carry is null) return;
            try { _owner.OnCtfSpawnFlags(arena, carry); }
            finally { arena.ReleaseInterface(ref carry); }
        }

        short ICarryFlagBehavior.GetPlayerKillTransferCount(Arena arena, Player killed,
            Player killer, ReadOnlySpan<short> flagIds) => 0;

        short ICarryFlagBehavior.PlayerKill(Arena arena, Player killed, Player killer,
            ReadOnlySpan<short> flagIds)
        {
            _owner.OnCtfPlayerKill(arena, killed, killer, flagIds);
            return 0;
        }

        void ICarryFlagBehavior.TouchFlag(Arena arena, Player player, short flagId)
        {
            var carry = arena.GetInterface<ICarryFlagGame>();
            if (carry is null) return;
            try { _owner.OnCtfTouchFlag(arena, player, flagId, carry); }
            finally { arena.ReleaseInterface(ref carry); }
        }

        void ICarryFlagBehavior.AdjustFlags(Arena arena, ReadOnlySpan<short> flagIds,
            AdjustFlagReason reason, Player oldCarrier, short oldFreq)
        {
            _owner.OnCtfAdjustFlags(arena, flagIds, reason, oldCarrier, oldFreq);
        }
    }
}
