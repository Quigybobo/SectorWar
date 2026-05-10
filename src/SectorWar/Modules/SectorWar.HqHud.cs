using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — HqHud subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Two screen-relative LVZ status icons anchored above the mini-map showing
// the live state of each team's HQ. Same anchor + visual idiom as the
// existing claim-row indicators (SectorClaimVisual): map=False, layer 7
// TopMost, ScreenOffset.R, fixed pixel offset above the radar.
//
// State (per freq) is driven by the existing HqArenaState.Capitals data
// the Hq subsystem already maintains:
//   OK   — capital is Alive AND no defender died recently (steady cyan).
//   DMG  — capital is Alive but a perimeter gun / command core died within
//          the last HqHudDmgWindowMs (amber icon). The Hq subsystem stamps
//          HqCapitalRuntime.LastDefenderDeathTickMs in OnBotKilled_Hq.
//   CRIT — capital is dead, awaiting module-driven respawn (red icon w/ X).
//
// LVZ POOL
//   - 9350  freq-0 HQ HUD slot
//   - 9351  freq-1 HQ HUD slot
//   Initial image (set in the LVZ file by tools/lvz_warbird_capital.py) is
//   the OK icon. SetImage swaps to CRIT when capital is dead.
//
// SCREEN POSITION
//   ScreenOffset.R (radar anchor — top-left of the mini-map). The C#
//   sets X across two slots (left = freq 0, right = freq 1) and Y far
//   enough above the claim-row that they don't overlap.
//
// RUNTIME OWNERSHIP
//   - Owned state: per-arena flag tracking "last image we set" so we don't
//     spam SetImage when the state hasn't changed.
//   - Conf keys: NONE.
//   - Persisted data: NONE.
//   - Fakes registered: NONE.
//   - Timers: 1 Hz mainloop tick (recomputes state).
//   - Broker interfaces published: NONE.
//   - Subscribed: NONE (polling-only — reads HqArenaState directly).
//
// THREADING: mainloop only.
// =============================================================================

public sealed partial class SectorWar
{
    // -------------------------------------------------------------------------
    // CONSTANTS
    // -------------------------------------------------------------------------

    /// <summary>LVZ object IDs reserved by tools/lvz_warbird_capital.py.</summary>
    private const short HqHudSlotFreq0 = 9350;
    private const short HqHudSlotFreq1 = 9351;

    /// <summary>Image indices in sectorwar.lvz. Order matches the
    /// `hq_hud_state_keys` list in lvz_warbird_capital.py main():
    /// hq_hud_ok, hq_hud_dmg, hq_hud_crit. If the LVZ image-list order
    /// changes, update these indices.</summary>
    private const byte HqHudImageOk   = 22;
    private const byte HqHudImageDmg  = 23;
    private const byte HqHudImageCrit = 24;

    /// <summary>Tick cadence for HUD state polling.</summary>
    private const int HqHudTickMs = 1000;

    /// <summary>How long the amber DMG icon stays lit after the most recent
    /// defender (perimeter gun / command core) kill. Defenders respawn on
    /// their own (infiniteRespawn=true), so this window is the player-facing
    /// signal that the HQ is currently under fire.</summary>
    private const int HqHudDmgWindowMs = 8000;

    /// <summary>Screen-relative position above the mini-map. Anchor = R
    /// (top-left of radar). X stride matches SectorClaimVisualColumnXStep
    /// (24px tile + 2px gap). Y is more negative than the claim-row's
    /// y=-30 so the HUD sits above it.</summary>
    private const short HqHudColumnX0 = 0;
    private const short HqHudColumnXStep = 36;   // 32px icon + 4px gap
    private const short HqHudRowY = -68;         // ~36px above the claim row

    // -------------------------------------------------------------------------
    // PER-ARENA STATE
    // -------------------------------------------------------------------------

    internal sealed class HqHudArenaState
    {
        /// <summary>Last image we sent for each freq slot. Lets the tick
        /// avoid redundant SetImage broadcasts when state is unchanged.</summary>
        public byte LastImageFreq0 = byte.MaxValue;  // sentinel = "never set"
        public byte LastImageFreq1 = byte.MaxValue;
        public bool Initialized;
    }

    internal sealed partial class ArenaData
    {
        public HqHudArenaState? HqHudArenaState;
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private IComponentBroker? _hqHudBroker;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD
    // -------------------------------------------------------------------------

    private void LoadHqHud(IComponentBroker broker)
    {
        _hqHudBroker = broker;
        _mainloopTimer.SetTimer(OnTick_HqHud, HqHudTickMs, HqHudTickMs, this);
        _logManager.LogM(LogLevel.Info, LogCategory, "HqHud subsystem loaded.");
    }

    private void UnloadHqHud(IComponentBroker broker)
    {
        _mainloopTimer.ClearTimer(OnTick_HqHud, this);
        _hqHudBroker = null;
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    // -------------------------------------------------------------------------

    private void AttachHqHud(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        var st = new HqHudArenaState { Initialized = true };
        ad.HqHudArenaState = st;

        // Position both slots screen-relative above the mini-map (radar
        // anchor) and toggle them on with the OK image as default state.
        // The Hq subsystem may not have spawned yet (Hq attaches AFTER us
        // in the umbrella's AttachModule order); in that case OK is the
        // accurate initial state anyway.
        try
        {
            short x0 = (short)(HqHudColumnX0);
            short x1 = (short)(HqHudColumnX0 + HqHudColumnXStep);
            _lvzObjects.SetPosition(arena, HqHudSlotFreq0, x0, HqHudRowY,
                ScreenOffset.R, ScreenOffset.R);
            _lvzObjects.SetImage(arena, HqHudSlotFreq0, HqHudImageOk);
            _lvzObjects.Toggle(arena, HqHudSlotFreq0, true);
            st.LastImageFreq0 = HqHudImageOk;

            _lvzObjects.SetPosition(arena, HqHudSlotFreq1, x1, HqHudRowY,
                ScreenOffset.R, ScreenOffset.R);
            _lvzObjects.SetImage(arena, HqHudSlotFreq1, HqHudImageOk);
            _lvzObjects.Toggle(arena, HqHudSlotFreq1, true);
            st.LastImageFreq1 = HqHudImageOk;
        }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                $"HqHud attach: initial LVZ setup failed: {ex.Message}");
        }
    }

    private void DetachHqHud(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (ad.HqHudArenaState is null) return;
        try
        {
            _lvzObjects.Toggle(arena, HqHudSlotFreq0, false);
            _lvzObjects.Toggle(arena, HqHudSlotFreq1, false);
        }
        catch { /* phong's no-crash rule */ }
        ad.HqHudArenaState = null;
    }

    // -------------------------------------------------------------------------
    // TICK — polls capital state, updates the HUD images
    // -------------------------------------------------------------------------

    private bool OnTick_HqHud()
    {
        _arenaManager.Lock();
        try
        {
            foreach (Arena arena in _arenaManager.Arenas)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) continue;
                if (ad.HqHudArenaState is not { Initialized: true } hud) continue;

                // Read each freq's HQ capital state from the Hq subsystem's
                // existing tracker. Capitals[] order parallels _hqDefinitions
                // (freq 0 first, freq 1 second). Resolve a 3-state image:
                // dead capital → CRIT, recent defender death → DMG, else OK.
                int now = Environment.TickCount;
                byte target0 = HqHudImageOk;
                byte target1 = HqHudImageOk;
                if (ad.HqArenaState is { } hqState)
                {
                    foreach (var cap in hqState.Capitals)
                    {
                        byte img = ResolveHqHudImage(cap, now);
                        if (cap.Freq == 0) target0 = img;
                        else if (cap.Freq == 1) target1 = img;
                    }
                }

                if (target0 != hud.LastImageFreq0)
                {
                    try { _lvzObjects.SetImage(arena, HqHudSlotFreq0, target0); }
                    catch { continue; }
                    hud.LastImageFreq0 = target0;
                }
                if (target1 != hud.LastImageFreq1)
                {
                    try { _lvzObjects.SetImage(arena, HqHudSlotFreq1, target1); }
                    catch { continue; }
                    hud.LastImageFreq1 = target1;
                }
            }
        }
        finally { _arenaManager.Unlock(); }
        return true;
    }

    private static byte ResolveHqHudImage(HqCapitalRuntime cap, int nowMs)
    {
        if (!cap.Alive) return HqHudImageCrit;
        if (cap.LastDefenderDeathTickMs != 0
            && (uint)(nowMs - cap.LastDefenderDeathTickMs) < HqHudDmgWindowMs)
            return HqHudImageDmg;
        return HqHudImageOk;
    }
}
