using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — SectorClaim subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Per-arena per-freq pylon-count tracker. Computes dominant freq + controlled/
// contested/unclaimed status snapshots on demand, and fires
// <see cref="ISectorClaim.ArenaOwnerChanged"/> when the dominant freq for an
// arena flips. Used by SectorClaimVisual (mini-map indicator) and by the
// SectorWarState gate-evaluation path.
//
// SOURCE
// ------
// Standalone module `Modules/SectorClaim.cs` stays as a library copy.
//
// DOMINANCE RULES
// ---------------
//   - "controlled by freq X" — X holds 50%+ of total claim weight in the arena
//   - "contested"             — claims exist but no freq holds 50%+ (or there's a tie)
//   - "unclaimed"             — no pylons standing
//
// Ties at the top of the leaderboard count as no-clear-dominant (and thus
// "contested" if there's any claim weight at all). This matches the original
// behaviour.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: per-arena claim maps + last-known-dominant cache
//                  (zone-wide dictionaries protected by a Lock; not per-arena
//                  ArenaData because lookups happen by arena NAME, not Arena
//                  reference, and consumers like SectorClaimVisual iterate
//                  across arenas).
//   - Conf keys read: NONE.
//   - Persisted data: NONE (rebuilt from IPylon events on restart).
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE.
//   - Commands registered: cmd_claim (default — anyone), cmd_claimall.
//   - Broker interfaces published: ISectorClaim with ArenaOwnerChanged event.
//
// CALLBACKS HOOKED (zone-wide)
//   - IPylon.PylonDeployed event   → OnPylonDeployed_SectorClaim
//   - IPylon.PylonDespawned event  → OnPylonDespawned_SectorClaim
//
// THREADING
// ---------
// Pylon events fire on the mainloop. The internal claim map is guarded by
// <see cref="_sectorClaimLock"/> as a defensive measure (events may grow to
// fire from a worker thread later).
//
// PERSISTENCE-RESTORE GOTCHA
// --------------------------
// On a pylon re-hydrated from persistence, `pylon.Anchor` is null (no live
// player attached). The OnPylonDeployed handler reads `pylon.Arena.Name` —
// `Arena` is required and always set, so this is safe. Original module had
// the same fix; preserved here.
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave 2: PylonInstance.Arena required field (no `pylon.Anchor.Arena` NRE
// on persistence-restored pylons).
// =============================================================================

public sealed partial class SectorWar : ISectorClaim
{
    private const string SectorClaimCommand = "claim";
    private const string SectorClaimAllCommand = "claimall";

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    /// <summary>Token for unregistering ISectorClaim on Unload.</summary>
    private InterfaceRegistrationToken<ISectorClaim>? _sectorClaimToken;

    /// <summary>Cached IPylon handle. Subscribed to during Load and released
    /// during Unload. Nullable because IPylon may not be available during
    /// the parallel-coexistence period — degrades gracefully.</summary>
    private IPylon? _sectorClaimPylon;

    /// <summary>arenaName -> freq -> claim weight. Synchronized via
    /// <see cref="_sectorClaimLock"/>.</summary>
    private readonly Dictionary<string, Dictionary<short, int>> _sectorClaimClaims =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>arenaName -> last-known dominant freq. Used to detect dominant
    /// flips in <see cref="AdjustSectorClaim"/>.</summary>
    private readonly Dictionary<string, short?> _sectorClaimLastDominant =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guards <see cref="_sectorClaimClaims"/> and
    /// <see cref="_sectorClaimLastDominant"/>. Leaf lock — never acquire while
    /// holding any other umbrella lock.</summary>
    private readonly Lock _sectorClaimLock = new();

    /// <summary>Fired when an arena's dominant freq flips. Both old and new
    /// can be null (no-dominant -> someone, or someone -> no-dominant).</summary>
    public event Action<string, short?, short?>? ArenaOwnerChanged;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Subscribes to IPylon events, registers the two commands, registers
    /// ISectorClaim on the broker. If IPylon isn't available at load time
    /// (load-order edge case), claims won't track; logs a Warn and continues
    /// rather than failing the load.
    /// </summary>
    private void LoadSectorClaim(IComponentBroker broker)
    {
        _sectorClaimPylon = broker.GetInterface<IPylon>();
        if (_sectorClaimPylon is null)
        {
            _logManager.LogM(LogLevel.Warn, LogCategory,
                "SectorClaim: IPylon unavailable; tracking will be empty until Pylon registers.");
        }
        else
        {
            _sectorClaimPylon.PylonDeployed += OnPylonDeployed_SectorClaim;
            _sectorClaimPylon.PylonDespawned += OnPylonDespawned_SectorClaim;
        }

        _sectorClaimToken = broker.RegisterInterface<ISectorClaim>(this);

        _logManager.LogM(LogLevel.Info, LogCategory, "SectorClaim subsystem loaded.");
    }

    /// <summary>Reverse of Load. Unsubscribes from events on the same IPylon
    /// instance we subscribed to, releases the cached interface, drops
    /// per-arena state.</summary>
    private void UnloadSectorClaim(IComponentBroker broker)
    {
        if (_sectorClaimToken is not null)
            broker.UnregisterInterface(ref _sectorClaimToken);

        if (_sectorClaimPylon is not null)
        {
            _sectorClaimPylon.PylonDeployed -= OnPylonDeployed_SectorClaim;
            _sectorClaimPylon.PylonDespawned -= OnPylonDespawned_SectorClaim;
            broker.ReleaseInterface(ref _sectorClaimPylon);
        }

        lock (_sectorClaimLock)
        {
            _sectorClaimClaims.Clear();
            _sectorClaimLastDominant.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH (no-ops — zone-wide subsystem)
    // -------------------------------------------------------------------------

    private void AttachSectorClaim(Arena arena)
    {
        _commandManager.AddCommand(SectorClaimCommand, Command_SectorClaim, arena);
        _commandManager.AddCommand(SectorClaimAllCommand, Command_SectorClaimAll, arena);
    }

    private void DetachSectorClaim(Arena arena)
    {
        _commandManager.RemoveCommand(SectorClaimCommand, Command_SectorClaim, arena);
        _commandManager.RemoveCommand(SectorClaimAllCommand, Command_SectorClaimAll, arena);
    }

    // -------------------------------------------------------------------------
    // PYLON EVENT HANDLERS
    // -------------------------------------------------------------------------

    private void OnPylonDeployed_SectorClaim(PylonInstance pylon)
    {
        // Wave 2: pylon.Arena is the required field (set on initial deploy AND
        // on persistence restore). Don't read pylon.Anchor.Arena — Anchor is
        // null for restored pylons, NRE waiting to happen.
        if (pylon.Arena.Name is not string arenaName) return;
        AdjustSectorClaim(arenaName, pylon.OwnerFreq, pylon.ClaimWeight);
    }

    private void OnPylonDespawned_SectorClaim(PylonInstance pylon)
    {
        if (pylon.Arena.Name is not string arenaName) return;
        AdjustSectorClaim(arenaName, pylon.OwnerFreq, -pylon.ClaimWeight);
    }

    // -------------------------------------------------------------------------
    // ISectorClaim IMPLEMENTATION
    // -------------------------------------------------------------------------

    SectorClaimSnapshot? ISectorClaim.GetSnapshot(string arenaName)
    {
        lock (_sectorClaimLock)
        {
            if (!_sectorClaimClaims.TryGetValue(arenaName, out var freqMap)) return null;
            return BuildSectorClaimSnapshot(arenaName, freqMap);
        }
    }

    IEnumerable<SectorClaimSnapshot> ISectorClaim.GetAllSnapshots()
    {
        lock (_sectorClaimLock)
        {
            var results = new List<SectorClaimSnapshot>(_sectorClaimClaims.Count);
            foreach (var (name, map) in _sectorClaimClaims)
                results.Add(BuildSectorClaimSnapshot(name, map));
            return results;
        }
    }

    // -------------------------------------------------------------------------
    // INTERNAL HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adjust a freq's claim count in an arena, then check for dominant-freq
    /// flip and fire <see cref="ArenaOwnerChanged"/> if needed. Lock is held
    /// only for the dictionary mutation; event invocation happens outside
    /// the lock to avoid holding it across handler code.
    /// </summary>
    private void AdjustSectorClaim(string arenaName, short freq, int delta)
    {
        short? oldDom;
        short? newDom;
        lock (_sectorClaimLock)
        {
            if (!_sectorClaimClaims.TryGetValue(arenaName, out var freqMap))
            {
                freqMap = new Dictionary<short, int>();
                _sectorClaimClaims[arenaName] = freqMap;
            }
            int next = (freqMap.TryGetValue(freq, out int cur) ? cur : 0) + delta;
            if (next <= 0) freqMap.Remove(freq);
            else freqMap[freq] = next;

            oldDom = _sectorClaimLastDominant.TryGetValue(arenaName, out short? d) ? d : null;
            newDom = ComputeSectorClaimDominant(freqMap);
            _sectorClaimLastDominant[arenaName] = newDom;
        }

        if (oldDom != newDom)
        {
            _logManager.LogM(LogLevel.Info, LogCategory,
                $"{arenaName} owner change: {oldDom?.ToString() ?? "<none>"} -> {newDom?.ToString() ?? "<none>"}");
            ArenaOwnerChanged?.Invoke(arenaName, oldDom, newDom);
        }
    }

    /// <summary>
    /// Find the freq with the highest claim. Ties at the top → return null
    /// (no clear dominant), which the snapshot path then renders as "contested".
    /// </summary>
    private static short? ComputeSectorClaimDominant(IReadOnlyDictionary<short, int> map)
    {
        if (map.Count == 0) return null;
        short? best = null;
        int bestVal = 0;
        bool tied = false;
        foreach (var (freq, val) in map)
        {
            if (val > bestVal) { best = freq; bestVal = val; tied = false; }
            else if (val == bestVal) tied = true;
        }
        return tied ? null : best;
    }

    /// <summary>
    /// Build a snapshot from the current claim map. Caller MUST hold
    /// <see cref="_sectorClaimLock"/>. Copies the freq map out to avoid
    /// aliasing — consumers can iterate without re-entering the lock.
    /// </summary>
    private static SectorClaimSnapshot BuildSectorClaimSnapshot(
        string arenaName, Dictionary<short, int> freqMap)
    {
        var copy = new Dictionary<short, int>(freqMap);
        short? dominant = ComputeSectorClaimDominant(copy);
        int total = 0;
        int domVal = 0;
        foreach (var (f, v) in copy) { total += v; if (f == dominant) domVal = v; }

        // Controlled iff there's a clear dominant AND it holds 50%+ of total.
        // Contested iff total>0 but not controlled (covers ties + sub-50% leaders).
        bool controlled = dominant is not null && total > 0 && (domVal * 2) >= total;
        bool contested = total > 0 && (dominant is null || !controlled);
        return new SectorClaimSnapshot
        {
            ArenaName = arenaName,
            ClaimByFreq = copy,
            DominantFreq = dominant,
            IsControlled = controlled,
            IsContested = contested,
        };
    }

    // -------------------------------------------------------------------------
    // COMMAND HANDLERS
    // -------------------------------------------------------------------------

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Show current arena's pylon-claim state.")]
    private void Command_SectorClaim(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena?.Name is not string arenaName)
        {
            _chat.SendMessage(player, "Not in an arena.");
            return;
        }
        ISectorClaim self = this;
        var snap = self.GetSnapshot(arenaName);
        if (snap is null || snap.ClaimByFreq.Count == 0)
        {
            _chat.SendMessage(player, $"{arenaName}: no pylons placed (unclaimed).");
            return;
        }
        SendSectorClaimSnapshotLines(player, snap);
    }

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Show pylon-claim state for ALL linked sector arenas.")]
    private void Command_SectorClaimAll(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        ISectorClaim self = this;
        var snaps = self.GetAllSnapshots().ToArray();
        if (snaps.Length == 0)
        {
            _chat.SendMessage(player, "No claim data — no pylons in any arena yet.");
            return;
        }
        _chat.SendMessage(player, "--- Sector Claim Status ---");
        foreach (var snap in snaps)
            SendSectorClaimSnapshotLines(player, snap);
    }

    /// <summary>Pretty-print one snapshot to the player as a header line plus
    /// a sorted-descending freq breakdown.</summary>
    private void SendSectorClaimSnapshotLines(Player player, SectorClaimSnapshot snap)
    {
        string status = snap.IsControlled
            ? $"controlled by freq {snap.DominantFreq}"
            : snap.IsContested
                ? "contested"
                : "unclaimed";
        _chat.SendMessage(player, $"  {snap.ArenaName}: {status}");
        foreach (var (freq, claim) in snap.ClaimByFreq.OrderByDescending(kv => kv.Value))
            _chat.SendMessage(player, $"    freq {freq}: {claim} claim");
    }
}
