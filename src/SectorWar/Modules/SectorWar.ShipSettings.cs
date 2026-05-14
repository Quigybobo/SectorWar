using SS.SectorWar.Interfaces;
using SS.SectorWar.Items;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — ShipSettings subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Per-ship-per-player setting overrides for the Hyperspace-style upgrade model.
// The framework manages a small set of tracked keys (energy/recharge/thrust/
// speed/rotation/bullet-bomb-speed/radius); each ship-section's `.Floor`
// subsection holds the BASE values for any player who's on that ship class,
// and equipped items in <see cref="IInventory"/> add to those floors via
// `__SHIP__` modifiers.
//
// The cap stays at the standard `[<ShipName>]` section's value; no item can
// raise a stat above the cap.
//
// SOURCE
// ------
// Standalone module `Modules/ShipSettings.cs` stays as a library copy.
//
// CONF KEYS — KEPT IN STANDARD `[<ShipName>]` AND `[<ShipName>.Floor]` SECTIONS
// -----------------------------------------------------------------------------
// Like PerShipLvz, the per-ship sections stay where SS.NET conventionally
// puts them. Forcing them under `[SectorWar]` would worsen the migration story
// for zone admins. Documented as a deliberate exception in
// `docs/SECTORWAR_CONF.md`.
//
//   [Warbird]                  ; cap values (SS.NET standard ship section)
//     MaximumEnergy = 1700
//     MaximumThrust = 17
//     ...
//
//   [Warbird.Floor]            ; floor values (SectorWar-specific subsection)
//     MaximumEnergy = 1000     ; baseline before items
//     ...
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: 2D arrays of ClientSettingIdentifier resolved at Load
//                  (stored on subsystem fields, not ArenaData — identifiers
//                  are zone-wide).
//   - Conf keys read: [<ship>] / [<ship>.Floor] for 8 ships × 8 keys = 64.
//   - Persisted data: NONE (overrides are recomputed on EnterGame).
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE.
//   - Commands registered: cmd_shipfloor (was cmd_floor; renamed to disambiguate
//                          from any future game-mechanic ?floor command).
//   - Broker interfaces published: IShipSettings.
//
// CALLBACKS HOOKED (zone-wide)
//   - PlayerActionCallback → OnPlayerAction_ShipSettings (re-apply on EnterGame)
//
// THREADING
// ---------
// Mainloop only. ClientSettings overrides are mainloop-safe.
//
// NAMING NOTE
// -----------
// The standalone module's command was `?floor`. The umbrella renames it to
// `?shipfloor` so it doesn't clash with any future "floor" command someone
// might add. Old standalone keeps `?floor`; that's fine — they're parallel
// implementations.
//
// WAVE-FIXES PRESERVED
// --------------------
// Identifier resolution counts both attempts and successes; logs a Warn for
// any unresolvable (ship, key) pair.
// =============================================================================

public sealed partial class SectorWar : IShipSettings
{
    /// <summary>SS.NET ship-section names, ordered to match the
    /// <see cref="ShipType"/> enum (Warbird=0..Shark=7).</summary>
    private static readonly string[] ShipSettingsShipSections =
    {
        "Warbird", "Javelin", "Spider", "Leviathan",
        "Terrier", "Weasel", "Lancaster", "Shark",
    };

    /// <summary>Tracked client-settings keys for the floor-cap framework.
    /// Adding a new key here also requires adding `__SHIP__` modifier
    /// support in items if the new key should be item-bumpable. Radius is
    /// here so Hull-tier items can grow the collision circle alongside the
    /// visible Capital ship sprite (it's a per-ship MiscBitfield —
    /// TryGetSettingsIdentifier resolves bitfields the same way).</summary>
    private static readonly string[] ShipSettingsTrackedKeys =
    {
        "MaximumEnergy",
        "MaximumRecharge",
        "MaximumThrust",
        "MaximumSpeed",
        "MaximumRotation",
        "BulletSpeed",
        "BombSpeed",
        "Radius",
    };

    private const string ShipSettingsFloorCommand = "floor";

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    /// <summary>[shipIndex, keyIndex] → identifier. ClientSettingIdentifier is
    /// a struct, so the default (zero) value would be ambiguous; we track
    /// resolution status separately in <see cref="_shipSettingsResolved"/>.</summary>
    private readonly ClientSettingIdentifier[,] _shipSettingsIdentifiers =
        new ClientSettingIdentifier[ShipSettingsShipSections.Length, ShipSettingsTrackedKeys.Length];

    /// <summary>Parallel array tracking which (ship, key) pairs successfully
    /// resolved at Load. Skipped pairs log a Warn but don't fail the load.</summary>
    private readonly bool[,] _shipSettingsResolved =
        new bool[ShipSettingsShipSections.Length, ShipSettingsTrackedKeys.Length];

    /// <summary>Token for unregistering IShipSettings on Unload.</summary>
    private InterfaceRegistrationToken<IShipSettings>? _shipSettingsToken;

    /// <summary>Cached broker handle so the IInventory lookup in
    /// <see cref="ApplyShipSettingsFloorOverrides"/> can release cleanly.</summary>
    private IComponentBroker? _shipSettingsBroker;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves the 64 (ship × key) identifiers, registers the PlayerAction
    /// callback, registers the IShipSettings interface, registers the
    /// ?shipfloor diagnostic command.
    /// </summary>
    private void LoadShipSettings(IComponentBroker broker)
    {
        _shipSettingsBroker = broker;
        ResolveShipSettingsIdentifiers();

        PlayerActionCallback.Register(broker, OnPlayerAction_ShipSettings);

        _shipSettingsToken = broker.RegisterInterface<IShipSettings>(this);

        _logManager.LogM(LogLevel.Info, LogCategory, "ShipSettings subsystem loaded.");
    }

    /// <summary>Reverse of Load. Symmetric — every Register has an Unregister.</summary>
    private void UnloadShipSettings(IComponentBroker broker)
    {
        if (_shipSettingsToken is not null)
            broker.UnregisterInterface(ref _shipSettingsToken);

        PlayerActionCallback.Unregister(broker, OnPlayerAction_ShipSettings);
        _shipSettingsBroker = null;
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH — arena-scoped command registration
    // -------------------------------------------------------------------------

    private void AttachShipSettings(Arena arena)
    {
        _commandManager.AddCommand(ShipSettingsFloorCommand, Command_ShipSettingsFloor, arena);
    }

    private void DetachShipSettings(Arena arena)
    {
        _commandManager.RemoveCommand(ShipSettingsFloorCommand, Command_ShipSettingsFloor, arena);
    }

    // -------------------------------------------------------------------------
    // IShipSettings IMPLEMENTATION
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reapply floor overrides for a given player. Called by Inventory or any
    /// other consumer when an item is equipped/unequipped — the floor + cap
    /// values may have changed and the player's ClientSettings need a refresh.
    /// </summary>
    void IShipSettings.RefreshPlayer(Player player)
    {
        if (player?.Arena is null) return;
        ApplyShipSettingsFloorOverrides(player, player.Arena);
    }

    // -------------------------------------------------------------------------
    // CALLBACK
    // -------------------------------------------------------------------------

    /// <summary>EnterGame is the moment the player has actually joined a ship
    /// + arena combo. Apply floor overrides then; subsequent ship swaps inside
    /// the same arena will fire EnterGame again so we don't need a separate
    /// ShipFreqChange hook.</summary>
    private void OnPlayerAction_ShipSettings(Player player, PlayerAction action, Arena? arena)
    {
        if (action != PlayerAction.EnterGame) return;
        if (player is null || arena is null) return;
        ApplyShipSettingsFloorOverrides(player, arena);
    }

    // -------------------------------------------------------------------------
    // INTERNAL HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walk the 8 ships × 8 tracked keys and pre-resolve every
    /// ClientSettingIdentifier. Anything that can't resolve gets a Warn but
    /// doesn't fail the load — partial coverage is better than no coverage.
    /// </summary>
    private void ResolveShipSettingsIdentifiers()
    {
        int resolved = 0;
        int total = 0;

        for (int s = 0; s < ShipSettingsShipSections.Length; s++)
        {
            for (int k = 0; k < ShipSettingsTrackedKeys.Length; k++)
            {
                total++;
                if (_clientSettings.TryGetSettingsIdentifier(
                        ShipSettingsShipSections[s], ShipSettingsTrackedKeys[k], out var id))
                {
                    _shipSettingsIdentifiers[s, k] = id;
                    _shipSettingsResolved[s, k] = true;
                    resolved++;
                }
                else
                {
                    _logManager.LogM(LogLevel.Warn, LogCategory,
                        $"Could not resolve identifier for {ShipSettingsShipSections[s]}:{ShipSettingsTrackedKeys[k]}");
                }
            }
        }

        _logManager.LogM(LogLevel.Info, LogCategory,
            $"Resolved {resolved}/{total} ship setting identifiers.");
    }

    /// <summary>
    /// For each ship class, compute (floor + sum-of-equipped-item-modifiers)
    /// clamped to the cap, and override the player's ClientSetting. Sends
    /// the updated settings packet at the end if anything changed.
    /// </summary>
    /// <remarks>
    /// IInventory is fetched per-call (not cached) because the umbrella's
    /// SectorClaim/SectorWar pattern prefers freshness — Inventory may
    /// register/unregister across hot reloads. The lookup is cheap and the
    /// release is bracketed in a finally-equivalent.
    /// </remarks>
    private void ApplyShipSettingsFloorOverrides(Player player, Arena arena)
    {
        ConfigHandle? cfg = arena.Cfg;
        if (cfg is null) return;

        IInventory? inventory = _shipSettingsBroker?.GetInterface<IInventory>();
        int totalItemsEquipped = 0;
        int applied = 0;

        try
        {
            for (int s = 0; s < ShipSettingsShipSections.Length; s++)
            {
                string section = ShipSettingsShipSections[s];
                string floorSection = section + ".Floor";
                ShipType ship = (ShipType)s;

                // Sum all `__SHIP__` modifiers from items currently equipped on THIS ship.
                var keyAddends = new Dictionary<string, int>();
                IReadOnlyList<ItemDefinition>? equipped = inventory?.GetEquippedForShip(player, ship);
                if (equipped is not null)
                {
                    totalItemsEquipped += equipped.Count;
                    foreach (var item in equipped)
                    {
                        foreach (var mod in item.Modifiers)
                        {
                            if (mod.Section == "__SHIP__")
                            {
                                keyAddends.TryGetValue(mod.Key, out int existing);
                                keyAddends[mod.Key] = existing + mod.Addend;
                            }
                        }
                    }
                }

                // Apply each tracked key: floor + addend, clamped to cap.
                for (int k = 0; k < ShipSettingsTrackedKeys.Length; k++)
                {
                    if (!_shipSettingsResolved[s, k]) continue;

                    string key = ShipSettingsTrackedKeys[k];
                    int floorValue = _configManager.GetInt(cfg, floorSection, key, -1);
                    if (floorValue < 0) continue;  // no floor configured for this (ship, key) — skip

                    int capValue = _configManager.GetInt(cfg, section, key, int.MaxValue);
                    int addend = keyAddends.TryGetValue(key, out int a) ? a : 0;

                    int active = floorValue + addend;
                    if (active > capValue) active = capValue;
                    if (active < 0) active = 0;

                    _clientSettings.OverrideSetting(player, _shipSettingsIdentifiers[s, k], active);
                    applied++;
                }
            }
        }
        finally
        {
            if (inventory is not null && _shipSettingsBroker is not null)
                _shipSettingsBroker.ReleaseInterface(ref inventory);
        }

        if (applied > 0)
        {
            _clientSettings.SendClientSettings(player);
            // Drivel — fires on every equip/unequip + on each arena entry,
            // so noisy during a shopping session. Significant inventory
            // changes have their own logs at the menu / equip call sites.
            _logManager.LogP(LogLevel.Drivel, LogCategory, player,
                $"Refreshed {applied} ship setting overrides ({totalItemsEquipped} items equipped across all ships).");
        }
    }

    // -------------------------------------------------------------------------
    // COMMAND
    // -------------------------------------------------------------------------

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = null,
        Description = "Shows your current ship's floor vs live values.")]
    private void Command_ShipSettingsFloor(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        Arena? arena = player.Arena;
        if (arena is null)
        {
            _chat.SendMessage(player, "Not in an arena.");
            return;
        }

        ConfigHandle? cfg = arena.Cfg;
        if (cfg is null) return;

        int currentShip = (int)player.Ship;
        if (currentShip < 0 || currentShip >= ShipSettingsShipSections.Length)
        {
            _chat.SendMessage(player, "Get into a ship to see floor values.");
            return;
        }

        string ship = ShipSettingsShipSections[currentShip];
        string floorSection = ship + ".Floor";

        _chat.SendMessage(player, $"--- {ship} floor vs live ---");
        for (int k = 0; k < ShipSettingsTrackedKeys.Length; k++)
        {
            if (!_shipSettingsResolved[currentShip, k]) continue;

            string key = ShipSettingsTrackedKeys[k];
            int floorValue = _configManager.GetInt(cfg, floorSection, key, -1);
            int liveValue = _clientSettings.GetSetting(player, _shipSettingsIdentifiers[currentShip, k]);
            _chat.SendMessage(player, $"  {key}: floor={floorValue} live={liveValue}");
        }
    }
}
