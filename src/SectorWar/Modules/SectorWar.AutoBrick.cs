using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — AutoBrick subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Periodically drops a configured set of bricks at fixed coordinates to make
// permanent walls/barriers in arenas without editing the map itself. Reads
// up to 32 bricks per arena via [SectorWar] AutoBrickBrickN=x1,y1,x2,y2 plus
// optional AutoBrickTeamN=freq for per-brick team tinting.
//
// Bricks must be horizontal or vertical and listed consecutively
// (AutoBrickBrick0, AutoBrickBrick1, …). The timer fires at
// (Brick:BrickTime - 100ms) so a fresh brick drops just before the previous
// one expires — creates the illusion of permanence.
//
// SOURCE
// ------
// Port of smong's autobrick.c (ASSS, 2003-2006):
//   bitbucket.org/jowie/asss-autobrick
// The standalone module at Modules/AutoBrick.cs stays in place as a library
// copy. This partial preserves identical behaviour.
//
// CONF MIGRATION
// --------------
// Original key prefix `[AutoBrick] BrickN` → `[SectorWar] AutoBrickBrickN`
// (qualified prefix avoids collision with SS.NET's `[Brick] BrickTime` which
// IS still read from the standard `[Brick]` section since it's owned by the
// core Bricks module, not us). Same for AutoBrickTeamN.
//
//   was [AutoBrick] Brick0 = ...      becomes [SectorWar] AutoBrickBrick0 = ...
//   was [AutoBrick] Team0  = ...      becomes [SectorWar] AutoBrickTeam0  = ...
//   [Brick] BrickTime                 unchanged — owned by SS.NET Bricks core
//   [Team] SpectatorFrequency         unchanged — owned by SS.NET FreqManager
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: ArenaData.AutoBrickBricks (per-arena List<BrickData>).
//   - Conf keys read: [SectorWar] AutoBrickBrickN, AutoBrickTeamN (per-arena);
//                     [Team] SpectatorFrequency, [Brick] BrickTime
//                     (read but not owned).
//   - Persisted data: NONE (bricks regenerate from conf on attach).
//   - Fakes registered: NONE.
//   - Timers scheduled: per-arena ServerTimer keyed by ArenaData, fires every
//                       (BrickTime - 100ms).
//   - Commands registered: NONE.
//
// CALLBACKS HOOKED (zone-wide, fans out by arena)
//   - ArenaActionCallback → OnArenaAction_AutoBrick
//     (only acts on ConfChanged so live-edit of bricks works without recycle)
//
// THREADING
// ---------
// All ServerTimer callbacks run on the mainloop. ArenaActionCallback fires
// on the mainloop. No locks needed.
//
// WAVE-FIXES PRESERVED
// --------------------
// Wave 11: ArenaActionCallback hook (ConfChanged) so brick edits via
// `?reloadconf` are picked up without a full arena recycle.
// =============================================================================

public sealed partial class SectorWar
{
    /// <summary>Maximum brick entries read from conf (per arena). Matches
    /// the original module's hard cap.</summary>
    private const int AutoBrickMaxBricks = 32;

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    /// <summary>Per-brick coordinates + freq tint. <c>X1/Y1/X2/Y2</c> are tile
    /// coords, not pixels — same as <see cref="IBrickManager.DropBrick"/>'s
    /// expected units. Internal so ArenaData (also internal) can hold a List
    /// of these without accessibility complaints.</summary>
    internal sealed record AutoBrickData(short X1, short Y1, short X2, short Y2, short Freq);

    // ArenaData extension: subsystem state lives on the umbrella's per-arena
    // record so the umbrella's ArenaDataKey allocation is shared across all
    // subsystems (one slot, one allocation, one IResettable.TryReset path).
    internal sealed partial class ArenaData
    {
        /// <summary>Bricks parsed from conf at attach. Empty list = subsystem
        /// inactive on this arena (no error — just nothing to drop).</summary>
        public List<AutoBrickData> AutoBrickBricks = new();

        /// <summary>Last computed (BrickTime - 100ms) interval, kept to
        /// support the ConfChanged refresh path.</summary>
        public int AutoBrickIntervalMs;
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers <see cref="ArenaActionCallback"/> at the broker so live conf
    /// edits trigger a re-attach. Per-arena timer scheduling happens in
    /// <see cref="AttachAutoBrick"/>.
    /// </summary>
    private void LoadAutoBrick(IComponentBroker broker)
    {
        ArenaActionCallback.Register(broker, OnArenaAction_AutoBrick);
        _logManager.LogM(LogLevel.Info, LogCategory, "AutoBrick subsystem loaded.");
    }

    /// <summary>Reverse of Load.</summary>
    private void UnloadAutoBrick(IComponentBroker broker)
    {
        ArenaActionCallback.Unregister(broker, OnArenaAction_AutoBrick);
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads conf for THIS arena, populates the brick list, schedules the
    /// drop timer. Idempotent: clears any prior bricks + cancels any prior
    /// timer first, so calling Attach repeatedly (e.g. via ConfChanged) is
    /// safe.
    /// </summary>
    private void AttachAutoBrick(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        ConfigHandle? cfg = arena.Cfg;
        if (cfg is null) return;

        // Idempotency: cancel any prior timer keyed on this ArenaData and
        // clear the prior brick list before repopulating.
        _serverTimer.ClearTimer<ArenaData>(OnTick_AutoBrick, ad);
        ad.AutoBrickBricks.Clear();

        // [Team] SpectatorFrequency is SS.NET FreqManager-owned. Used as the
        // default freq for bricks that don't specify a team override.
        short specFreq = (short)_configManager.GetInt(cfg, "Team", "SpectatorFrequency", 8025);

        // Read up to AutoBrickMaxBricks consecutive entries. First missing
        // index ends the list (mirrors the original ASSS module's behaviour).
        for (int i = 0; i < AutoBrickMaxBricks; i++)
        {
            string? value = _configManager.GetStr(cfg, ConfSection, $"AutoBrickBrick{i}");
            if (string.IsNullOrWhiteSpace(value)) break;

            if (!TryParseAutoBrick(value, out short x1, out short y1, out short x2, out short y2))
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"Bad AutoBrickBrick{i} format '{value}' — expected x1,y1,x2,y2");
                continue;
            }

            short freq = (short)_configManager.GetInt(cfg, ConfSection, $"AutoBrickTeam{i}", specFreq);
            ad.AutoBrickBricks.Add(new AutoBrickData(x1, y1, x2, y2, freq));
        }

        if (ad.AutoBrickBricks.Count == 0)
        {
            _logManager.LogA(LogLevel.Info, LogCategory, arena,
                "AutoBrick attached with 0 bricks (none configured).");
            return;
        }

        // [Brick] BrickTime is owned by SS.NET Bricks core. We schedule at
        // (BrickTime - 100ms) so a fresh brick drops just before the previous
        // one expires. Math.Max(100, ...) guards against pathological 0-time
        // configs that would otherwise schedule a tight loop.
        int brickTime = _configManager.GetInt(cfg, "Brick", "BrickTime", 12000);
        ad.AutoBrickIntervalMs = Math.Max(100, brickTime - 100);

        // SetTimer args: (callback, initial delay, repeat interval, state, key).
        // Initial 300ms delay lets attach finish before the first drop.
        _serverTimer.SetTimer<ArenaData>(OnTick_AutoBrick, 300, ad.AutoBrickIntervalMs, ad, ad);

        _logManager.LogA(LogLevel.Info, LogCategory, arena,
            $"AutoBrick attached with {ad.AutoBrickBricks.Count} brick(s), refresh every {ad.AutoBrickIntervalMs}ms.");
    }

    /// <summary>
    /// Cancels the per-arena drop timer. The brick list itself is cleared by
    /// the umbrella's <see cref="ArenaData.IResettable.TryReset"/> when the
    /// per-arena slot is recycled.
    /// </summary>
    private void DetachAutoBrick(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        _serverTimer.ClearTimer<ArenaData>(OnTick_AutoBrick, ad);
    }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Re-runs the conf read + timer reschedule when the operator edits conf
    /// (Wave-11 fix). Without this, BrickTime / AutoBrickBrickN edits via
    /// `?reloadconf` were silently ignored until the next attach (usually
    /// arena recycle).
    /// </summary>
    private void OnArenaAction_AutoBrick(Arena arena, ArenaAction action)
    {
        if (action != ArenaAction.ConfChanged) return;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? _)) return;
        // Cleanest re-init path: just re-run Attach. It's idempotent.
        AttachAutoBrick(arena);
    }

    /// <summary>
    /// Timer callback — drops every configured brick once per tick.
    /// Returning <c>true</c> keeps the timer firing; <c>false</c> if the
    /// arena was torn down.
    /// </summary>
    private bool OnTick_AutoBrick(ArenaData ad)
    {
        if (ad.Arena is null) return false;
        foreach (var b in ad.AutoBrickBricks)
        {
            _brickManager.DropBrick(ad.Arena, b.Freq, b.X1, b.Y1, b.X2, b.Y2);
        }
        return true;
    }

    // -------------------------------------------------------------------------
    // INTERNAL HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses "x1,y1,x2,y2" into four shorts. Returns false on any parse
    /// failure (caller logs the bad entry and skips it).
    /// </summary>
    private static bool TryParseAutoBrick(string text,
        out short x1, out short y1, out short x2, out short y2)
    {
        x1 = y1 = x2 = y2 = 0;
        var parts = text.Split(',');
        if (parts.Length != 4) return false;
        return short.TryParse(parts[0].Trim(), out x1)
            && short.TryParse(parts[1].Trim(), out y1)
            && short.TryParse(parts[2].Trim(), out x2)
            && short.TryParse(parts[3].Trim(), out y2);
    }
}
