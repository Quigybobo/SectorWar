using Microsoft.Extensions.ObjectPool;
using SS.SectorWar.Interfaces;
using SS.SectorWar.Items;
using SS.SectorWar.Persist;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System.Threading;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — Inventory subsystem (LARGEST subsystem in the umbrella).
// =============================================================================
//
// PURPOSE
// -------
// The unified SectorWar shop + inventory system. Players buy ItemDefinition's
// (Engines, Shields, WeaponMods, HullPlating) at the shop, store them in a
// per-player Backpack (capacity 50), and equip them per-ship per-slot. Each
// of the 8 ship classes has its own 4-slot loadout. Equip/unequip drives the
// IShipSettings refresh path AND the IGunTurret turret-grant path so that
// equipping a Mk.X WeaponMod immediately:
//   (a) updates the ClientSettings overrides for that player's current ship,
//   (b) (re)spawns the matching twin-wing turrets at the hardpoint offsets.
//
// ON TOP OF the raw inventory plumbing, this subsystem owns the entire
// SectorWar ?menu UI: a SelectBox-based dialog tree the player navigates with
// the arrow keys + Enter + Esc. ?menu opens a TopMenu. From there a player
// can drill into:
//   - Shop (categories: Engines / Shields / Weapons / Hull) with a per-player
//     view filter (All / Affordable / Not owned),
//   - Deployables (lists IDeployableShop offerings — pylon, outpost, warstation),
//   - Inventory (with view filter All / Equipped / Storage; backpack items
//     branch into an "actions" sub-menu for Equip→ShipPicker or Sell),
//   - My Stats (level / xp / credits / prestige; offers the "do prestige"
//     entry once you hit RpgPrestigeRequiredLevel),
//   - Leaderboard (top 10 wealthiest online players),
//   - Casino (4 dice presets — wired via IMoneySinks),
//   - Market (read-only ticker view; trades happen via ?invest / ?divest).
//
// SOURCE
// ------
// Standalone module `Modules/Inventory.cs` stays as a library copy. This is
// the largest single module in the codebase (~1637 lines pre-merge); the
// merge is essentially verbatim with mechanical rename + DI plumbing changes.
//
// CONF SECTION
// ------------
// Inventory has NO conf keys today. BackpackCapacity, MaxItemsPerCategory,
// the SelectBox value ranges (10000+ / 20000+ / 21000+ / 30000+ / 32000+),
// and the persist version are all compile-time constants in this file.
// (See migration note below if Inventory ever grows conf knobs: any new keys
// land in `[SectorWar]` with `Inventory` prefix per the umbrella conf rule.)
//
// PERSISTENCE
// -----------
// PersistKeys.Inventory = 202 / PersistInterval.Forever / PersistScope.Global.
// The 202 key is shared with the standalone Inventory wire format so a save
// produced by either module is readable by the other. Schema versions:
//   v1 = global slot dict (EquipmentSlot → defId). All ships shared one
//        loadout. Auto-migrated on read: each saved entry is duplicated to
//        all 8 ship classes so a v1 loadout becomes a v2 loadout that's
//        consistent across ships.
//   v2 = per-ship slot dict ((ShipType, EquipmentSlot) → defId). Each ship
//        has its own independent 4-slot loadout. Current write format.
// Backpack is a flat List<int> of defIds (not versioned independently of
// the schema header — the version byte gates both halves).
// PersistVersion = 2.
//
// RUNTIME OWNERSHIP
//   - Owned state: per-player Backpack (List<int>), Equipped dict
//                  ((ship,slot)→defId), all lock-protected by InventoryPlayerData.Lock.
//                  Per-player MenuState (TopMenu / Shop / Inventory / …),
//                  per-player view-filter prefs (Inv / Shop), per-player
//                  pendingBackpackIdx for the action sub-menu, all
//                  protected by _inventoryMenuStateLock.
//   - Conf keys read: NONE.
//   - Persisted: yes (Forever/Global, key 202).
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE.
//   - Commands registered (8): cmd_shop, cmd_shopbuy, cmd_inv, cmd_inventory,
//                              cmd_equip, cmd_unequip, cmd_shopsell, cmd_menu.
//   - Broker interfaces published: IInventory.
//   - Broker interfaces consumed (per-call or cached at Load): IEconomy,
//     IShipSettings, IRpg, IMoneySinks, IMarketReader, IGunTurret, IPersist,
//     ISelectBox, IDeployableShop. The umbrella now provides several of
//     those itself (IEconomy, IShipSettings, IRpg, IMoneySinks, IMarketReader,
//     IGunTurret, IDeployableShop) — but we still go through the broker so
//     this partial behaves identically whether the umbrella or a standalone
//     library copy is publishing the interface. Casting `this` would couple
//     the partials and break the parallel-coexistence period.
//
// CALLBACKS HOOKED (zone-wide, not per-arena)
//   - SelectBoxItemSelectedCallback (drives the entire menu tree),
//   - PlayerActionCallback (cleanup on Disconnect/LeaveArena; resync turrets
//     on EnterGame),
//   - ShipFreqChangeCallback (resync turrets when the player's ship class
//     changes, since turret-grants are tied to the WeaponMod equipped on
//     the CURRENT ship).
//
// THREADING
// ---------
// SS.NET dispatches the SelectBox + PlayerAction + ShipFreqChange callbacks
// on the mainloop. The player-data and menu-state locks are uncontended on
// the mainloop, but they're necessary because IPersist.GetData/SetData run
// on a worker thread (PERSIST_THREAD), so the persist code path can race
// with mainloop equip/unequip without locking. The pattern in every
// pd.Lock'd section is "snapshot under lock, send chat outside lock" —
// preserved verbatim from the standalone module.
//
// WAVE-FIXES PRESERVED
// --------------------
// * Wave-10 (CRITICAL): deployable menu values are 30030+ — NOT 30010+.
//   Originally OpenDeployablesValue/BuyDeployPylonValue/etc. lived in the
//   30010..30013 range, which is the SAME range as the shop-categories
//   buttons (ShopEnginesValue=30010, ShopShieldsValue=30011, etc.). Dispatch
//   was gated on the per-player MenuState so it functionally worked, but
//   the constant collision was a maintenance trap waiting to bite anyone
//   who reordered/forgot the state check. The 30030+ renumbering ended
//   that. Searching for 30010 in this file should ONLY return shop-category
//   constants, never deployable constants. KEEP THESE RANGES SEPARATE.
// * Wave-IDeployableShop (Phase 3a): deployables sub-menu reads its
//   offerings list from IDeployableShop.GetOfferings() — the SAME list the
//   actual TryBuy implementation drives off — so prices and descriptions
//   can't drift between the menu and the buy path.
// * v1→v2 persist migration: legacy single-loadout saves are auto-promoted
//   to per-ship loadouts on read by duplicating each entry to all 8 ships.
//   Without this, a Wave-1 player would log in to find their Mk.X gear
//   "missing" because the v2 read path wouldn't find a matching (ship,slot)
//   key in the v1 stream. Logged at Info level for audit.
// * Backpack-full refund on shopbuy: TryBuyItem checks capacity AFTER
//   spending; if backpack is full, the spend is refunded with a "shopbuy
//   refund (backpack full)" reason tag so the audit log pairs cleanly.
// * Stable lock-ordering on equip swap: pd.Lock is taken once and the swap
//   is atomic under that single lock, so we can't deadlock.
// =============================================================================

public sealed partial class SectorWar : IInventory
{
    // -------------------------------------------------------------------------
    // CONSTANTS — all subsystem-prefixed with "Inventory".
    // -------------------------------------------------------------------------

    /// <summary>?shop — text-listing fallback when the player isn't in spec
    /// (the spec-only dialog UI requires the SelectBox channel).</summary>
    private const string InventoryShopCommand = "shop";

    /// <summary>?shopbuy &lt;id&gt; — buy by item id without opening the dialog.</summary>
    private const string InventoryShopBuyCommand = "shopbuy";

    /// <summary>?inv — text-listing of equipped + backpack contents.</summary>
    private const string InventoryInvCommand = "inv";

    /// <summary>?inventory — alias of ?inv (we add both to make the command
    /// discovery story friendlier).</summary>
    private const string InventoryInventoryCommand = "inventory";

    /// <summary>?equip &lt;backpack#&gt; &lt;ship&gt; — text-mode equip.</summary>
    private const string InventoryEquipCommand = "equip";

    /// <summary>?unequip &lt;ship&gt; &lt;slot&gt; — text-mode unequip.</summary>
    private const string InventoryUnequipCommand = "unequip";

    /// <summary>?shopsell &lt;backpack#&gt; — sell at 50% refund.</summary>
    private const string InventoryShopSellCommand = "shopsell";

    /// <summary>?menu — opens the unified SectorWar top-menu dialog.</summary>
    private const string InventoryMenuCommand = "menu";

    // -------------------------------------------------------------------------
    // SELECT-BOX VALUE-ENCODING SCHEME
    //
    // Every SelectBox value is a `short` (-32768..32767). We carve the namespace
    // into ranges so the SelectBoxItemSelected callback can dispatch by range
    // alone (most of the time) and only consult per-player MenuState for the
    // few menus that share value space (e.g. the Inventory menu uses both
    // EquippedBase 20000+ and BackpackBase 10000+ at once).
    //
    // Ranges:
    //   10000..10049  : Backpack indices (BackpackBase + 0..49)
    //   20000..20031  : Equipped slots ((shipIdx * 4 + slotIdx); 8 ships * 4 slots)
    //   21000..21007  : Ship picker (ShipPickerBase + ShipType ordinal)
    //   30000..30099  : Top-level menu nav + sub-menu opens
    //   30100..30199  : Universal close
    //   30200..30299  : Backpack action sub-menu
    //   31000         : InfoItem (header / read-only row)
    //   32001..32004  : Casino dice presets
    //
    // The 30030+ deployables block is intentionally separated from the
    // 30010..30014 shop-categories block (Wave-10 fix; see file header).
    // -------------------------------------------------------------------------

    /// <summary>Universal "go back to top menu" value.</summary>
    private const short InventoryBackToMenuValue = 30000;

    /// <summary>Top menu — opens shop categories.</summary>
    private const short InventoryOpenShopValue = 30001;

    /// <summary>Top menu — opens inventory.</summary>
    private const short InventoryOpenInventoryValue = 30002;

    /// <summary>Top menu — opens stats.</summary>
    private const short InventoryShowStatsValue = 30003;

    /// <summary>Top menu — opens leaderboard.</summary>
    private const short InventoryShowLeaderboardValue = 30004;

    /// <summary>Stats menu — performs prestige (requires level 100).</summary>
    private const short InventoryDoPrestigeValue = 30005;

    /// <summary>Top menu — opens casino dice presets.</summary>
    private const short InventoryOpenCasinoValue = 30006;

    /// <summary>Top menu — opens read-only market view.</summary>
    private const short InventoryOpenMarketValue = 30007;

    // ----- Shop categories sub-menu (30010..30014) ---------------------------

    /// <summary>Shop categories — opens engines.</summary>
    private const short InventoryShopEnginesValue = 30010;

    /// <summary>Shop categories — opens shields.</summary>
    private const short InventoryShopShieldsValue = 30011;

    /// <summary>Shop categories — opens weapon mods.</summary>
    private const short InventoryShopWeaponsValue = 30012;

    /// <summary>Shop categories — opens hull plating.</summary>
    private const short InventoryShopHullValue = 30013;

    /// <summary>"Back to shop categories" from a category dialog.</summary>
    private const short InventoryBackToShopCategoriesValue = 30014;

    // ----- Deployables sub-menu (30030..30033) -------------------------------
    //
    // WAVE-10 CRITICAL: this block is at 30030+, NOT 30010+. The previous
    // numbering collided with InventoryShopEnginesValue and friends and was
    // a maintenance trap even though dispatch was gated by MenuState. Do not
    // renumber back into the 30010..30014 range under any circumstance.
    // -------------------------------------------------------------------------

    /// <summary>Top menu — opens the deployables sub-menu (30030; Wave-10 fix
    /// moved this OUT of the 30010..30013 shop-categories range).</summary>
    private const short InventoryOpenDeployablesValue = 30030;

    /// <summary>Deployables — buy a Pylon (routes through IDeployableShop).</summary>
    private const short InventoryBuyDeployPylonValue = 30031;

    /// <summary>Deployables — buy an Outpost (routes through IDeployableShop).</summary>
    private const short InventoryBuyDeployOutpostValue = 30032;

    /// <summary>Deployables — buy a WarStation (routes through IDeployableShop).</summary>
    private const short InventoryBuyDeployWarStationValue = 30033;

    // ----- Universal close (30100) ------------------------------------------

    /// <summary>Universal "close menu" value — clears MenuState to None.</summary>
    private const short InventoryCloseMenuValue = 30100;

    // ----- Read-only / info row (31000) -------------------------------------

    /// <summary>Read-only info row (header / placeholder). Selecting one
    /// re-opens the current menu so the dialog stays up.</summary>
    private const short InventoryInfoItemValue = 31000;

    // ----- Backpack actions sub-menu (30200..30201) -------------------------

    /// <summary>Backpack actions — equip flow (opens ship picker next).</summary>
    private const short InventoryBackpackEquipValue = 30200;

    /// <summary>Backpack actions — sell flow (50% refund).</summary>
    private const short InventoryBackpackSellValue = 30201;

    // ----- Inventory view-filter (30210..30212) -----------------------------

    /// <summary>Inventory dialog — filter: show all (equipped + storage).</summary>
    private const short InventoryInvViewAllValue = 30210;

    /// <summary>Inventory dialog — filter: show equipped only.</summary>
    private const short InventoryInvViewEquippedValue = 30211;

    /// <summary>Inventory dialog — filter: show storage (backpack) only.</summary>
    private const short InventoryInvViewStorageValue = 30212;

    // ----- Shop view-filter (30220..30222) ----------------------------------

    /// <summary>Shop category dialog — filter: show all items.</summary>
    private const short InventoryShopViewAllValue = 30220;

    /// <summary>Shop category dialog — filter: show only items the player can afford.</summary>
    private const short InventoryShopViewAffordableValue = 30221;

    /// <summary>Shop category dialog — filter: show only items not yet owned.</summary>
    private const short InventoryShopViewNotOwnedValue = 30222;

    // ----- Casino dice presets (32001..32004) -------------------------------

    /// <summary>Casino — bet 100 cr (50/50, win = +90 net).</summary>
    private const short InventoryDiceBet100Value = 32001;

    /// <summary>Casino — bet 1,000 cr (50/50, win = +900 net).</summary>
    private const short InventoryDiceBet1kValue = 32002;

    /// <summary>Casino — bet 10,000 cr (50/50, win = +9,000 net).</summary>
    private const short InventoryDiceBet10kValue = 32003;

    /// <summary>Casino — bet 100,000 cr (50/50, win = +90,000 net).</summary>
    private const short InventoryDiceBet100kValue = 32004;

    // ----- Range bases ------------------------------------------------------

    /// <summary>Backpack value base (BackpackBase + idx, idx in 0..49).</summary>
    private const short InventoryBackpackBase = 10000;

    /// <summary>Equipped-slot value base (Base + shipIdx*4 + slotIdx,
    /// 8 ships * 4 slots = 32 values, 20000..20031).</summary>
    private const short InventoryEquippedBase = 20000;

    /// <summary>Ship-picker value base (Base + ShipType ordinal, 8 ships,
    /// 21000..21007). ShipType.Spec is excluded at fill time.</summary>
    private const short InventoryShipPickerBase = 21000;

    /// <summary>Persist schema version. v1 = global slots; v2 = per-ship slots.
    /// We write v2 today; v1 is auto-migrated on read.</summary>
    private const byte InventoryPersistVersion = 2;

    /// <summary>Backpack capacity. Buying past this point refunds the spend
    /// and sends a "backpack full" message; same with unequip.</summary>
    private const int InventoryBackpackCapacity = 50;

    /// <summary>Hard cap for items shown in a shop category dialog. SelectBox
    /// dialogs have a ~250-row practical limit; 100 keeps each category
    /// well under that with room for filter rows + back row.</summary>
    private const int InventoryMaxItemsPerCategory = 100;

    /// <summary>Required level for ?prestige (and the "do prestige" menu
    /// entry that appears in My Stats once the player hits this level).</summary>
    private const int InventoryPrestigeRequiredLevel = 100;

    // -------------------------------------------------------------------------
    // ENUMS — internal types referenced from menu dispatch.
    // -------------------------------------------------------------------------

    /// <summary>Tracks which menu each player has open so SelectBox callbacks
    /// dispatch correctly and dialogs from other modules are ignored. Values
    /// are mutated under <see cref="_inventoryMenuStateLock"/>.</summary>
    private enum InventoryMenuState
    {
        None, TopMenu, ShopCategories, Shop, Inventory, BackpackItemActions,
        EquipShipPicker, Stats, Leaderboard, Casino, Market, Deployables,
    }

    /// <summary>Inventory dialog view filter (per-player). Defaults to All.</summary>
    private enum InventoryInvView { All, Equipped, Storage }

    /// <summary>Shop category dialog view filter (per-player). Defaults to All.</summary>
    private enum InventoryShopView { All, Affordable, NotOwned }

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    //
    // Every field is `_inventory`-prefixed to avoid collisions with sibling
    // partials. Cached interface handles are released in UnloadInventoryAsync.
    // -------------------------------------------------------------------------

    /// <summary>Cached broker reference. Lazy interface lookups (IDeployableShop,
    /// ISelectBox callbacks dispatched on a different thread, etc.) need a
    /// broker target. Set in LoadInventoryAsync, cleared in UnloadInventoryAsync.</summary>
    private IComponentBroker? _inventoryBroker;

    // ----- Cached broker interfaces -----------------------------------------
    //
    // ISelectBox is a hard dependency (the entire menu UI is built on it).
    // The other interfaces are soft dependencies: missing IRpg means the
    // Stats / Leaderboard / Prestige menus quietly skip; missing IShipSettings
    // means the equip flow doesn't push ClientSettings overrides; missing
    // IGunTurret means WeaponMod equip skips the turret-grant path. None of
    // those failures are user-visible beyond "that part of the menu doesn't
    // do anything" — which is the right graceful-degradation answer per the
    // umbrella's no-crash requirement.
    //
    // We resolve these via broker.GetInterface in Load (not constructor
    // injection) because:
    //   1. The umbrella's constructor is shared across all partials and
    //      can't take per-subsystem services without each partial knowing
    //      everyone else's needs (partial classes get exactly ONE ctor).
    //   2. Several of these interfaces are now PUBLISHED BY THE UMBRELLA
    //      ITSELF (IEconomy, IShipSettings, IRpg, IMoneySinks, IMarketReader,
    //      IGunTurret, IDeployableShop). Going through the broker preserves
    //      the standalone-vs-umbrella indirection so this partial still works
    //      against either provider.
    // -------------------------------------------------------------------------

    private IEconomy? _inventoryEconomy;
    private IShipSettings? _inventoryShipSettings;
    private IRpg? _inventoryRpg;
    private IMoneySinks? _inventoryMoneySinks;
    private IMarketReader? _inventoryMarketReader;
    private IGunTurret? _inventoryGunTurret;
    private ISelectBox? _inventorySelectBox;
    private IPersist? _inventoryPersist;

    /// <summary>The DelegatePersistentData<Player> registration we passed to
    /// IPersist. Keeping a reference around so we can pass it back to
    /// UnregisterPersistentDataAsync at unload.</summary>
    private DelegatePersistentData<Player>? _inventoryPersistRegistration;

    /// <summary>Token returned by RegisterInterface<IInventory> so we can
    /// cleanly unregister at unload.</summary>
    private InterfaceRegistrationToken<IInventory>? _inventoryToken;

    /// <summary>Per-arena player-extra-data slot key for InventoryPlayerData.</summary>
    private PlayerDataKey<InventoryPlayerData> _inventoryPdKey;

    // ----- Per-player UI state ----------------------------------------------
    //
    // These four dictionaries describe transient UI state — what menu the
    // player has open, what filter they last picked, which backpack item
    // they're acting on. All four are protected by the same lock so we don't
    // need fine-grained ordering (the lock is uncontended on the mainloop).
    // Cleared on Disconnect/LeaveArena via the PlayerActionCallback.
    // -------------------------------------------------------------------------

    /// <summary>Lock protecting the four per-player UI dictionaries below.</summary>
    private readonly Lock _inventoryMenuStateLock = new();

    /// <summary>Player → which menu is currently open. Missing key == None.</summary>
    private readonly Dictionary<Player, InventoryMenuState> _inventoryMenuState = new();

    /// <summary>Player → which backpack index they're currently acting on
    /// (used by BackpackItemActions and EquipShipPicker states).</summary>
    private readonly Dictionary<Player, int> _inventoryPendingBackpackIdx = new();

    /// <summary>Player → inventory dialog filter preference. Missing == All.</summary>
    private readonly Dictionary<Player, InventoryInvView> _inventoryInvView = new();

    /// <summary>Player → shop category dialog filter preference. Missing == All.</summary>
    private readonly Dictionary<Player, InventoryShopView> _inventoryShopView = new();

    /// <summary>Player → currently-open shop category, so view-filter clicks
    /// can re-open the same category with the new filter applied.</summary>
    private readonly Dictionary<Player, EquipmentSlot> _inventoryShopCategory = new();

    // -------------------------------------------------------------------------
    // PER-PLAYER PERSISTENT DATA
    //
    // Lives in the player's extra-data slot keyed by _inventoryPdKey. Backpack
    // is a flat list of defIds; Equipped is a per-(ship,slot) defId map.
    // IResettable.TryReset is called by SS.NET when the slot is recycled
    // (player disconnect with extra-data return-to-pool), and clears both
    // dictionaries to known-empty.
    // -------------------------------------------------------------------------

    /// <summary>Per-player Inventory state (backpack + per-ship loadout).</summary>
    private sealed class InventoryPlayerData : IResettable
    {
        /// <summary>Storage backpack — flat list of ItemDefinition.Id values.
        /// Order matters for ?inv slot numbers and for menu dispatch.</summary>
        public List<int> Backpack = new();

        /// <summary>Equipped items keyed by (ship, slot) → defId. Each ship
        /// owns its own 4-slot loadout.</summary>
        public Dictionary<(ShipType Ship, EquipmentSlot Slot), int> Equipped = new();

        /// <summary>Lock protecting Backpack + Equipped. Acquired on every
        /// read and write because IPersist may run on a worker thread.</summary>
        public readonly Lock Lock = new();

        bool IResettable.TryReset()
        {
            lock (Lock)
            {
                Backpack.Clear();
                Equipped.Clear();
            }
            return true;
        }
    }

    // -------------------------------------------------------------------------
    // ASYNC LOAD / UNLOAD
    //
    // Async because IPersist.RegisterPersistentDataAsync is awaited. The
    // umbrella's IAsyncModule.LoadAsync awaits us in turn.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Subsystem load hook called from the umbrella's IAsyncModule.LoadAsync.
    /// Allocates per-player data, resolves broker interfaces, registers the
    /// IPersist delegate and IInventory broker interface, hooks SelectBox /
    /// PlayerAction / ShipFreqChange callbacks, and adds 8 chat commands.
    /// </summary>
    /// <remarks>
    /// Failure mode: if IEconomy is unavailable the subsystem cannot function
    /// (the entire shop is credit-gated), so we log Error and bail without
    /// hooking anything else. The umbrella will still load; ?menu / ?shop /
    /// ?inv simply won't be available. This matches the standalone module's
    /// graceful-degradation behavior. Same idea for IPersist (Warn — items
    /// just won't survive restart) and IGunTurret (Warn — turret-grant items
    /// won't generate turrets, but other items still equip fine).
    /// </remarks>
    private async Task LoadInventoryAsync(IComponentBroker broker, CancellationToken ct)
    {
        _inventoryBroker = broker;
        _inventoryPdKey = _playerData.AllocatePlayerData<InventoryPlayerData>();

        // Resolve all broker interfaces. Several of these are now provided
        // by sibling partials of THIS class; the broker indirection makes
        // that transparent. Released in UnloadInventoryAsync.
        _inventoryEconomy = broker.GetInterface<IEconomy>();
        _inventoryShipSettings = broker.GetInterface<IShipSettings>();
        _inventoryRpg = broker.GetInterface<IRpg>();
        _inventoryMoneySinks = broker.GetInterface<IMoneySinks>();
        _inventoryMarketReader = broker.GetInterface<IMarketReader>();
        _inventoryGunTurret = broker.GetInterface<IGunTurret>();
        _inventorySelectBox = broker.GetInterface<ISelectBox>();

        if (_inventoryGunTurret is null)
            _logManager.LogM(LogLevel.Warn, LogCategory,
                "Inventory: IGunTurret not available — turret-grant items won't generate turrets. Check load order.");

        if (_inventorySelectBox is null)
            _logManager.LogM(LogLevel.Warn, LogCategory,
                "Inventory: ISelectBox not available — ?menu and dialog-mode shop will not function.");

        if (_inventoryEconomy is null)
        {
            _logManager.LogM(LogLevel.Error, LogCategory,
                "Inventory: IEconomy unavailable — Inventory cannot function (no shop, no sell, no buy).");
            return;
        }

        // Register persistence (PerPlayer / Forever / Global). Persist key 202
        // is shared with the standalone Inventory module; saves are mutually
        // readable as long as the schema versions are compatible (currently
        // both write v2 and both can migrate v1).
        _inventoryPersist = broker.GetInterface<IPersist>();
        if (_inventoryPersist is not null)
        {
            _inventoryPersistRegistration = new DelegatePersistentData<Player>(
                PersistKeys.Inventory,
                PersistInterval.Forever,
                PersistScope.Global,
                Persist_Inventory_GetData,
                Persist_Inventory_SetData,
                Persist_Inventory_ClearData);

            await _inventoryPersist.RegisterPersistentDataAsync(_inventoryPersistRegistration);
        }
        else
        {
            _logManager.LogM(LogLevel.Warn, LogCategory,
                "Inventory: IPersist not available — backpack and loadouts will not persist across restarts.");
        }

        // Publish IInventory so other partials and external modules can read
        // a player's equipped loadout (e.g. ShipSettings reads modifiers from
        // GetEquippedForShip when a player picks a ship).
        _inventoryToken = broker.RegisterInterface<IInventory>(this);

        // Callback subscriptions. These are zone-wide, not per-arena, so they
        // live in Load (not Attach).
        SelectBoxItemSelectedCallback.Register(broker, OnSelectBoxItemSelected_Inventory);
        PlayerActionCallback.Register(broker, OnPlayerAction_Inventory);
        ShipFreqChangeCallback.Register(broker, OnShipFreqChange_Inventory);

        _logManager.LogM(LogLevel.Info, LogCategory,
            $"Inventory subsystem loaded with {ItemCatalog.All.Length} items in catalog.");
    }

    /// <summary>
    /// Subsystem unload hook called from the umbrella's IAsyncModule.UnloadAsync.
    /// Reverse of Load: unhook callbacks, unregister IInventory + IPersist,
    /// remove commands, release broker interfaces, free per-player data.
    /// </summary>
    private async Task UnloadInventoryAsync(IComponentBroker broker, CancellationToken ct)
    {
        SelectBoxItemSelectedCallback.Unregister(broker, OnSelectBoxItemSelected_Inventory);
        PlayerActionCallback.Unregister(broker, OnPlayerAction_Inventory);
        ShipFreqChangeCallback.Unregister(broker, OnShipFreqChange_Inventory);

        if (_inventoryToken is not null)
            broker.UnregisterInterface(ref _inventoryToken);

        if (_inventoryPersist is not null && _inventoryPersistRegistration is not null)
        {
            await _inventoryPersist.UnregisterPersistentDataAsync(_inventoryPersistRegistration);
            _inventoryPersistRegistration = null;
        }

        // Release broker interfaces in reverse-resolve order.
        if (_inventoryPersist is not null) broker.ReleaseInterface(ref _inventoryPersist);
        if (_inventorySelectBox is not null) broker.ReleaseInterface(ref _inventorySelectBox);
        if (_inventoryGunTurret is not null) broker.ReleaseInterface(ref _inventoryGunTurret);
        if (_inventoryMarketReader is not null) broker.ReleaseInterface(ref _inventoryMarketReader);
        if (_inventoryMoneySinks is not null) broker.ReleaseInterface(ref _inventoryMoneySinks);
        if (_inventoryRpg is not null) broker.ReleaseInterface(ref _inventoryRpg);
        if (_inventoryShipSettings is not null) broker.ReleaseInterface(ref _inventoryShipSettings);
        if (_inventoryEconomy is not null) broker.ReleaseInterface(ref _inventoryEconomy);

        _playerData.FreePlayerData(ref _inventoryPdKey);
        _inventoryBroker = null;
    }

    /// <summary>Per-arena attach: register all 8 shop/inventory commands at
    /// arena scope so they only surface in arenas where SectorWar is attached.</summary>
    private void AttachInventory(Arena arena)
    {
        _commandManager.AddCommand(InventoryShopCommand, Command_InventoryShop, arena);
        _commandManager.AddCommand(InventoryShopBuyCommand, Command_InventoryShopBuy, arena);
        _commandManager.AddCommand(InventoryInvCommand, Command_InventoryInv, arena);
        _commandManager.AddCommand(InventoryInventoryCommand, Command_InventoryInv, arena);
        _commandManager.AddCommand(InventoryEquipCommand, Command_InventoryEquip, arena);
        _commandManager.AddCommand(InventoryUnequipCommand, Command_InventoryUnequip, arena);
        _commandManager.AddCommand(InventoryShopSellCommand, Command_InventoryShopSell, arena);
        _commandManager.AddCommand(InventoryMenuCommand, Command_InventoryMenu, arena);
    }

    /// <summary>Per-arena detach: reverse the AddCommand calls.</summary>
    private void DetachInventory(Arena arena)
    {
        _commandManager.RemoveCommand(InventoryShopCommand, Command_InventoryShop, arena);
        _commandManager.RemoveCommand(InventoryShopBuyCommand, Command_InventoryShopBuy, arena);
        _commandManager.RemoveCommand(InventoryInvCommand, Command_InventoryInv, arena);
        _commandManager.RemoveCommand(InventoryInventoryCommand, Command_InventoryInv, arena);
        _commandManager.RemoveCommand(InventoryEquipCommand, Command_InventoryEquip, arena);
        _commandManager.RemoveCommand(InventoryUnequipCommand, Command_InventoryUnequip, arena);
        _commandManager.RemoveCommand(InventoryShopSellCommand, Command_InventoryShopSell, arena);
        _commandManager.RemoveCommand(InventoryMenuCommand, Command_InventoryMenu, arena);
    }

    // -------------------------------------------------------------------------
    // IInventory IMPLEMENTATION
    //
    // Public-surface broker methods. These are read-only views into a player's
    // current loadout. ShipSettings calls GetEquippedForShip to compute the
    // ClientSettings overrides when a player picks a ship.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the <see cref="ItemDefinition"/> currently equipped in the given
    /// (ship, slot) pair, or <c>null</c> if nothing is equipped or the def-id
    /// is unknown to the catalog (stale save).
    /// </summary>
    ItemDefinition? IInventory.GetEquipped(Player player, ShipType ship, EquipmentSlot slot)
    {
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return null;
        lock (pd.Lock)
        {
            return pd.Equipped.TryGetValue((ship, slot), out int defId)
                ? ItemCatalog.Find(defId)
                : null;
        }
    }

    /// <summary>
    /// Returns every item equipped on the given ship across all 4 slots.
    /// Returned list is a snapshot — safe to enumerate after the lock releases.
    /// </summary>
    IReadOnlyList<ItemDefinition> IInventory.GetEquippedForShip(Player player, ShipType ship)
    {
        var result = new List<ItemDefinition>(4);
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return result;

        lock (pd.Lock)
        {
            foreach (var ((sh, _), defId) in pd.Equipped)
            {
                if (sh != ship) continue;
                var item = ItemCatalog.Find(defId);
                if (item is not null) result.Add(item);
            }
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // TURRET SYNC HELPER
    //
    // Twin-wing turrets are spawned for whichever WeaponMod is equipped on the
    // player's CURRENT ship. Called after every equip/unequip of WeaponMod and
    // on ship change. Idempotent — always tears down all current turrets first
    // so the live state is purely a function of the current loadout.
    // -------------------------------------------------------------------------

    private void SyncTurretsForCurrentShip_Inventory(Player player)
    {
        if (_inventoryGunTurret is null) return;
        if (player.Ship == ShipType.Spec)
        {
            _inventoryGunTurret.RemoveAllTurrets(player);
            return;
        }
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        // Always start clean — turret state is purely a function of the
        // current ship's WeaponMod equip slot, not history.
        _inventoryGunTurret.RemoveAllTurrets(player);

        ShipType ship = player.Ship;
        ItemDefinition? wmod;
        lock (pd.Lock)
        {
            wmod = pd.Equipped.TryGetValue((ship, EquipmentSlot.WeaponMod), out int defId)
                ? ItemCatalog.Find(defId)
                : null;
        }
        if (wmod?.Grant is null) return;

        // Twin-wing turrets: matching ship class, both wings.
        var grant = wmod.Grant;
        (int lx, int ly) = Hardpoints.Offset(ship, Hardpoint.LeftWing);
        (int rx, int ry) = Hardpoints.Offset(ship, Hardpoint.RightWing);

        var leftInfo = new GunTurretInfo($"{ship}-L", ship, lx, ly, 0, grant.Weapon, grant.Level, grant.AutoFire);
        var rightInfo = new GunTurretInfo($"{ship}-R", ship, rx, ry, 0, grant.Weapon, grant.Level, grant.AutoFire);
        _inventoryGunTurret.AddTurret(player, leftInfo);
        _inventoryGunTurret.AddTurret(player, rightInfo);
    }

    /// <summary>Buy gating: shop usage requires Spec mode (player can't be
    /// flying). Sends a hint to the player on rejection.</summary>
    private bool RequireSpecForInventory(Player player)
    {
        if (player.Ship == ShipType.Spec) return true;
        _chat.SendMessage(player, "Spectate first to use the shop. (Press Esc → Spectator)");
        return false;
    }

    // -------------------------------------------------------------------------
    // MENU STATE HELPERS
    // -------------------------------------------------------------------------

    /// <summary>Sets the player's menu state. None clears the pending-backpack
    /// index; non-None overwrites the current state.</summary>
    private void SetInventoryMenuState(Player player, InventoryMenuState state)
    {
        lock (_inventoryMenuStateLock)
        {
            if (state == InventoryMenuState.None)
            {
                _inventoryMenuState.Remove(player);
                _inventoryPendingBackpackIdx.Remove(player);
            }
            else
            {
                _inventoryMenuState[player] = state;
            }
        }
    }

    /// <summary>Returns the player's current menu state, or None if no menu
    /// is open. Thread-safe.</summary>
    private InventoryMenuState GetInventoryMenuState(Player player)
    {
        lock (_inventoryMenuStateLock)
        {
            return _inventoryMenuState.TryGetValue(player, out InventoryMenuState s)
                ? s
                : InventoryMenuState.None;
        }
    }

    // -------------------------------------------------------------------------
    // DIALOG OPENERS — each method builds a SelectBoxItem list and opens
    // a dialog via _inventorySelectBox.Open. Dialog dispatch happens in
    // OnSelectBoxItemSelected_Inventory based on the per-player MenuState.
    // -------------------------------------------------------------------------

    /// <summary>Top-level SectorWar menu (?menu).</summary>
    private void OpenInventoryTopMenu(Player player)
    {
        if (_inventorySelectBox is null) return;
        var items = new List<SelectBoxItem>
        {
            new SelectBoxItem(InventoryOpenShopValue,        "Shop".AsMemory()),
            new SelectBoxItem(InventoryOpenDeployablesValue, "Deployables (Pylons / Structures)".AsMemory()),
            new SelectBoxItem(InventoryOpenInventoryValue,   "Inventory".AsMemory()),
            new SelectBoxItem(InventoryShowStatsValue,       "My Stats".AsMemory()),
            new SelectBoxItem(InventoryShowLeaderboardValue, "Leaderboard".AsMemory()),
            new SelectBoxItem(InventoryOpenCasinoValue,      "Casino".AsMemory()),
            new SelectBoxItem(InventoryOpenMarketValue,      "Market".AsMemory()),
            new SelectBoxItem(InventoryCloseMenuValue,       "Close menu".AsMemory()),
        };
        SetInventoryMenuState(player, InventoryMenuState.TopMenu);
        _inventorySelectBox.Open(player, "SectorWar Menu", items);
    }

    /// <summary>
    /// Phase 3a — opens the Deployables sub-menu. Each item routes through
    /// IDeployableShop.TryBuy via the broker. Offerings (kind, price,
    /// description) come from IDeployableShop.GetOfferings so prices stay
    /// in sync with the shop's actual TryBuy implementation.
    /// </summary>
    private void OpenInventoryDeployablesDialog(Player player)
    {
        if (_inventorySelectBox is null) return;
        long balance = _inventoryEconomy?.GetBalance(player) ?? 0;
        var items = new List<SelectBoxItem>
        {
            new SelectBoxItem(InventoryInfoItemValue, $"-- Balance: {balance:N0} cr --".AsMemory()),
        };

        // Query the live shop for offerings. Falls back to a "shop offline"
        // info row if IDeployableShop isn't available.
        IReadOnlyList<DeployableOffering>? offerings = null;
        if (_inventoryBroker is not null)
        {
            IDeployableShop? shop = _inventoryBroker.GetInterface<IDeployableShop>();
            try { offerings = shop?.GetOfferings(); }
            finally { if (shop is not null) _inventoryBroker.ReleaseInterface(ref shop); }
        }

        if (offerings is null || offerings.Count == 0)
        {
            items.Add(new SelectBoxItem(InventoryInfoItemValue, "Deployable shop unavailable.".AsMemory()));
        }
        else
        {
            foreach (var o in offerings)
            {
                // WAVE-10 CRITICAL: each kind dispatches to a value in the
                // 30030+ block. Do NOT renumber back into the 30010..30013
                // range — those collide with the shop-categories buttons.
                short itemValue = o.Kind switch
                {
                    "pylon" => InventoryBuyDeployPylonValue,
                    "outpost" => InventoryBuyDeployOutpostValue,
                    "warstation" => InventoryBuyDeployWarStationValue,
                    _ => InventoryInfoItemValue,
                };
                if (itemValue == InventoryInfoItemValue) continue;       // unknown kind — skip
                items.Add(new SelectBoxItem(itemValue,
                    $"{o.DisplayName} ({o.Cost:N0} cr) - {o.Description}".AsMemory()));
            }
        }

        items.Add(new SelectBoxItem(InventoryBackToMenuValue, "<- Back to menu".AsMemory()));
        SetInventoryMenuState(player, InventoryMenuState.Deployables);
        _inventorySelectBox.Open(player, "Deployables", items);
    }

    /// <summary>Shop categories sub-menu (Engines / Shields / Weapons / Hull).</summary>
    private void OpenInventoryShopCategoriesDialog(Player player)
    {
        if (_inventorySelectBox is null) return;
        var items = new List<SelectBoxItem>
        {
            new SelectBoxItem(InventoryShopEnginesValue, "Engines (Mk.1 - Mk.100)".AsMemory()),
            new SelectBoxItem(InventoryShopShieldsValue, "Shields (Mk.1 - Mk.100)".AsMemory()),
            new SelectBoxItem(InventoryShopWeaponsValue, "Bullets (Mk.1 - Mk.100)".AsMemory()),
            new SelectBoxItem(InventoryShopHullValue,    "Hull (Mk.1 - Mk.100)".AsMemory()),
            new SelectBoxItem(InventoryBackToMenuValue,  "<- Back to menu".AsMemory()),
        };
        SetInventoryMenuState(player, InventoryMenuState.ShopCategories);
        _inventorySelectBox.Open(player, "Shop — pick a category", items);
    }

    /// <summary>Shop category dialog (e.g. all 100 Engine items, optionally
    /// filtered by Affordable / NotOwned).</summary>
    private void OpenInventoryShopCategoryDialog(Player player, EquipmentSlot slot)
    {
        if (_inventorySelectBox is null) return;
        // Remember current category so view-filter clicks (Affordable/NotOwned/All) can refresh.
        lock (_inventoryMenuStateLock) { _inventoryShopCategory[player] = slot; }

        InventoryShopView view;
        long credits = 0;
        HashSet<int> ownedDefIds = new();
        lock (_inventoryMenuStateLock)
        {
            view = _inventoryShopView.TryGetValue(player, out InventoryShopView v) ? v : InventoryShopView.All;
        }
        if (_inventoryEconomy is not null)
        {
            credits = _inventoryEconomy.GetBalance(player);
        }
        if (player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd))
        {
            lock (pd.Lock)
            {
                foreach (int id in pd.Backpack) ownedDefIds.Add(id);
                foreach (int id in pd.Equipped.Values) ownedDefIds.Add(id);
            }
        }

        var allItems = ItemCatalog.ForSlot(slot).ToList();
        int totalCount = allItems.Count;
        int affordableCount = allItems.Count(d => d.Cost <= credits);
        int notOwnedCount = allItems.Count(d => !ownedDefIds.Contains(d.Id));

        var items = new List<SelectBoxItem>(InventoryMaxItemsPerCategory + 6);

        // View-filter rows.
        items.Add(new SelectBoxItem(InventoryShopViewAllValue,
            $"{(view == InventoryShopView.All ? "[*]" : "[ ]")} View: All ({totalCount} items)".AsMemory()));
        items.Add(new SelectBoxItem(InventoryShopViewAffordableValue,
            $"{(view == InventoryShopView.Affordable ? "[*]" : "[ ]")} View: Affordable only ({affordableCount} - balance {credits} cr)".AsMemory()));
        items.Add(new SelectBoxItem(InventoryShopViewNotOwnedValue,
            $"{(view == InventoryShopView.NotOwned ? "[*]" : "[ ]")} View: Not owned yet ({notOwnedCount})".AsMemory()));

        items.Add(new SelectBoxItem(InventoryInfoItemValue, $"=== {slot} ===".AsMemory()));

        int shown = 0;
        foreach (var def in allItems)
        {
            if (view == InventoryShopView.Affordable && def.Cost > credits) continue;
            if (view == InventoryShopView.NotOwned && ownedDefIds.Contains(def.Id)) continue;

            string ownedTag = ownedDefIds.Contains(def.Id) ? " [OWNED]" : "";
            string affordTag = def.Cost > credits ? " [need more cr]" : "";
            string text = $"{def.DisplayName} - {def.Cost} cr{ownedTag}{affordTag}";
            if (text.Length > 100) text = text[..100];
            // Item dispatch by raw def.Id (cast to short). def.Id ranges live
            // in the catalog's allocated id space; they do NOT collide with
            // our 10000+/20000+/30000+ menu-value ranges.
            items.Add(new SelectBoxItem((short)def.Id, text.AsMemory()));
            shown++;
        }
        if (shown == 0)
        {
            items.Add(new SelectBoxItem(InventoryInfoItemValue, "  (no items match this filter)".AsMemory()));
        }

        items.Add(new SelectBoxItem(InventoryBackToShopCategoriesValue, "<- Back to shop".AsMemory()));

        SetInventoryMenuState(player, InventoryMenuState.Shop);
        string viewLabel = view switch
        {
            InventoryShopView.Affordable => "Affordable",
            InventoryShopView.NotOwned => "Not owned",
            _ => "All",
        };
        _inventorySelectBox.Open(player, $"{slot} - {viewLabel} (must be in spec to buy)", items);
    }

    /// <summary>Casino dice presets dialog (4 bet sizes; routes through
    /// IMoneySinks.TryPlayDice on selection).</summary>
    private void OpenInventoryCasinoDialog(Player player)
    {
        if (_inventorySelectBox is null) return;
        if (_inventoryMoneySinks is null) return;
        long jackpot = _inventoryMoneySinks.GetJackpot();
        long balance = _inventoryEconomy?.GetBalance(player) ?? 0;

        var items = new List<SelectBoxItem>
        {
            new SelectBoxItem(InventoryInfoItemValue,    $"Jackpot pool: {jackpot} cr".AsMemory()),
            new SelectBoxItem(InventoryInfoItemValue,    $"Your balance: {balance} cr".AsMemory()),
            new SelectBoxItem(InventoryDiceBet100Value,  "Dice 100 cr (50/50, win = +90)".AsMemory()),
            new SelectBoxItem(InventoryDiceBet1kValue,   "Dice 1,000 cr (win = +900)".AsMemory()),
            new SelectBoxItem(InventoryDiceBet10kValue,  "Dice 10,000 cr (win = +9,000)".AsMemory()),
            new SelectBoxItem(InventoryDiceBet100kValue, "Dice 100,000 cr (win = +90,000)".AsMemory()),
            new SelectBoxItem(InventoryBackToMenuValue,  "<- Back to menu".AsMemory()),
        };
        SetInventoryMenuState(player, InventoryMenuState.Casino);
        _inventorySelectBox.Open(player, "Casino", items);
    }

    /// <summary>Read-only market view (current ticker prices + own holdings).
    /// Trades happen via ?invest / ?divest, not from this dialog.</summary>
    private void OpenInventoryMarketDialog(Player player)
    {
        if (_inventorySelectBox is null) return;
        if (_inventoryMarketReader is null) return;

        var tickers = _inventoryMarketReader.GetTickers();
        var holdings = _inventoryMarketReader.GetHoldings(player);

        var items = new List<SelectBoxItem>();
        foreach (var t in tickers)
        {
            holdings.TryGetValue(t.Symbol, out long owned);
            string text = owned > 0
                ? $"{t.Symbol}: buy {t.Ask} / sell {t.Bid} (own {owned})"
                : $"{t.Symbol}: buy {t.Ask} / sell {t.Bid}";
            if (text.Length > 80) text = text[..80];
            items.Add(new SelectBoxItem(InventoryInfoItemValue, text.AsMemory()));
        }

        items.Add(new SelectBoxItem(InventoryInfoItemValue,
            "(use ?invest <sym> <qty> to buy, ?divest to sell)".AsMemory()));
        items.Add(new SelectBoxItem(InventoryBackToMenuValue, "<- Back to menu".AsMemory()));

        SetInventoryMenuState(player, InventoryMenuState.Market);
        _inventorySelectBox.Open(player, "Market — current prices & your holdings", items);
    }

    /// <summary>Run a dice bet from the casino menu. Bets are validated by
    /// IMoneySinks (which also enforces "can't bet more than balance").</summary>
    private void TryInventoryDiceFromMenu(Player player, long amount)
    {
        if (_inventoryMoneySinks is null) return;
        if (!_inventoryMoneySinks.TryPlayDice(player, amount, out bool win, out long delta))
        {
            long bal = _inventoryEconomy?.GetBalance(player) ?? 0;
            _chat.SendMessage(player, $"You only have {bal} cr — can't bet {amount}.");
            return;
        }
        long balanceAfter = _inventoryEconomy?.GetBalance(player) ?? 0;
        _chat.SendMessage(player,
            win ? $"Dice: WIN. +{delta} cr profit. Balance: {balanceAfter}"
                : $"Dice: LOSE. {delta} cr. Balance: {balanceAfter}");
    }

    /// <summary>My Stats dialog: level, XP, credits, prestige tier. Offers
    /// the do-prestige entry once level >= InventoryPrestigeRequiredLevel.</summary>
    private void OpenInventoryStatsDialog(Player player)
    {
        if (_inventorySelectBox is null) return;
        if (_inventoryRpg is null || _inventoryEconomy is null) return;
        if (!_inventoryRpg.TryGetStats(player, out int level, out long xp, out int prestigeTier))
        {
            _chat.SendMessage(player, "No stats yet.");
            return;
        }
        long credits = _inventoryEconomy.GetBalance(player);

        var items = new List<SelectBoxItem>
        {
            new SelectBoxItem(InventoryInfoItemValue, $"Level: {level}".AsMemory()),
            new SelectBoxItem(InventoryInfoItemValue, $"XP: {xp}".AsMemory()),
            new SelectBoxItem(InventoryInfoItemValue, $"Credits: {credits} cr".AsMemory()),
        };

        if (prestigeTier > 0)
            items.Add(new SelectBoxItem(InventoryInfoItemValue,
                $"Prestige: *{prestigeTier} (+{prestigeTier * 10}% XP/credit gains)".AsMemory()));
        else
            items.Add(new SelectBoxItem(InventoryInfoItemValue,
                $"Prestige: not yet (requires level {InventoryPrestigeRequiredLevel})".AsMemory()));

        if (level >= InventoryPrestigeRequiredLevel)
            items.Add(new SelectBoxItem(InventoryDoPrestigeValue,
                "*** Prestige now (resets level, +10% gains permanent) ***".AsMemory()));

        items.Add(new SelectBoxItem(InventoryBackToMenuValue, "<- Back to menu".AsMemory()));

        SetInventoryMenuState(player, InventoryMenuState.Stats);
        _inventorySelectBox.Open(player, "My Stats", items);
    }

    /// <summary>Top 10 wealthiest online players. Reads IEconomy balances
    /// under the playerData lock for a stable enumeration.</summary>
    private void OpenInventoryLeaderboardDialog(Player player)
    {
        if (_inventorySelectBox is null) return;
        if (_inventoryEconomy is null) return;

        var rows = new List<(string Name, long Credits)>();
        _playerData.Lock();
        try
        {
            foreach (Player p in _playerData.Players)
            {
                if (p.Status != PlayerState.Playing) continue;
                if (p.Type == ClientType.Fake) continue;   // hide HQ defenders / capital / pylons / turrets
                long bal = _inventoryEconomy.GetBalance(p);
                rows.Add((p.Name ?? "?", bal));
            }
        }
        finally { _playerData.Unlock(); }

        rows.Sort((a, b) => b.Credits.CompareTo(a.Credits));

        var items = new List<SelectBoxItem>();
        int rank = 1;
        foreach (var (name, credits) in rows.Take(10))
        {
            string text = $"{rank}. {name} — {credits} cr";
            if (text.Length > 120) text = text[..120];
            items.Add(new SelectBoxItem(InventoryInfoItemValue, text.AsMemory()));
            rank++;
        }

        if (items.Count == 0)
            items.Add(new SelectBoxItem(InventoryInfoItemValue, "(no online players to rank)".AsMemory()));

        items.Add(new SelectBoxItem(InventoryBackToMenuValue, "<- Back to menu".AsMemory()));

        SetInventoryMenuState(player, InventoryMenuState.Leaderboard);
        _inventorySelectBox.Open(player, "Top 10 Wealthiest Online", items);
    }

    /// <summary>Performs prestige via IRpg. Failure path sends the failure
    /// reason from IRpg.TryPrestige to the player; success broadcast happens
    /// inside IRpg.TryPrestige itself.</summary>
    private void DoInventoryPrestige(Player player)
    {
        if (_inventoryRpg is null) return;
        if (!_inventoryRpg.TryPrestige(player, out int newTier, out string failureReason))
        {
            _chat.SendMessage(player, failureReason);
            return;
        }
        // Success message is broadcast inside TryPrestige.
    }

    /// <summary>Single-page shop dialog (legacy fallback — categories dialog
    /// is the modern entry). Lists every catalog item with a 1-line summary.</summary>
    private void OpenInventoryShopDialog(Player player)
    {
        if (_inventorySelectBox is null) return;
        var items = new List<SelectBoxItem>(ItemCatalog.All.Length + 1);
        foreach (var def in ItemCatalog.All)
        {
            string effect = SummarizeInventoryModifiers(def);
            string text = $"{def.DisplayName} - {def.Cost} cr ({effect})";
            if (text.Length > 120) text = text[..120];
            items.Add(new SelectBoxItem((short)def.Id, text.AsMemory()));
        }
        items.Add(new SelectBoxItem(InventoryBackToMenuValue, "<- Back to menu".AsMemory()));

        SetInventoryMenuState(player, InventoryMenuState.Shop);
        _inventorySelectBox.Open(player, "Shop — Enter to buy (must be in spec)", items);
    }

    /// <summary>Inventory dialog. Two sections under a view-filter row:
    /// EQUIPPED (collapsible by ship) and STORAGE (backpack list).</summary>
    private void OpenInventoryDialog(Player player)
    {
        if (_inventorySelectBox is null) return;
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        InventoryInvView view;
        lock (_inventoryMenuStateLock)
        {
            view = _inventoryInvView.TryGetValue(player, out InventoryInvView v) ? v : InventoryInvView.All;
        }

        var items = new List<SelectBoxItem>();

        Dictionary<(ShipType, EquipmentSlot), int> equipSnap;
        List<int> backpackSnap;
        lock (pd.Lock)
        {
            equipSnap = new Dictionary<(ShipType, EquipmentSlot), int>(pd.Equipped);
            backpackSnap = new List<int>(pd.Backpack);
        }

        // View-filter selector at top: shows current selection with [*] marker.
        items.Add(new SelectBoxItem(InventoryInvViewAllValue,
            $"{(view == InventoryInvView.All ? "[*]" : "[ ]")} View: All ({equipSnap.Count} equipped, {backpackSnap.Count} stored)".AsMemory()));
        items.Add(new SelectBoxItem(InventoryInvViewEquippedValue,
            $"{(view == InventoryInvView.Equipped ? "[*]" : "[ ]")} View: Equipped only ({equipSnap.Count})".AsMemory()));
        items.Add(new SelectBoxItem(InventoryInvViewStorageValue,
            $"{(view == InventoryInvView.Storage ? "[*]" : "[ ]")} View: Storage only ({backpackSnap.Count} / {InventoryBackpackCapacity})".AsMemory()));

        // === EQUIPPED section ===
        if (view == InventoryInvView.All || view == InventoryInvView.Equipped)
        {
            items.Add(new SelectBoxItem(InventoryInfoItemValue, $"=== EQUIPPED ({equipSnap.Count}) ===".AsMemory()));

            bool anyEquipped = false;
            foreach (ShipType ship in Enum.GetValues<ShipType>())
            {
                if (ship == ShipType.Spec) continue;
                foreach (EquipmentSlot slot in Enum.GetValues<EquipmentSlot>())
                {
                    if (!equipSnap.TryGetValue((ship, slot), out int defId)) continue;
                    var def = ItemCatalog.Find(defId);
                    if (def is null) continue;

                    anyEquipped = true;
                    short value = (short)(InventoryEquippedBase + (int)ship * 4 + (int)slot);
                    string text = $"  [{ship}.{slot}] {def.DisplayName}  (Enter to unequip)";
                    if (text.Length > 120) text = text[..120];
                    items.Add(new SelectBoxItem(value, text.AsMemory()));
                }
            }
            if (!anyEquipped)
            {
                items.Add(new SelectBoxItem(InventoryInfoItemValue, "  (none equipped on any ship)".AsMemory()));
            }
        }

        // === STORAGE section ===
        if (view == InventoryInvView.All || view == InventoryInvView.Storage)
        {
            items.Add(new SelectBoxItem(InventoryInfoItemValue,
                $"=== STORAGE / BACKPACK ({backpackSnap.Count} / {InventoryBackpackCapacity}) ===".AsMemory()));

            if (backpackSnap.Count == 0)
            {
                items.Add(new SelectBoxItem(InventoryInfoItemValue, "  (storage is empty - buy items at the Shop)".AsMemory()));
            }
            else
            {
                for (int i = 0; i < backpackSnap.Count && i < 50; i++)
                {
                    var def = ItemCatalog.Find(backpackSnap[i]);
                    if (def is null) continue;
                    short value = (short)(InventoryBackpackBase + i);
                    string text = $"  [{i + 1}] {def.DisplayName} ({def.Slot})  (Enter for actions)";
                    if (text.Length > 120) text = text[..120];
                    items.Add(new SelectBoxItem(value, text.AsMemory()));
                }
            }
        }

        items.Add(new SelectBoxItem(InventoryBackToMenuValue, "<- Back to menu".AsMemory()));

        SetInventoryMenuState(player, InventoryMenuState.Inventory);
        string title = view switch
        {
            InventoryInvView.Equipped => "Inventory - Equipped items",
            InventoryInvView.Storage => "Inventory - Storage",
            _ => "Inventory - All",
        };
        _inventorySelectBox.Open(player, title, items);
    }

    /// <summary>Backpack-item action sub-menu (Equip → ship picker, or Sell).</summary>
    private void OpenInventoryBackpackItemActionsDialog(Player player, int backpackIdx)
    {
        if (_inventorySelectBox is null) return;
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        ItemDefinition? def;
        lock (pd.Lock)
        {
            if (backpackIdx < 0 || backpackIdx >= pd.Backpack.Count)
            {
                _chat.SendMessage(player, "That backpack slot is empty.");
                OpenInventoryDialog(player);
                return;
            }
            def = ItemCatalog.Find(pd.Backpack[backpackIdx]);
        }
        if (def is null)
        {
            OpenInventoryDialog(player);
            return;
        }

        long sellRefund = def.Cost / 2;

        var items = new List<SelectBoxItem>
        {
            new SelectBoxItem(InventoryBackpackEquipValue, $"Equip {def.DisplayName} to a ship...".AsMemory()),
            new SelectBoxItem(InventoryBackpackSellValue,  $"Sell for {sellRefund} cr (50% of {def.Cost})".AsMemory()),
            new SelectBoxItem(InventoryBackToMenuValue,    "<- Back to inventory".AsMemory()),
        };

        lock (_inventoryMenuStateLock)
        {
            _inventoryPendingBackpackIdx[player] = backpackIdx;
            _inventoryMenuState[player] = InventoryMenuState.BackpackItemActions;
        }
        _inventorySelectBox.Open(player, $"{def.DisplayName} - choose action", items);
    }

    /// <summary>Ship picker for the equip flow. Lists all 8 non-Spec ships
    /// and shows what's currently in that slot (replace vs empty).</summary>
    private void OpenInventoryEquipShipPickerDialog(Player player, int backpackIdx)
    {
        if (_inventorySelectBox is null) return;
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        ItemDefinition? def;
        Dictionary<(ShipType, EquipmentSlot), int> equipSnap;
        lock (pd.Lock)
        {
            if (backpackIdx < 0 || backpackIdx >= pd.Backpack.Count)
            {
                _chat.SendMessage(player, "That backpack slot is empty.");
                OpenInventoryDialog(player);
                return;
            }
            def = ItemCatalog.Find(pd.Backpack[backpackIdx]);
            equipSnap = new Dictionary<(ShipType, EquipmentSlot), int>(pd.Equipped);
        }
        if (def is null)
        {
            OpenInventoryDialog(player);
            return;
        }

        var items = new List<SelectBoxItem>();
        foreach (ShipType ship in Enum.GetValues<ShipType>())
        {
            if (ship == ShipType.Spec) continue;
            short value = (short)(InventoryShipPickerBase + (int)ship);
            string label;
            if (equipSnap.TryGetValue((ship, def.Slot), out int currentDefId))
            {
                var current = ItemCatalog.Find(currentDefId);
                label = $"{ship} (replaces {current?.DisplayName ?? "unknown"})";
            }
            else
            {
                label = $"{ship} ({def.Slot} slot empty)";
            }
            if (label.Length > 120) label = label[..120];
            items.Add(new SelectBoxItem(value, label.AsMemory()));
        }
        items.Add(new SelectBoxItem(InventoryBackToMenuValue, "<- Back".AsMemory()));

        lock (_inventoryMenuStateLock)
        {
            _inventoryPendingBackpackIdx[player] = backpackIdx;
            _inventoryMenuState[player] = InventoryMenuState.EquipShipPicker;
        }
        _inventorySelectBox.Open(player, $"Equip {def.DisplayName} ({def.Slot}) to which ship?", items);
    }

    // -------------------------------------------------------------------------
    // EQUIP / UNEQUIP / SELL FROM MENU
    // -------------------------------------------------------------------------

    /// <summary>Equip a backpack item to a specific ship. If the slot is
    /// occupied the existing item is moved BACK to the backpack at the same
    /// slot index (atomic swap under pd.Lock).</summary>
    private void EquipFromInventoryMenu(Player player, int backpackIdx, ShipType ship)
    {
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        ItemDefinition? equippedItem = null;
        ItemDefinition? swappedOut = null;

        lock (pd.Lock)
        {
            if (backpackIdx < 0 || backpackIdx >= pd.Backpack.Count)
            {
                _chat.SendMessage(player, "That backpack slot is empty.");
                return;
            }
            int defId = pd.Backpack[backpackIdx];
            equippedItem = ItemCatalog.Find(defId);
            if (equippedItem is null)
            {
                _chat.SendMessage(player, "Unknown item — possibly a stale save. Skipping.");
                return;
            }

            var key = (ship, equippedItem.Slot);
            if (pd.Equipped.TryGetValue(key, out int existingDefId))
            {
                swappedOut = ItemCatalog.Find(existingDefId);
                pd.Backpack[backpackIdx] = existingDefId;
            }
            else
            {
                pd.Backpack.RemoveAt(backpackIdx);
            }
            pd.Equipped[key] = defId;
        }

        if (swappedOut is not null)
            _chat.SendMessage(player,
                $"Equipped {equippedItem.DisplayName} on {ship} (swapped {swappedOut.DisplayName} to backpack).");
        else
            _chat.SendMessage(player, $"Equipped {equippedItem.DisplayName} on {ship}.");

        _inventoryShipSettings?.RefreshPlayer(player);
        SyncTurretsForCurrentShip_Inventory(player);
    }

    /// <summary>Unequip a (ship, slot) and push the item back to the backpack.
    /// Refuses if the backpack is full (player must sell something first).</summary>
    private void UnequipFromInventoryMenu(Player player, ShipType ship, EquipmentSlot slot)
    {
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        ItemDefinition? unequipped = null;
        bool full = false;
        lock (pd.Lock)
        {
            var key = (ship, slot);
            if (!pd.Equipped.TryGetValue(key, out int defId)) return;

            if (pd.Backpack.Count >= InventoryBackpackCapacity) full = true;
            else
            {
                pd.Backpack.Add(defId);
                pd.Equipped.Remove(key);
                unequipped = ItemCatalog.Find(defId);
            }
        }

        if (full)
        {
            _chat.SendMessage(player, "Backpack full — sell something first.");
            return;
        }
        if (unequipped is not null)
            _chat.SendMessage(player, $"Unequipped {unequipped.DisplayName} from {ship}.");
        _inventoryShipSettings?.RefreshPlayer(player);
        SyncTurretsForCurrentShip_Inventory(player);
    }

    /// <summary>Sell a backpack item at 50% refund (rounded down).</summary>
    private void SellFromInventoryMenu(Player player, int backpackIdx)
    {
        if (_inventoryEconomy is null) return;
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        ItemDefinition? sold = null;
        lock (pd.Lock)
        {
            if (backpackIdx < 0 || backpackIdx >= pd.Backpack.Count) return;
            int defId = pd.Backpack[backpackIdx];
            sold = ItemCatalog.Find(defId);
            pd.Backpack.RemoveAt(backpackIdx);
        }
        if (sold is null) return;

        long refund = sold.Cost / 2;
        _inventoryEconomy.TryEarn(player, refund, $"shopsell {sold.DisplayName}");
        _chat.SendMessage(player, $"Sold {sold.DisplayName} for {refund} cr (50% of {sold.Cost}).");
    }

    // -------------------------------------------------------------------------
    // SELECT-BOX CALLBACK — central dispatcher for the entire menu tree.
    // -------------------------------------------------------------------------

    /// <summary>
    /// SelectBoxItemSelected callback. Dispatches by current MenuState (per
    /// player) so item values can have overlapping ranges across menus
    /// without ambiguity. Universal nav values (Close / BackToMenu /
    /// BackToShopCategories) are checked first and short-circuit the
    /// state-specific dispatch.
    /// </summary>
    /// <remarks>
    /// MenuState.None (no Inventory dialog open) is an immediate no-op so we
    /// don't accidentally swallow callbacks intended for SelectBox dialogs
    /// opened by other modules.
    /// </remarks>
    private void OnSelectBoxItemSelected_Inventory(Player player, short itemValue, ReadOnlySpan<char> itemText)
    {
        InventoryMenuState state = GetInventoryMenuState(player);
        if (state == InventoryMenuState.None) return;

        // Universal nav values.
        if (itemValue == InventoryCloseMenuValue)
        {
            SetInventoryMenuState(player, InventoryMenuState.None);
            return;
        }
        if (itemValue == InventoryBackToMenuValue)
        {
            OpenInventoryTopMenu(player);
            return;
        }
        if (itemValue == InventoryBackToShopCategoriesValue)
        {
            OpenInventoryShopCategoriesDialog(player);
            return;
        }

        switch (state)
        {
            case InventoryMenuState.TopMenu:
                if (itemValue == InventoryOpenShopValue) OpenInventoryShopCategoriesDialog(player);
                else if (itemValue == InventoryOpenDeployablesValue) OpenInventoryDeployablesDialog(player);
                else if (itemValue == InventoryOpenInventoryValue) OpenInventoryDialog(player);
                else if (itemValue == InventoryShowStatsValue) OpenInventoryStatsDialog(player);
                else if (itemValue == InventoryShowLeaderboardValue) OpenInventoryLeaderboardDialog(player);
                else if (itemValue == InventoryOpenCasinoValue) OpenInventoryCasinoDialog(player);
                else if (itemValue == InventoryOpenMarketValue) OpenInventoryMarketDialog(player);
                else if (itemValue == InventoryDoPrestigeValue)
                { SetInventoryMenuState(player, InventoryMenuState.None); DoInventoryPrestige(player); }
                else SetInventoryMenuState(player, InventoryMenuState.None);
                break;

            case InventoryMenuState.Deployables:
                // WAVE-10 dispatch: each kind in the 30030+ block routes
                // through IDeployableShop.TryBuy via the broker. The
                // standalone interface hooked the same way; the umbrella
                // also publishes IDeployableShop, so this works either way.
                if (itemValue == InventoryBuyDeployPylonValue
                    || itemValue == InventoryBuyDeployOutpostValue
                    || itemValue == InventoryBuyDeployWarStationValue)
                {
                    string kind =
                        itemValue == InventoryBuyDeployPylonValue ? "pylon" :
                        itemValue == InventoryBuyDeployOutpostValue ? "outpost" :
                                                                      "warstation";
                    SetInventoryMenuState(player, InventoryMenuState.None);
                    if (_inventoryBroker is null) break;
                    IDeployableShop? shop = _inventoryBroker.GetInterface<IDeployableShop>();
                    try
                    {
                        if (shop is null) { _chat.SendMessage(player, "Deployable shop unavailable."); break; }
                        shop.TryBuy(player, kind, out string msg);
                        _chat.SendMessage(player, msg);
                    }
                    finally
                    {
                        if (shop is not null) _inventoryBroker.ReleaseInterface(ref shop);
                    }
                }
                else if (itemValue == InventoryBackToMenuValue) OpenInventoryTopMenu(player);
                else if (itemValue == InventoryInfoItemValue) OpenInventoryDeployablesDialog(player);  // info-row click refreshes
                else SetInventoryMenuState(player, InventoryMenuState.None);
                break;

            case InventoryMenuState.ShopCategories:
                if (itemValue == InventoryShopEnginesValue) OpenInventoryShopCategoryDialog(player, EquipmentSlot.Engine);
                else if (itemValue == InventoryShopShieldsValue) OpenInventoryShopCategoryDialog(player, EquipmentSlot.Shield);
                else if (itemValue == InventoryShopWeaponsValue) OpenInventoryShopCategoryDialog(player, EquipmentSlot.WeaponMod);
                else if (itemValue == InventoryShopHullValue) OpenInventoryShopCategoryDialog(player, EquipmentSlot.HullPlating);
                else SetInventoryMenuState(player, InventoryMenuState.None);
                break;

            case InventoryMenuState.Stats:
            case InventoryMenuState.Leaderboard:
                if (itemValue == InventoryDoPrestigeValue)
                { SetInventoryMenuState(player, InventoryMenuState.None); DoInventoryPrestige(player); }
                else if (itemValue == InventoryInfoItemValue) { SetInventoryMenuState(player, InventoryMenuState.None); }
                else SetInventoryMenuState(player, InventoryMenuState.None);
                break;

            case InventoryMenuState.Casino:
                if (itemValue == InventoryDiceBet100Value)
                { SetInventoryMenuState(player, InventoryMenuState.None); TryInventoryDiceFromMenu(player, 100); }
                else if (itemValue == InventoryDiceBet1kValue)
                { SetInventoryMenuState(player, InventoryMenuState.None); TryInventoryDiceFromMenu(player, 1000); }
                else if (itemValue == InventoryDiceBet10kValue)
                { SetInventoryMenuState(player, InventoryMenuState.None); TryInventoryDiceFromMenu(player, 10000); }
                else if (itemValue == InventoryDiceBet100kValue)
                { SetInventoryMenuState(player, InventoryMenuState.None); TryInventoryDiceFromMenu(player, 100000); }
                else SetInventoryMenuState(player, InventoryMenuState.None);
                break;

            case InventoryMenuState.Market:
                SetInventoryMenuState(player, InventoryMenuState.None);
                break;

            case InventoryMenuState.Shop:
                // View-filter clicks: update preference + re-open same category.
                if (itemValue == InventoryShopViewAllValue ||
                    itemValue == InventoryShopViewAffordableValue ||
                    itemValue == InventoryShopViewNotOwnedValue)
                {
                    EquipmentSlot category;
                    lock (_inventoryMenuStateLock)
                    {
                        _inventoryShopView[player] = itemValue switch
                        {
                            InventoryShopViewAffordableValue => InventoryShopView.Affordable,
                            InventoryShopViewNotOwnedValue => InventoryShopView.NotOwned,
                            _ => InventoryShopView.All,
                        };
                        if (!_inventoryShopCategory.TryGetValue(player, out category))
                            category = EquipmentSlot.Engine;
                    }
                    OpenInventoryShopCategoryDialog(player, category);
                }
                else if (itemValue == InventoryInfoItemValue)
                {
                    // Section header / placeholder click — inert, refresh.
                    EquipmentSlot category;
                    lock (_inventoryMenuStateLock)
                    {
                        if (!_inventoryShopCategory.TryGetValue(player, out category))
                            category = EquipmentSlot.Engine;
                    }
                    OpenInventoryShopCategoryDialog(player, category);
                }
                else
                {
                    // Any other value in shop state == catalog item id — try buy.
                    SetInventoryMenuState(player, InventoryMenuState.None);
                    var def = ItemCatalog.Find(itemValue);
                    if (def is not null) TryBuyInventoryItem(player, def);
                }
                break;

            case InventoryMenuState.Inventory:
                if (itemValue == InventoryInvViewAllValue)
                {
                    lock (_inventoryMenuStateLock) { _inventoryInvView[player] = InventoryInvView.All; }
                    OpenInventoryDialog(player);
                }
                else if (itemValue == InventoryInvViewEquippedValue)
                {
                    lock (_inventoryMenuStateLock) { _inventoryInvView[player] = InventoryInvView.Equipped; }
                    OpenInventoryDialog(player);
                }
                else if (itemValue == InventoryInvViewStorageValue)
                {
                    lock (_inventoryMenuStateLock) { _inventoryInvView[player] = InventoryInvView.Storage; }
                    OpenInventoryDialog(player);
                }
                else if (itemValue >= InventoryEquippedBase && itemValue < InventoryEquippedBase + 32)
                {
                    int idx = itemValue - InventoryEquippedBase;
                    int shipIdx = idx / 4;
                    int slotIdx = idx % 4;
                    SetInventoryMenuState(player, InventoryMenuState.None);
                    UnequipFromInventoryMenu(player, (ShipType)shipIdx, (EquipmentSlot)slotIdx);
                }
                else if (itemValue >= InventoryBackpackBase && itemValue < InventoryBackpackBase + 50)
                {
                    int backpackIdx = itemValue - InventoryBackpackBase;
                    OpenInventoryBackpackItemActionsDialog(player, backpackIdx);
                }
                else if (itemValue == InventoryInfoItemValue)
                {
                    // Section header / placeholder click — inert, re-open dialog so menu stays up.
                    OpenInventoryDialog(player);
                }
                else
                {
                    SetInventoryMenuState(player, InventoryMenuState.None);
                }
                break;

            case InventoryMenuState.BackpackItemActions:
                {
                    int backpackIdx;
                    lock (_inventoryMenuStateLock)
                    {
                        _inventoryPendingBackpackIdx.TryGetValue(player, out backpackIdx);
                    }
                    if (itemValue == InventoryBackpackEquipValue)
                    {
                        OpenInventoryEquipShipPickerDialog(player, backpackIdx);
                    }
                    else if (itemValue == InventoryBackpackSellValue)
                    {
                        SetInventoryMenuState(player, InventoryMenuState.None);
                        SellFromInventoryMenu(player, backpackIdx);
                    }
                    else
                    {
                        // Back / unknown → back to inventory.
                        OpenInventoryDialog(player);
                    }
                }
                break;

            case InventoryMenuState.EquipShipPicker:
                {
                    int backpackIdx;
                    lock (_inventoryMenuStateLock)
                    {
                        _inventoryPendingBackpackIdx.TryGetValue(player, out backpackIdx);
                    }
                    if (itemValue >= InventoryShipPickerBase && itemValue < InventoryShipPickerBase + 8)
                    {
                        ShipType ship = (ShipType)(itemValue - InventoryShipPickerBase);
                        SetInventoryMenuState(player, InventoryMenuState.None);
                        EquipFromInventoryMenu(player, backpackIdx, ship);
                    }
                    else
                    {
                        // Back to backpack actions.
                        OpenInventoryBackpackItemActionsDialog(player, backpackIdx);
                    }
                }
                break;
        }
    }

    // -------------------------------------------------------------------------
    // PLAYER-LIFECYCLE CALLBACKS
    // -------------------------------------------------------------------------

    /// <summary>PlayerActionCallback — clears per-player UI state on disconnect/leave;
    /// resyncs turrets on enter (so a freshly-loaded loadout produces turrets).</summary>
    private void OnPlayerAction_Inventory(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.Disconnect || action == PlayerAction.LeaveArena)
        {
            // Cleanup branch — always safe (no-op for players without state).
            SetInventoryMenuState(player, InventoryMenuState.None);
            lock (_inventoryMenuStateLock)
            {
                _inventoryInvView.Remove(player);
                _inventoryShopView.Remove(player);
                _inventoryShopCategory.Remove(player);
            }
        }
        else if (action == PlayerAction.EnterGame)
        {
            // Arena-attach guard: don't sync turrets in non-SectorWar arenas.
            if (arena is null) return;
            arena.TryGetExtraData(_adKey, out ArenaData? ad);
            if (ad?.Arena is null) return;
            SyncTurretsForCurrentShip_Inventory(player);
        }
    }

    /// <summary>ShipFreqChangeCallback — turret-grants are tied to the
    /// WeaponMod equipped on the CURRENT ship, so changing ships needs a resync.</summary>
    private void OnShipFreqChange_Inventory(Player player, ShipType newShip, ShipType oldShip,
        short newFreq, short oldFreq)
    {
        if (newShip == oldShip) return;
        // Arena-attach guard: don't sync turrets in non-SectorWar arenas.
        Arena? arena = player.Arena;
        if (arena is null) return;
        arena.TryGetExtraData(_adKey, out ArenaData? ad);
        if (ad?.Arena is null) return;
        SyncTurretsForCurrentShip_Inventory(player);
    }

    // -------------------------------------------------------------------------
    // SHARED BUY HELPER + UTILITIES
    // -------------------------------------------------------------------------

    /// <summary>
    /// Spec-only buy path. Spends the item's cost, then attempts to add it
    /// to the player's backpack. If the backpack is full AFTER the spend,
    /// we refund the spend (tagged as "shopbuy refund (backpack full)" for
    /// audit-log pairing).
    /// </summary>
    private void TryBuyInventoryItem(Player player, ItemDefinition def)
    {
        if (_inventoryEconomy is null) return;
        if (!RequireSpecForInventory(player)) return;
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        if (!_inventoryEconomy.TrySpend(player, def.Cost, $"shopbuy {def.DisplayName}"))
        {
            long balance = _inventoryEconomy.GetBalance(player);
            _chat.SendMessage(player,
                $"You need {def.Cost} cr for {def.DisplayName}. You have {balance} cr.");
            return;
        }

        int newCount;
        bool full = false;
        lock (pd.Lock)
        {
            if (pd.Backpack.Count >= InventoryBackpackCapacity)
            {
                full = true;
            }
            else
            {
                pd.Backpack.Add(def.Id);
            }
            newCount = pd.Backpack.Count;
        }

        if (full)
        {
            _inventoryEconomy.TryEarn(player, def.Cost, "shopbuy refund (backpack full)");
            _chat.SendMessage(player,
                $"Backpack full ({InventoryBackpackCapacity}). Sell something first. Refunded.");
            return;
        }

        _chat.SendMessage(player,
            $"Bought {def.DisplayName} for {def.Cost} cr. Backpack: {newCount}/{InventoryBackpackCapacity}.");
    }

    /// <summary>Pretty-print an item's modifier list for ?shop / dialog rows.</summary>
    private static string SummarizeInventoryModifiers(ItemDefinition item)
    {
        var parts = new List<string>();
        foreach (var mod in item.Modifiers)
        {
            string sign = mod.Addend >= 0 ? "+" : "";
            parts.Add($"{sign}{mod.Addend} {mod.Key}");
        }
        return string.Join(", ", parts);
    }

    // -------------------------------------------------------------------------
    // CHAT COMMANDS — 8 commands, all routed through the per-MenuState
    // dialog flow when the player is in spec, or to text-mode fallback when
    // the player is flying.
    // -------------------------------------------------------------------------

    /// <summary>?menu — opens the unified SectorWar top-menu dialog. Arrow
    /// keys to navigate, Enter to select, Esc to close.</summary>
    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Open the unified SectorWar menu (shop, inventory). Arrow keys to navigate, Enter to select, Esc to close.")]
    private void Command_InventoryMenu(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        // In spec: open the full SelectBox dialog UI (arrow-key navigation).
        // In flight: print a text-mode menu so players in mid-match can still
        // see what's available + which commands to use. Continuum's SelectBox
        // channel is spec-only, but the underlying commands (?buy pylon, etc.)
        // work in-flight just fine.
        if (player.Ship == ShipType.Spec)
        {
            if (_inventorySelectBox is null)
            {
                _chat.SendMessage(player,
                    "?menu: SelectBox host module isn't loaded on this zone. " +
                    "Ask phong to attach SS.Core.Modules.SelectBox.");
                return;
            }
            OpenInventoryTopMenu(player);
            return;
        }

        // Flight mode: text-mode quick reference. Each item lists the direct
        // command so players can fire it off without going to spec.
        _chat.SendMessage(player, "--- SectorWar quick menu (flight) ---");
        _chat.SendMessage(player, "DEPLOYABLES (cost in credits):");
        _chat.SendMessage(player, "  ?buy pylon         - power source + claim point");
        _chat.SendMessage(player, "  ?buy outpost       - 5-turret defense ring");
        _chat.SendMessage(player, "  ?buy warstation    - 9-turret fortress");
        _chat.SendMessage(player, "  ?deploy shop       - full deployable price list");
        _chat.SendMessage(player, "STATS / ECONOMY:");
        _chat.SendMessage(player, "  ?bal               - credit balance");
        _chat.SendMessage(player, "  ?sectorwar         - level / XP / credits");
        _chat.SendMessage(player, "  ?top               - wealthiest online players");
        _chat.SendMessage(player, "  ?market / ?portfolio - ticker prices + holdings");
        _chat.SendMessage(player, "INVENTORY (spec to equip/unequip):");
        _chat.SendMessage(player, "  ?inv               - your equipped + backpack items");
        _chat.SendMessage(player, "  ?shop              - browse item catalog");
        _chat.SendMessage(player, "GAME:");
        _chat.SendMessage(player, "  ?start war         - spawn both team HQs (begin round)");
        _chat.SendMessage(player, "  ?claim             - this arena's pylon-claim state");
        _chat.SendMessage(player, "");
        _chat.SendMessage(player, "Spec (Esc) and re-type ?menu for the full dialog UI.");
    }

    /// <summary>?shop — in spec, opens the dialog UI; otherwise prints a
    /// text list of every catalog item.</summary>
    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Open the shop. In spec, opens a dialog (arrow keys + Enter). Otherwise prints a text list.")]
    private void Command_InventoryShop(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Ship == ShipType.Spec)
        {
            OpenInventoryShopDialog(player);
            return;
        }

        _chat.SendMessage(player, "--- SectorWar Shop ---");
        foreach (var item in ItemCatalog.All)
        {
            string effect = SummarizeInventoryModifiers(item);
            _chat.SendMessage(player, $"  [{item.Id}] {item.DisplayName} - {item.Cost} cr ({effect})");
        }
        _chat.SendMessage(player, "Spectate (Esc) and ?shop or ?menu for the dialog UI.");
    }

    /// <summary>?shopbuy &lt;id&gt; — buy by id without opening the dialog.</summary>
    [CommandHelp(Targets = CommandTarget.None, Args = "<id>",
        Description = "Buy an item from the shop by id. Must be in spectator mode.")]
    private void Command_InventoryShopBuy(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!int.TryParse(parameters.Trim(), out int id))
        {
            _chat.SendMessage(player, "Usage: ?shopbuy <id>  (or use ?shop in spec to click)");
            return;
        }

        var def = ItemCatalog.Find(id);
        if (def is null)
        {
            _chat.SendMessage(player, $"No item with id {id}. Try ?shop.");
            return;
        }

        TryBuyInventoryItem(player, def);
    }

    /// <summary>?inv (alias ?inventory) — prints equipped + backpack.</summary>
    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Show your equipped gear and backpack. Ships with no equipment are hidden.")]
    private void Command_InventoryInv(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        Dictionary<(ShipType Ship, EquipmentSlot Slot), int> equipSnap;
        List<int> backpackSnap;
        lock (pd.Lock)
        {
            equipSnap = new Dictionary<(ShipType, EquipmentSlot), int>(pd.Equipped);
            backpackSnap = new List<int>(pd.Backpack);
        }

        // Per-ship summary — show only ships with at least one item equipped.
        var perShip = new Dictionary<ShipType, List<string>>();
        foreach (var ((ship, slot), defId) in equipSnap)
        {
            var item = ItemCatalog.Find(defId);
            if (item is null) continue;
            if (!perShip.TryGetValue(ship, out var list))
            {
                list = new List<string>();
                perShip[ship] = list;
            }
            list.Add($"{slot}={item.DisplayName}");
        }

        if (perShip.Count == 0)
        {
            _chat.SendMessage(player, "--- Ships --- (nothing equipped on any ship)");
        }
        else
        {
            _chat.SendMessage(player, "--- Ships ---");
            foreach (ShipType ship in Enum.GetValues<ShipType>())
            {
                if (ship == ShipType.Spec) continue;
                if (!perShip.TryGetValue(ship, out var items)) continue;
                _chat.SendMessage(player, $"  {ship}: {string.Join(", ", items)}");
            }
        }

        _chat.SendMessage(player, $"--- Backpack ({backpackSnap.Count}/{InventoryBackpackCapacity}) ---");
        if (backpackSnap.Count == 0)
        {
            _chat.SendMessage(player, "  (empty — try ?shop)");
        }
        else
        {
            for (int i = 0; i < backpackSnap.Count; i++)
            {
                var item = ItemCatalog.Find(backpackSnap[i]);
                _chat.SendMessage(player, $"  [{i + 1}] {item?.DisplayName ?? "?"}");
            }
            _chat.SendMessage(player,
                "?equip <slot#> <ship> to equip, ?shopsell <slot#> to sell at 50%.");
        }
    }

    /// <summary>?equip &lt;backpack#&gt; &lt;ship&gt; — text-mode equip with swap.</summary>
    [CommandHelp(Targets = CommandTarget.None, Args = "<backpack#> <ship>",
        Description = "Equip a backpack item to a specific ship's matching slot. Ship is Warbird, Javelin, Spider, etc.")]
    private void Command_InventoryEquip(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        int spaceIdx = parameters.IndexOf(' ');
        if (spaceIdx < 1 || spaceIdx >= parameters.Length - 1)
        {
            _chat.SendMessage(player, "Usage: ?equip <backpack#> <ship>  (e.g. ?equip 1 warbird)");
            return;
        }

        ReadOnlySpan<char> idxText = parameters[..spaceIdx].Trim();
        ReadOnlySpan<char> shipText = parameters[(spaceIdx + 1)..].Trim();

        if (!int.TryParse(idxText, out int oneBased) || oneBased < 1)
        {
            _chat.SendMessage(player, "Backpack# must be a positive integer.");
            return;
        }
        int idx = oneBased - 1;

        if (!Enum.TryParse(shipText, ignoreCase: true, out ShipType ship) || ship == ShipType.Spec)
        {
            _chat.SendMessage(player,
                "Ship must be one of: Warbird, Javelin, Spider, Leviathan, Terrier, Weasel, Lancaster, Shark.");
            return;
        }

        ItemDefinition? equippedItem = null;
        ItemDefinition? swappedOut = null;

        lock (pd.Lock)
        {
            if (idx >= pd.Backpack.Count)
            {
                _chat.SendMessage(player, "Invalid backpack slot. Use ?inv to list.");
                return;
            }
            int defId = pd.Backpack[idx];
            equippedItem = ItemCatalog.Find(defId);
            if (equippedItem is null)
            {
                _chat.SendMessage(player, "Unknown item — possibly a stale save. Skipping.");
                return;
            }

            var key = (ship, equippedItem.Slot);
            if (pd.Equipped.TryGetValue(key, out int existingDefId))
            {
                swappedOut = ItemCatalog.Find(existingDefId);
                pd.Backpack[idx] = existingDefId;
            }
            else
            {
                pd.Backpack.RemoveAt(idx);
            }
            pd.Equipped[key] = defId;
        }

        if (swappedOut is not null)
            _chat.SendMessage(player,
                $"Equipped {equippedItem.DisplayName} on {ship} (swapped {swappedOut.DisplayName} to backpack).");
        else
            _chat.SendMessage(player, $"Equipped {equippedItem.DisplayName} on {ship}.");

        _inventoryShipSettings?.RefreshPlayer(player);
        SyncTurretsForCurrentShip_Inventory(player);
    }

    /// <summary>?unequip &lt;ship&gt; &lt;slot&gt; — text-mode unequip.</summary>
    [CommandHelp(Targets = CommandTarget.None, Args = "<ship> <slot>",
        Description = "Unequip a slot on a specific ship. e.g. ?unequip warbird Engine")]
    private void Command_InventoryUnequip(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        int spaceIdx = parameters.IndexOf(' ');
        if (spaceIdx < 1 || spaceIdx >= parameters.Length - 1)
        {
            _chat.SendMessage(player, "Usage: ?unequip <ship> <slot>  (e.g. ?unequip warbird Engine)");
            return;
        }

        ReadOnlySpan<char> shipText = parameters[..spaceIdx].Trim();
        ReadOnlySpan<char> slotText = parameters[(spaceIdx + 1)..].Trim();

        if (!Enum.TryParse(shipText, ignoreCase: true, out ShipType ship) || ship == ShipType.Spec)
        {
            _chat.SendMessage(player,
                "Ship must be one of: Warbird, Javelin, Spider, Leviathan, Terrier, Weasel, Lancaster, Shark.");
            return;
        }
        if (!Enum.TryParse(slotText, ignoreCase: true, out EquipmentSlot slot))
        {
            _chat.SendMessage(player, "Slot must be one of: Engine, Shield, WeaponMod, HullPlating.");
            return;
        }

        ItemDefinition? unequipped = null;
        bool full = false;
        lock (pd.Lock)
        {
            var key = (ship, slot);
            if (!pd.Equipped.TryGetValue(key, out int defId))
            {
                _chat.SendMessage(player, $"Nothing equipped in {ship}'s {slot} slot.");
                return;
            }

            if (pd.Backpack.Count >= InventoryBackpackCapacity)
            {
                full = true;
            }
            else
            {
                pd.Backpack.Add(defId);
                pd.Equipped.Remove(key);
                unequipped = ItemCatalog.Find(defId);
            }
        }

        if (full)
        {
            _chat.SendMessage(player, "Backpack full. Sell something first.");
            return;
        }

        if (unequipped is not null)
            _chat.SendMessage(player, $"Unequipped {unequipped.DisplayName} from {ship}.");

        _inventoryShipSettings?.RefreshPlayer(player);
        SyncTurretsForCurrentShip_Inventory(player);
    }

    /// <summary>?shopsell &lt;backpack#&gt; — text-mode sell at 50% refund.</summary>
    [CommandHelp(Targets = CommandTarget.None, Args = "<backpack#>",
        Description = "Sell an item from your backpack at 50% refund.")]
    private void Command_InventoryShopSell(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (_inventoryEconomy is null) return;
        if (!player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd)) return;

        if (!int.TryParse(parameters.Trim(), out int oneBased) || oneBased < 1)
        {
            _chat.SendMessage(player, "Usage: ?shopsell <backpack#>");
            return;
        }
        int idx = oneBased - 1;

        ItemDefinition? sold = null;
        lock (pd.Lock)
        {
            if (idx >= pd.Backpack.Count)
            {
                _chat.SendMessage(player, "Invalid slot number.");
                return;
            }
            int defId = pd.Backpack[idx];
            sold = ItemCatalog.Find(defId);
            pd.Backpack.RemoveAt(idx);
        }

        if (sold is null)
        {
            _chat.SendMessage(player, "Unknown item.");
            return;
        }

        long refund = sold.Cost / 2;
        _inventoryEconomy.TryEarn(player, refund, $"shopsell {sold.DisplayName}");
        _chat.SendMessage(player, $"Sold {sold.DisplayName} for {refund} cr (50% of {sold.Cost}).");
    }

    // -------------------------------------------------------------------------
    // PERSIST — IPersist GetData/SetData/ClearData callbacks.
    //
    // Wire format:
    //   byte version (1 = global slots, 2 = per-ship slots — current write)
    //   int  backpackCount
    //     (int defId) * backpackCount
    //   int  equippedCount
    //     v1: (byte slotByte, int defId) * equippedCount
    //     v2: (byte shipByte, byte slotByte, int defId) * equippedCount
    //
    // Threading: IPersist may invoke GetData/SetData on a worker thread, so
    // every read/write of pd.Backpack/pd.Equipped is under pd.Lock. We
    // snapshot under the lock then write the snapshot outside the lock to
    // keep lock-hold time bounded.
    // -------------------------------------------------------------------------

    /// <summary>Serialize a player's inventory to <paramref name="outStream"/>.</summary>
    private void Persist_Inventory_GetData(Player? player, Stream outStream)
    {
        if (player is null || !player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd))
            return;

        List<int> backpack;
        Dictionary<(ShipType Ship, EquipmentSlot Slot), int> equipped;
        lock (pd.Lock)
        {
            // Skip empty state — no row written, nothing to read on next load.
            if (pd.Backpack.Count == 0 && pd.Equipped.Count == 0)
                return;
            backpack = new List<int>(pd.Backpack);
            equipped = new Dictionary<(ShipType, EquipmentSlot), int>(pd.Equipped);
        }

        using BinaryWriter writer = new(outStream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(InventoryPersistVersion);

        writer.Write(backpack.Count);
        foreach (int id in backpack) writer.Write(id);

        writer.Write(equipped.Count);
        foreach (var ((ship, slot), id) in equipped)
        {
            writer.Write((byte)ship);
            writer.Write((byte)slot);
            writer.Write(id);
        }
    }

    /// <summary>Deserialize a player's inventory from <paramref name="inStream"/>.</summary>
    /// <remarks>
    /// Migration path: v1 saves stored a single (slot → defId) dict shared
    /// across all 8 ships. On read we duplicate every entry to all 8 ship
    /// classes so a v1 player keeps their gear after the schema upgrade.
    /// Logged at Info level so anyone tracing a migration can find the
    /// breadcrumb in the log.
    /// </remarks>
    private void Persist_Inventory_SetData(Player? player, Stream inStream)
    {
        if (player is null || !player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd))
            return;

        using BinaryReader reader = new(inStream, System.Text.Encoding.UTF8, leaveOpen: true);

        byte version = reader.ReadByte();
        if (version != 1 && version != InventoryPersistVersion)
        {
            _logManager.LogP(LogLevel.Warn, LogCategory, player,
                $"Unknown inventory version {version}; ignoring.");
            return;
        }

        int backpackCount = reader.ReadInt32();
        var loadedBackpack = new List<int>(backpackCount);
        for (int i = 0; i < backpackCount; i++)
            loadedBackpack.Add(reader.ReadInt32());

        int equipCount = reader.ReadInt32();
        var loadedEquipped = new Dictionary<(ShipType, EquipmentSlot), int>();

        if (version == 1)
        {
            // Migration: v1 had global slots — duplicate to all 8 ships.
            for (int i = 0; i < equipCount; i++)
            {
                byte slotByte = reader.ReadByte();
                int defId = reader.ReadInt32();
                if (!Enum.IsDefined((EquipmentSlot)slotByte)) continue;

                EquipmentSlot slot = (EquipmentSlot)slotByte;
                foreach (ShipType ship in Enum.GetValues<ShipType>())
                {
                    if (ship == ShipType.Spec) continue;
                    loadedEquipped[(ship, slot)] = defId;
                }
            }
            _logManager.LogP(LogLevel.Info, LogCategory, player,
                $"Migrated v1 inventory ({equipCount} item(s)) to v2 (per-ship). " +
                "Items duplicated to all 8 ships.");
        }
        else
        {
            for (int i = 0; i < equipCount; i++)
            {
                byte shipByte = reader.ReadByte();
                byte slotByte = reader.ReadByte();
                int defId = reader.ReadInt32();
                if (Enum.IsDefined((ShipType)shipByte) && Enum.IsDefined((EquipmentSlot)slotByte))
                {
                    loadedEquipped[((ShipType)shipByte, (EquipmentSlot)slotByte)] = defId;
                }
            }
        }

        lock (pd.Lock)
        {
            pd.Backpack.Clear();
            pd.Backpack.AddRange(loadedBackpack);
            pd.Equipped.Clear();
            foreach (var (k, i) in loadedEquipped)
                pd.Equipped[k] = i;
        }
    }

    /// <summary>Reset a player's inventory to empty (e.g. ?clearinventory or
    /// admin-driven reset). Both Backpack and Equipped are cleared atomically.</summary>
    private void Persist_Inventory_ClearData(Player? player)
    {
        if (player is null || !player.TryGetExtraData(_inventoryPdKey, out InventoryPlayerData? pd))
            return;

        lock (pd.Lock)
        {
            pd.Backpack.Clear();
            pd.Equipped.Clear();
        }
    }
}
