using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — PerShipLvz subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Toggles a per-ship LVZ object on/off when a player switches ship class. Each
// ship section (Warbird, Javelin, Spider, Leviathan, Terrier, Weasel,
// Lancaster, Shark, Spectator) can specify "ShowLvz = <id>" in arena.conf —
// the player sees that LVZ object only while flying THAT ship class.
//
// Use cases: per-ship HUD overlays, ship-class-specific decorations, hangar
// visuals.
//
// IMPORTANT — `target = player` SCOPE
// -----------------------------------
// The toggle is sent with the player as target, so only THAT player sees the
// LVZ object. To broadcast a custom ship sprite to OTHER players, the target
// would have to be arena-scope; that's a separate feature (TODO future).
//
// SOURCE
// ------
// Port of JoWie's pershiplvz.c (ASSS, 2007). Standalone module
// `Modules/PerShipLvz.cs` stays in place as a library copy.
//
// CONF KEYS — KEPT IN STANDARD `[<ShipName>]` SECTIONS
// ----------------------------------------------------
// Unlike most subsystems whose config moves under `[SectorWar]`, the per-ship
// LVZ keys stay in their per-ship sections (`[Warbird] ShowLvz = …`). Reason:
// SS.NET's per-ship sections are a well-known convention and zone admins
// already organise their conf around them. Forcing `[SectorWar] WarbirdShowLvz`
// would be a worse migration. We document this as a deliberate exception in
// `docs/SECTORWAR_CONF.md`.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: ArenaData.PerShipLvzIds (per-arena short[9]),
//                  PerShipLvzPlayerData.PerShipLvzOldShip (per-player tracker).
//   - Conf keys read: [<ShipName>] ShowLvz (9 ships, including Spectator).
//   - Persisted data: NONE.
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE.
//   - Commands registered: NONE.
//
// CALLBACKS HOOKED (per-arena via Attach/Detach)
//   - ShipFreqChangeCallback → OnShipFreqChange_PerShipLvz (toggle on swap)
//   - PlayerActionCallback   → OnPlayerAction_PerShipLvz (initial show on
//                                                          EnterGame)
//
// THREADING
// ---------
// Both callbacks fire on the mainloop. <see cref="ILvzObjects.Toggle"/> is
// mainloop-safe. No locks needed.
//
// WAVE-FIXES PRESERVED
// --------------------
// None Wave-specific.
// =============================================================================

public sealed partial class SectorWar
{
    /// <summary>
    /// Order matches the SS.NET <see cref="ShipType"/> enum values
    /// (Warbird=0..Shark=7, Spec=8). Used to read per-ship config sections by
    /// name.
    /// </summary>
    // Conf surface read by the PerShipLvz subsystem — see docs/ARENA_SETTINGS.md.
    // One ShowLvz key per ship section (8 ships + Spectator).
    // Pinned to a field; the framework's Help scanner only walks members.
    [ConfigHelp<int>("Warbird",   "ShowLvz", ConfigScope.Arena, Default = -1, Min = -1, Max = 32767, Description = "LVZ object id to toggle on when entering this ship. -1 = none.")]
    [ConfigHelp<int>("Javelin",   "ShowLvz", ConfigScope.Arena, Default = -1, Min = -1, Max = 32767, Description = "LVZ object id to toggle on when entering this ship. -1 = none.")]
    [ConfigHelp<int>("Spider",    "ShowLvz", ConfigScope.Arena, Default = -1, Min = -1, Max = 32767, Description = "LVZ object id to toggle on when entering this ship. -1 = none.")]
    [ConfigHelp<int>("Leviathan", "ShowLvz", ConfigScope.Arena, Default = -1, Min = -1, Max = 32767, Description = "LVZ object id to toggle on when entering this ship. -1 = none.")]
    [ConfigHelp<int>("Terrier",   "ShowLvz", ConfigScope.Arena, Default = -1, Min = -1, Max = 32767, Description = "LVZ object id to toggle on when entering this ship. -1 = none.")]
    [ConfigHelp<int>("Weasel",    "ShowLvz", ConfigScope.Arena, Default = -1, Min = -1, Max = 32767, Description = "LVZ object id to toggle on when entering this ship. -1 = none.")]
    [ConfigHelp<int>("Lancaster", "ShowLvz", ConfigScope.Arena, Default = -1, Min = -1, Max = 32767, Description = "LVZ object id to toggle on when entering this ship. -1 = none.")]
    [ConfigHelp<int>("Shark",     "ShowLvz", ConfigScope.Arena, Default = -1, Min = -1, Max = 32767, Description = "LVZ object id to toggle on when entering this ship. -1 = none.")]
    [ConfigHelp<int>("Spectator", "ShowLvz", ConfigScope.Arena, Default = -1, Min = -1, Max = 32767, Description = "LVZ object id to toggle on when entering spectator. -1 = none.")]
    private static readonly string[] PerShipLvzShipNames =
    {
        "Warbird", "Javelin", "Spider", "Leviathan",
        "Terrier", "Weasel", "Lancaster", "Shark",
        "Spectator",
    };

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    /// <summary>Per-player extra-data slot — tracks last seen ship so we know
    /// which LVZ object to hide on swap.</summary>
    private PlayerDataKey<PerShipLvzPlayerData> _perShipLvzPdKey;

    // ArenaData extension: per-ship LVZ object id table.
    internal sealed partial class ArenaData
    {
        /// <summary>LVZ object id per ship class. Index = (int)ShipType.
        /// Value -1 means "no LVZ for this ship". Length 9 covers the 8 ship
        /// classes plus Spec.</summary>
        public short[] PerShipLvzIds = NewMinusOnes();

        private static short[] NewMinusOnes()
        {
            var a = new short[9];
            for (int i = 0; i < a.Length; i++) a[i] = -1;
            return a;
        }
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    /// <summary>Allocates the per-player tracker slot. No zone-wide callbacks
    /// (everything's per-arena via Attach).</summary>
    private void LoadPerShipLvz(IComponentBroker broker)
    {
        _perShipLvzPdKey = _playerData.AllocatePlayerData<PerShipLvzPlayerData>();
        _logManager.LogM(LogLevel.Info, LogCategory, "PerShipLvz subsystem loaded.");
    }

    /// <summary>Frees the per-player tracker slot.</summary>
    private void UnloadPerShipLvz(IComponentBroker broker)
    {
        _playerData.FreePlayerData(ref _perShipLvzPdKey);
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    // -------------------------------------------------------------------------

    /// <summary>
    /// Subscribes the two per-arena callbacks and reads the per-ship ShowLvz
    /// config for THIS arena. The config table lives on ArenaData so it
    /// survives reads from the callback hot-path without re-parsing conf.
    /// </summary>
    private void AttachPerShipLvz(Arena arena)
    {
        ShipFreqChangeCallback.Register(arena, OnShipFreqChange_PerShipLvz);
        PlayerActionCallback.Register(arena, OnPlayerAction_PerShipLvz);

        if (arena.TryGetExtraData(_adKey, out ArenaData? ad))
        {
            ConfigHandle? cfg = arena.Cfg;
            if (cfg is not null)
            {
                // Walk the 9 ship sections. Missing key => -1 (which the
                // callback path skips). This pre-population avoids a
                // GetInt call on every ship swap.
                for (int i = 0; i < PerShipLvzShipNames.Length; i++)
                {
                    ad.PerShipLvzIds[i] = (short)_configManager.GetInt(
                        cfg, PerShipLvzShipNames[i], "ShowLvz", -1);
                }
            }
        }
    }

    /// <summary>Unsubscribes both callbacks. The ArenaData LVZ table is
    /// reset by the umbrella's TryReset path on slot recycle.</summary>
    private void DetachPerShipLvz(Arena arena)
    {
        ShipFreqChangeCallback.Unregister(arena, OnShipFreqChange_PerShipLvz);
        PlayerActionCallback.Unregister(arena, OnPlayerAction_PerShipLvz);
    }

    // -------------------------------------------------------------------------
    // CALLBACKS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fires on every ship/freq change. Only acts when the SHIP changed
    /// (freq-only changes don't swap LVZ; that's FreqChangeWarp's job).
    /// </summary>
    private void OnShipFreqChange_PerShipLvz(
        Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
    {
        if (player?.Arena is null) return;
        if (newShip != oldShip)
            SetPerShipLvz(player, newShip);
    }

    /// <summary>
    /// Fires on player lifecycle events. We only act on EnterGame — that's
    /// the moment when the player has joined a ship for real and the initial
    /// LVZ should appear. Reset OldShip to Spec sentinel so the SetPerShipLvz
    /// "old != new" path correctly toggles ON the first real LVZ.
    /// </summary>
    private void OnPlayerAction_PerShipLvz(Player player, PlayerAction action, Arena? arena)
    {
        if (player is null || arena is null || player.Arena != arena) return;
        if (action != PlayerAction.EnterGame) return;

        if (player.TryGetExtraData(_perShipLvzPdKey, out PerShipLvzPlayerData? pd))
            pd.OldShip = ShipType.Spec;  // sentinel "no previous"

        SetPerShipLvz(player, player.Ship);
    }

    // -------------------------------------------------------------------------
    // INTERNAL HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Toggle off the LVZ for the OLD ship and on for the NEW ship. No-ops
    /// for ships with no ShowLvz configured (id stored as -1). The toggles
    /// are sent with the player as target so only that player sees them.
    /// </summary>
    private void SetPerShipLvz(Player player, ShipType newShip)
    {
        if (player?.Arena is null) return;
        if (!player.Arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (!player.TryGetExtraData(_perShipLvzPdKey, out PerShipLvzPlayerData? pd)) return;

        short oldLvz = PerShipLvzForShip(ad, pd.OldShip);
        short newLvz = PerShipLvzForShip(ad, newShip);

        if (oldLvz != newLvz)
        {
            ITarget target = player;
            if (oldLvz >= 0) _lvzObjects.Toggle(target, oldLvz, false);
            if (newLvz >= 0) _lvzObjects.Toggle(target, newLvz, true);
        }
        pd.OldShip = newShip;
    }

    private static short PerShipLvzForShip(ArenaData ad, ShipType ship)
    {
        int idx = (int)ship;
        if (idx < 0 || idx >= 9) return -1;
        return ad.PerShipLvzIds[idx];
    }

    // -------------------------------------------------------------------------
    // PER-PLAYER DATA
    // -------------------------------------------------------------------------

    private sealed class PerShipLvzPlayerData : IResettable
    {
        /// <summary>Last ship class we showed an LVZ for. Spec sentinel
        /// means "no LVZ currently shown" (Spec has no ShowLvz typically).</summary>
        public ShipType OldShip = ShipType.Spec;

        bool IResettable.TryReset()
        {
            OldShip = ShipType.Spec;
            return true;
        }
    }
}
