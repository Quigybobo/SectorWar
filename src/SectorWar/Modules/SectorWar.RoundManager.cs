using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — RoundManager subsystem (sudden-death win condition).
// =============================================================================
//
// PURPOSE
// -------
// Sudden-death rounds: kill the enemy freq's HQ capital and you win the round.
// On round-end the arena celebrates the victor for RoundManagerResetDelay
// seconds, awards a credit reward to every winning-freq player still in-arena,
// then wipes deployables + respawns the HQs from scratch. Players warp to
// their team spawn for round 2.
//
// FLOW
//   Active : Both HQ capitals alive. Normal play.
//   Ended  : One capital just died. Broadcast + countdown ticks down.
//   On reset: despawn all pylons + war stations, respawn both HQs,
//             warp every playing player to their team spawn, return to Active.
//
// HOOK FROM Hq subsystem
//   SectorWar.Hq.cs OnBotKilled_Hq for hq_capital — calls
//   <see cref="OnHqCapitalKilled_RoundManager"/> from inside its existing
//   handler. We DON'T subscribe to BotKilled directly here to avoid
//   double-fire race risk between two zone-wide subscribers.
//
// RUNTIME OWNERSHIP
//   - Owned state: per-arena RoundManagerArenaState (in ArenaData).
//   - Conf keys read: NONE.
//   - Persisted data: NONE (rounds reset cleanly each cycle).
//   - Fakes registered: NONE.
//   - Timers: 1 Hz mainloop tick (countdown driver).
//
// THREADING: mainloop only.
// =============================================================================

public sealed partial class SectorWar
{
    private const int RoundManagerTickIntervalMs = 1000;
    private const int RoundManagerResetDelaySeconds = 30;
    private const long RoundManagerWinnerCreditReward = 10_000;

    internal enum RoundState { Active, Ended }

    internal sealed class RoundManagerArenaState
    {
        public RoundState State = RoundState.Active;
        public short WinnerFreq = -1;
        public short LoserFreq = -1;
        public int RoundEndedAtTickMs;
        public bool ResetExecuted;
        public int LastBroadcastSecond = -1;
    }

    internal sealed partial class ArenaData
    {
        public RoundManagerArenaState? RoundManagerArenaState;
    }

    private IComponentBroker? _roundManagerBroker;

    private void LoadRoundManager(IComponentBroker broker)
    {
        _roundManagerBroker = broker;
        _mainloopTimer.SetTimer(OnTick_RoundManager, RoundManagerTickIntervalMs,
            RoundManagerTickIntervalMs, this);
        _logManager.LogM(LogLevel.Info, LogCategory, "RoundManager subsystem loaded.");
    }

    private void UnloadRoundManager(IComponentBroker broker)
    {
        _mainloopTimer.ClearTimer(OnTick_RoundManager, this);
        _roundManagerBroker = null;
    }

    private void AttachRoundManager(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        ad.RoundManagerArenaState = new RoundManagerArenaState();
    }

    private void DetachRoundManager(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        ad.RoundManagerArenaState = null;
    }

    /// <summary>
    /// Called by SectorWar.Hq.cs when an hq_capital is killed. Transitions the
    /// round to Ended, broadcasts the victor, awards credits to the winning
    /// freq, and starts the reset countdown.
    /// </summary>
    private void OnHqCapitalKilled_RoundManager(Arena arena, short loserFreq)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.RoundManagerArenaState is not { } st) return;
        if (st.State != RoundState.Active) return;

        st.State = RoundState.Ended;
        st.LoserFreq = loserFreq;
        st.WinnerFreq = (short)(loserFreq == 0 ? 1 : 0);
        st.RoundEndedAtTickMs = Environment.TickCount;
        st.ResetExecuted = false;
        st.LastBroadcastSecond = -1;

        _chat.SendArenaMessage(arena, ChatSound.Beep3,
            $"*** TEAM {loserFreq} HQ CAPITAL DESTROYED ***");
        _chat.SendArenaMessage(arena, ChatSound.Ding,
            $"*** TEAM {st.WinnerFreq} WINS THE ROUND ***");
        _chat.SendArenaMessage(arena,
            $"Arena resets in {RoundManagerResetDelaySeconds}s. " +
            $"Winning team earns {RoundManagerWinnerCreditReward:N0} cr.");

        AwardWinnerCredits_RoundManager(arena, st.WinnerFreq);

        // Visually clear the loser's HQ for the 30s celebration window —
        // baseplate off, any remaining defender bots torn down, capital
        // respawn parked. Reset path will rebuild from scratch.
        try { HideHqForFreq(arena, loserFreq); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"HideHqForFreq failed: {ex.Message}");
        }

        _logManager.LogA(LogLevel.Info, LogCategory, arena,
            $"Round ended: freq {loserFreq} HQ destroyed; freq {st.WinnerFreq} wins.");
    }

    private void AwardWinnerCredits_RoundManager(Arena arena, short winnerFreq)
    {
        if (_roundManagerBroker is null) return;
        IEconomy? econ = _roundManagerBroker.GetInterface<IEconomy>();
        if (econ is null) return;
        try
        {
            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.Players)
                {
                    if (p.Arena != arena) continue;
                    if (p.Type == ClientType.Fake) continue;
                    if (p.Freq != winnerFreq) continue;
                    if (p.Status != PlayerState.Playing) continue;
                    econ.TryEarn(p, RoundManagerWinnerCreditReward, "round victory");
                }
            }
            finally { _playerData.Unlock(); }
        }
        finally { _roundManagerBroker.ReleaseInterface(ref econ); }
    }

    private bool OnTick_RoundManager()
    {
        _arenaManager.Lock();
        try
        {
            foreach (Arena arena in _arenaManager.Arenas)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) continue;
                if (ad.RoundManagerArenaState is not { State: RoundState.Ended } st) continue;
                if (st.ResetExecuted) continue;

                int elapsedSec = (Environment.TickCount - st.RoundEndedAtTickMs) / 1000;
                int remainingSec = RoundManagerResetDelaySeconds - elapsedSec;

                // 10s, 5s, 4s, 3s, 2s, 1s broadcasts.
                if (remainingSec <= 10 && remainingSec > 0
                    && remainingSec != st.LastBroadcastSecond
                    && (remainingSec <= 5 || remainingSec == 10))
                {
                    _chat.SendArenaMessage(arena, $"Resetting in {remainingSec}...");
                    st.LastBroadcastSecond = remainingSec;
                }

                if (remainingSec <= 0)
                {
                    ExecuteRoundReset_RoundManager(arena, ad, st);
                }
            }
        }
        finally { _arenaManager.Unlock(); }
        return true;
    }

    /// <summary>
    /// Wipe deployables + respawn HQs + warp players. Returns to Active state.
    /// </summary>
    private void ExecuteRoundReset_RoundManager(Arena arena, ArenaData ad,
        RoundManagerArenaState st)
    {
        st.ResetExecuted = true;

        try { WipeDeployables_RoundManager(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Round reset: deployable wipe failed: {ex.Message}");
        }

        try { RespawnHqs_RoundManager(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Round reset: HQ respawn failed: {ex.Message}");
        }

        try { WarpPlayersToSpawn_RoundManager(arena); }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"Round reset: warp failed: {ex.Message}");
        }

        _chat.SendArenaMessage(arena, ChatSound.Goal, "*** Round 2: FIGHT ***");

        // Reset state for next round.
        st.State = RoundState.Active;
        st.WinnerFreq = -1;
        st.LoserFreq = -1;
        st.LastBroadcastSecond = -1;
        st.ResetExecuted = false;

        _logManager.LogA(LogLevel.Info, LogCategory, arena, "Round reset complete.");
    }

    /// <summary>Despawn every pylon + war station in the arena. Iterates a
    /// snapshot so the underlying registries can mutate safely.</summary>
    private void WipeDeployables_RoundManager(Arena arena)
    {
        if (_roundManagerBroker is null) return;

        IPylon? pylon = _roundManagerBroker.GetInterface<IPylon>();
        if (pylon is not null)
        {
            try
            {
                var snap = pylon.GetPylons(arena).ToArray();
                foreach (var p in snap) pylon.Despawn(arena, p);
            }
            finally { _roundManagerBroker.ReleaseInterface(ref pylon); }
        }

        IStationDeployer? sd = _roundManagerBroker.GetInterface<IStationDeployer>();
        if (sd is not null)
        {
            try
            {
                var snap = sd.GetStructures(arena).ToArray();
                foreach (var s in snap) sd.Despawn(arena, s);
            }
            finally { _roundManagerBroker.ReleaseInterface(ref sd); }
        }
    }

    /// <summary>Tear down both HQs and immediately spawn fresh ones — the
    /// simplest path to "full reset". Reuses the existing Hq subsystem
    /// despawn/spawn helpers. Also unfreezes any per-freq respawn locks
    /// that were set when the loser's capital died — the round-reset must
    /// leave both freqs unfrozen so deployables work normally next round.</summary>
    private void RespawnHqs_RoundManager(Arena arena)
    {
        DespawnHqArena(arena);

        if (_roundManagerBroker is not null)
        {
            IStaticTurret? st = _roundManagerBroker.GetInterface<IStaticTurret>();
            if (st is not null)
            {
                try
                {
                    st.FreezeRespawn(arena, 0, false);
                    st.FreezeRespawn(arena, 1, false);
                }
                finally { _roundManagerBroker.ReleaseInterface(ref st); }
            }
        }

        TrySpawnHqArena(arena);
    }

    /// <summary>Give every playing real player a Warp prize so they bounce to
    /// their team's spawn coords. Subspace's spawn picker reads
    /// [Spawn] Team{N}-X/Y, so freq 0 lands left-base, freq 1 lands
    /// right-base, just like the round-1 arena entry.</summary>
    private void WarpPlayersToSpawn_RoundManager(Arena arena)
    {
        _playerData.Lock();
        try
        {
            foreach (Player p in _playerData.Players)
            {
                if (p.Arena != arena) continue;
                if (p.Type == ClientType.Fake) continue;
                if (p.Status != PlayerState.Playing) continue;
                if (p.Ship == ShipType.Spec) continue;
                _game.GivePrize(p, Prize.Warp, 1);
            }
        }
        finally { _playerData.Unlock(); }
    }
}
