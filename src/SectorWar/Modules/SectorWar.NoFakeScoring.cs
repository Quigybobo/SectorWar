using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — NoFakeScoring subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Keep fake players (HQ defenders, capital, capital cannons, warstation
// turrets, pylons, outposts) off the F2 leaderboard. Without this, every
// turret bot that kills a player accumulates KillPoints and shows up
// ranked above the player on the score screen.
//
// MECHANISM
// ---------
// Subscribe to KillCallback. After each kill, if either killer or killed
// is a fake, zero out that fake's per-arena and global Kills / Deaths /
// KillPoints stats AND reset the fake's Packet.KillPoints field that
// drives the F2 sort key. The fake still appears in the player list
// (it's a visible turret) but with score 0.
//
// We don't suppress the kill's NORMAL flow — real players still get their
// bounty from killing fakes; fakes still "die" properly so cleanup runs.
// Only the score totals are zeroed.
//
// RUNTIME OWNERSHIP
//   - Owned state: NONE.
//   - Conf keys read: NONE.
//   - Persisted data: NONE.
//   - Fakes registered: NONE.
//   - Timers: NONE.
//   - Broker interfaces published: NONE.
//
// THREADING: KillCallback fires on the mainloop.
// =============================================================================

public sealed partial class SectorWar
{
    private IComponentBroker? _noFakeScoringBroker;

    private void LoadNoFakeScoring(IComponentBroker broker)
    {
        _noFakeScoringBroker = broker;
        KillCallback.Register(broker, OnKill_NoFakeScoring);
        _logManager.LogM(LogLevel.Info, LogCategory, "NoFakeScoring subsystem loaded.");
    }

    private void UnloadNoFakeScoring(IComponentBroker broker)
    {
        KillCallback.Unregister(broker, OnKill_NoFakeScoring);
        _noFakeScoringBroker = null;
    }

    private void OnKill_NoFakeScoring(Arena arena, Player killer, Player killed,
        short bounty, short flagCount, short points, Prize green)
    {
        // Arena-attach guard: callback is zone-wide; only zero fake stats in
        // arenas where SectorWar is attached (fakes in other arenas may belong
        // to other modules and shouldn't be touched).
        arena.TryGetExtraData(_adKey, out ArenaData? ad);
        if (ad?.Arena is null) return;
        if (killer.Type == ClientType.Fake) ZeroFakeStats_NoFakeScoring(killer);
        if (killed.Type == ClientType.Fake) ZeroFakeStats_NoFakeScoring(killed);
    }

    private void ZeroFakeStats_NoFakeScoring(Player fake)
    {
        if (_noFakeScoringBroker is null) return;

        // Zero out scoring stats via the arena + global stats interfaces.
        // Reset interval covers the F2 leaderboard display; null interval
        // covers session totals.
        IArenaPlayerStats? arenaStats = _noFakeScoringBroker.GetInterface<IArenaPlayerStats>();
        if (arenaStats is not null)
        {
            try
            {
                arenaStats.SetStat(fake, StatCodes.KillPoints, PersistInterval.Reset, 0);
                arenaStats.SetStat(fake, StatCodes.Kills, PersistInterval.Reset, 0);
                arenaStats.SetStat(fake, StatCodes.Deaths, PersistInterval.Reset, 0);
                arenaStats.SetStat(fake, StatCodes.FlagPoints, PersistInterval.Reset, 0);
            }
            finally { _noFakeScoringBroker.ReleaseInterface(ref arenaStats); }
        }

        IGlobalPlayerStats? globalStats = _noFakeScoringBroker.GetInterface<IGlobalPlayerStats>();
        if (globalStats is not null)
        {
            try
            {
                globalStats.SetStat(fake, StatCodes.KillPoints, PersistInterval.Reset, 0);
                globalStats.SetStat(fake, StatCodes.Kills, PersistInterval.Reset, 0);
                globalStats.SetStat(fake, StatCodes.Deaths, PersistInterval.Reset, 0);
                globalStats.SetStat(fake, StatCodes.FlagPoints, PersistInterval.Reset, 0);
            }
            finally { _noFakeScoringBroker.ReleaseInterface(ref globalStats); }
        }

        // Reset the packet field that drives the F2 sort key. Continuum
        // sorts the leaderboard by this value, so all fakes pin to the
        // bottom of the list.
        fake.Packet.KillPoints = 0;
        fake.Packet.FlagPoints = 0;
    }
}
