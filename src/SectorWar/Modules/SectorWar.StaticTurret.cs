using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.Utilities;
using System.Threading;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — StaticTurret subsystem (Phase 1 D1st0rt port).
// =============================================================================
//
// PURPOSE
// -------
// Server-spawned destroyable turrets on the map. Each turret is a fake-player
// with HP, weapons, AI rotation/firing toward the nearest visible enemy. A
// per-freq "power" resource lets freqs power down enemy turrets. Optional
// build sequences let turrets construct over time before becoming combat-active.
// Optional cannon-LVZ overlay lets turrets render a static graphic instead of
// the underlying ship sprite (Phase 5b-C).
//
// SOURCE
// ------
// Port of D1st0rt's staticturret.c (ASSS, ~2174 lines C). Standalone module
// `Modules/StaticTurret.cs` stays as a library copy. Behavior parity with the
// original is partial:
//   - Data structures, conf parsing, AddBot/RemoveBot, freeze, SetPower,
//     Bresenham line-of-sight, FireControl prediction, rotate/fire AI — all
//     faithful ports.
//   - LVZ HP bars / build bars — DEFERRED.
//   - Splash damage (DoDamage) — DEFERRED until full asss-damage parity lands.
// The dormant fields stay in BotData so the wiring is ready to go live without
// shape changes when the deferred features are implemented.
//
// CONF MIGRATION
// --------------
// Per the umbrella's Phase-1 plan, every key the standalone module read from
// `[StaticTurret]` now lives under the unified `[SectorWar]` section with a
// `StaticTurret` prefix:
//
//   [SectorWar]
//   StaticTurretHosted = 0
//   StaticTurretMaxBots = -1
//   StaticTurretShipFavour = 0
//   StaticTurretTurretPlacementRange = 0
//   StaticTurretTurret0 = sentry           ; was StaticTurret:turret0 = sentry
//   StaticTurretTurret1 = bunker
//   StaticTurretSpawn0 = 256, 256, 0, sentry
//   StaticTurretSpawn1 = 768, 768, 1, bunker
//
// EXCEPTION (per Hard rule 7): the `[staticturret_<key>]` per-type registry
// sections stay AS-IS. They form the turret-type catalog and renaming would
// break the conf surface external content authors depend on. The same goes
// for `[<ShipName>]` lookups for BulletSpeed / BombSpeed which use SS.NET's
// standard ship sections.
//
// LIFECYCLE / OWNERSHIP
// ---------------------
//   - Owned state: per-arena ArenaData fields (StaticTurret*-prefixed) +
//                  one zone-wide List<BotData> guarded by the global lock.
//   - Conf keys read: `[SectorWar]` (StaticTurret-prefixed) at attach +
//                     `[staticturret_<key>]` per-type at attach.
//   - Persisted data: NONE (session-only — bots disappear on arena restart).
//   - Fakes registered: 1 per turret. Each is registered with IDamage when
//                       the Damage subsystem is loaded so server-authoritative
//                       bullet collision can route hits to OnBotDamaged_StaticTurret.
//                       (We call ((IDamage)this).AddFake directly — IDamage
//                       lives on the same partial class.)
//   - Timers scheduled: one IMainloopTimer at StaticTurretTickCadenceMs (50ms)
//                       for the AI tick (recharge / target / rotate / fire /
//                       periodic position broadcast).
//   - Commands registered: NONE in this subsystem (StartGame/StopGame are
//                          driven through the IStaticTurret interface — Pylon,
//                          ArenaDefenses, BossEncounter, DevCommands' ?damtest,
//                          and StationDeployer all call AddBot directly).
//   - Broker interfaces published: IStaticTurret.
//   - Broker events: BotKilled (Action<Arena, string, int, int, short, Player?>?).
//                    Pylon + StationDeployer subscribe via the resolved
//                    IStaticTurret interface and match against their registries
//                    by (pixelX, pixelY, freq) when a bot dies.
//
// CALLBACKS HOOKED (per arena via Attach)
// ---------------------------------------
//   - ArenaActionCallback (per-arena): refresh conf on Create / ConfChanged.
//
// THREADING
// ---------
// Single global lock (mirrors ASSS's "we run on the main thread, no lock
// needed" assumption — except SS.NET's IMainloopTimer + IPlayerData
// callbacks are not as serialized as ASSS's mainloop, so we explicitly guard
// _staticTurretBots and the per-arena Power/Freeze arrays with the lock.) The
// IDamage damageFunc callback (OnBotDamaged_StaticTurret) is documented to
// fire on the mainloop thread. The IStaticTurret API methods take the lock
// themselves; outside callers (Pylon, StationDeployer, etc.) don't need to.
//
// RACE WITH DAMAGE TICK
// ---------------------
// IDamage.AddFake's contract: "if a single tick has multiple bullets that hit
// the SAME fake AND the first hit triggers death (caller calls RemoveFake /
// EndFaked), the remaining snapshotted bullets WILL still invoke this
// damageFunc on the (now-dead) fake." OnBotDamaged_StaticTurret guards with
// `bot.Killed` to short-circuit late hits — preserved as Wave-style fix.
//
// WAVE-FIXES PRESERVED
// --------------------
//   - Wave: snapshot+ToArray-style index-walking on RemoveAllBots / DetachModule
//     so RemoveBotInternal_StaticTurret's downstream EndFaked + LVZ pool return
//     can't re-enter mid-iteration.
//   - Wave: bot.Killed flag to ignore late damage callbacks after the first
//     fatal hit (per IDamage threading-contract above).
//   - Wave: lazy-resolve IDamage availability at AddBot time. The standalone
//     module had a load-order race; in the umbrella the Damage subsystem is
//     loaded before StaticTurret in IAsyncModule.LoadAsync, but the same
//     defensive `_damageInterfaceAvailable` check stays so a misordered
//     load list logs a warning instead of silently leaking invulnerable bots.
//   - Wave: weapon-type clamp + WeaponMultifire=false for bombs (matches the
//     ASSS "bombs can't multifire" invariant).
//   - Wave: per-turret ShipRadius override read from the turret's own conf
//     section, NOT the arena-wide [Leviathan]/etc. Radius. Hyperspace-style
//     configs use [<Ship>] Radius as a per-player cap (e.g. 255), not as a
//     collision radius — using it for hit detection makes turrets impossibly
//     large hitboxes.
//   - Wave: fall back to the SS.NET-conventional [<Ship>] BulletSpeed /
//     BombSpeed for projSpeed lookup, so the turret's lead prediction matches
//     what the firing client actually simulates.
//
// SUBSYSTEM-PREFIXED IDENTIFIER POLICY
// ------------------------------------
// Every type, field, constant, method, callback handler, and ArenaData field
// declared in this file is prefixed with `StaticTurret` (or PascalCase
// equivalent on members). This is a hard partial-class rule — a 33-subsystem
// merge into one class will collide on common names like `BotData`, `OnTick`,
// `Energy`, `MaxBots` if subsystems don't namespace their identifiers.
// =============================================================================

public sealed partial class SectorWar : IStaticTurret
{
    // -------------------------------------------------------------------------
    // CONSTANTS — copied verbatim from staticturret.c top of file.
    // -------------------------------------------------------------------------

    // Conf surface owned by the StaticTurret subsystem — see docs/ARENA_SETTINGS.md.
    // Indexed keys [SectorWar] StaticTurretTurret{N} / StaticTurretSpawn{N} and
    // per-turret-type [staticturret_<key>] sections (Energy, Recharge, WeaponType,
    // …) are documented in ARENA_SETTINGS.md only — ConfigHelp does not support
    // indexed or dynamic-section declarations.
    // Pinned to a field; the framework's Help scanner only walks members.
    [ConfigHelp<int>("SectorWar", "StaticTurretHosted", ConfigScope.Arena,
        Default = 0, Min = 0, Max = 1,
        Description = "Bool-as-int. 1 = bots are player-hosted; 0 = NPC-managed.")]
    [ConfigHelp<int>("SectorWar", "StaticTurretMaxBots", ConfigScope.Arena,
        Default = -1, Min = -1, Max = 999,
        Description = "Max concurrent turret bots. -1 = unlimited.")]
    [ConfigHelp<int>("SectorWar", "StaticTurretShipFavour", ConfigScope.Arena,
        Default = 0, Min = 0, Max = 8,
        Description = "Preferred ship index for spawn. 0 = no preference; 1..8 = Warbird..Shark.")]
    [ConfigHelp<int>("SectorWar", "StaticTurretTurretPlacementRange", ConfigScope.Arena,
        Default = 0, Min = 0, Max = 16384,
        Description = "Max tile distance from host where turrets may be placed.")]
    /// <summary>Number of low-numbered freqs that have a power resource (3 = freqs 0, 1, 2).</summary>
    private const int StaticTurretStructuresFreqs = 3;

    /// <summary>Ticks to lead the target by during fire-control prediction.</summary>
    private const int StaticTurretBotProjectFavour = 80;

    /// <summary>Pixels of "favour" applied to humans (so turrets prefer them over bots).</summary>
    private const int StaticTurretBotHumanFavour = 150;

    /// <summary>Pixels of penalty for stealthed targets (only applied if XRadar=0 didn't already filter them out).</summary>
    private const int StaticTurretStealthFavour = 10;

    /// <summary>Pixels of penalty for cloaked targets.</summary>
    private const int StaticTurretCloakFavour = 70;

    /// <summary>Pixels of penalty for "wrong ship type" targets when ShipFavour configured.</summary>
    private const int StaticTurretSpecificShipFavour = 100;

    /// <summary>Target stickiness — once a turret is locked onto a target,
    /// it prefers that target unless a new candidate is THIS percent (or
    /// less) of the current target's distance. 80 = "switch only if the
    /// new target is &lt; 80% of current distance". Cuts down on the
    /// rapid-flip jitter when two enemies are roughly equidistant
    /// (capital + perimeter gun, two players in formation, etc.).
    /// 100 disables stickiness; 50 = "must be at least half as close".</summary>
    private const int StaticTurretTargetStickyPercent = 80;

    /// <summary>Periodic position-packet broadcast interval. ASSS uses 25 ticks (250ms).</summary>
    private const int StaticTurretPositionPacketIntervalMs = 250;

    /// <summary>AI tick cadence (recharge + target + rotate + fire + position).</summary>
    private const int StaticTurretTickCadenceMs = 50;

    /// <summary>Math.PI alias matching ASSS's `Pi` for verbatim-port readability.</summary>
    private const double StaticTurretPi = Math.PI;

    /// <summary>First LVZ object id reserved for the cannon-overlay pool (Phase 5b-C).</summary>
    private const short StaticTurretCannonPoolStart = 9400;

    /// <summary>Last LVZ object id reserved for the cannon-overlay pool (inclusive). 100 slots total.</summary>
    private const short StaticTurretCannonPoolEnd = 9499;

    /// <summary>Half the cannon LVZ image side length (used to center it on the bot's pixel).</summary>
    private const int StaticTurretCannonHalfSize = 32; // 64 / 2

    // -------------------------------------------------------------------------
    // ArenaData extension — per-arena state lives here.
    // -------------------------------------------------------------------------

    /// <summary>Per-turret-type registry entry. One instance per `[staticturret_<key>]` section.</summary>
    internal sealed class StaticTurretType
    {
        public string Key = "";
        public string Name = "~Turret";
        public int BotCount;

        // Per-type config (mirrors ASSS struct config).
        public byte Ship;                    // 0-7 (Warbird..Shark)
        public int ShipRadius = 14;
        public int Energy = 1000;
        public int Recharge = 1150;
        public int BuildSpeed = 1000;

        public byte WeaponType = (byte)WeaponCodes.Bullet;
        public int WeaponLevel;
        public int WeaponDelay = 100;
        public int WeaponFireEnergy;
        public int WeaponSightPixels = 160;
        public int WeaponShrapnelLevel;
        public int WeaponShrapnelCount;
        public bool WeaponShrapnelBouncing;
        public bool WeaponMultifire;
        public bool WeaponWaitForGoodShot;
        public int WeaponSpeed = 2000;

        public int RotationSpeed = -1;
        public int Timeout = 1500;
        public int RespawnDelay = 6000;
        public bool XRadar = true;
        public int RequiredPower;
        public int RespawnCount = 1;
        public bool Ufo;
        public int Bounty;
        public int MaxBots = -1;
        public bool DoBuildSequence;

        /// <summary>
        /// LVZ image index for the cannon-overlay graphic (Phase 5b-C). When
        /// set (>= 0), the bot's ship sprite is hidden via Cloak+Stealth+UFO
        /// status bits and a cannon LVZ object is drawn at the bot's center.
        /// Set to -1 (default) to keep the visible ship sprite (legacy behavior).
        /// When OverlayImageRotationCount &gt; 1 this is the BASE index of a
        /// rotation set; SetImage(base + frame) is called on rotation change.
        /// </summary>
        public int OverlayImageIndex = -1;
        /// <summary>
        /// Number of rotation frames packed in the cannon overlay. 1 (default)
        /// = static single image. N (e.g. 40) = an N-frame rotation set
        /// starting at OverlayImageIndex; the runtime tick maps the bot's
        /// current rotation 0..39 to a frame and SetImage's the cannon LVZ
        /// to (OverlayImageIndex + frame). Frame 0 is the barrel-points-north
        /// orientation; frames advance clockwise around the canvas center.
        /// </summary>
        public int OverlayImageRotationCount = 1;
    }

    /// <summary>
    /// Runtime state for a single turret instance. Several fields below the
    /// build-sequence block are populated by the deferred LVZ-bar / damage
    /// integration paths and look unused today — that's expected; they'll go
    /// live alongside the deferred features without changing this struct.
    /// </summary>
#pragma warning disable CS0649
    internal sealed class StaticTurretBotData
    {
        public Player? Player;
        public Arena Arena = null!;
        public StaticTurretType TurretType = null!;

        public int PixelX;
        public int PixelY;
        public short Freq;
        public bool InfiniteRespawn;

        public int Energy;
        public uint LastRecharge;
        public uint EmpShutdownExpiresAt;

        public int Spawns;
        public bool Specced;
        public uint CreatedOn;
        public uint LastPositionUpdate;
        public uint SpawnedOn;
        public uint DeathOn; // 0 = alive
        public bool Killed;

        public bool BuildSequence;
        public int BuildPoints;
        public uint LastBuild;

        /// <summary>Sub-stepped rotation: 0..40000 (40 visible steps, 1000 sub-steps each).</summary>
        public int ARotation;
        /// <summary>Target rotation in 0..40 (visible step).</summary>
        public int DesiredRotation;
        public uint LastFire;

        public Player? Targeting;

        /// <summary>
        /// Backing position packet handed by ref to IDamage.AddFake. Held by
        /// the damage subsystem for collision detection; we only need to
        /// populate it once at spawn since static turrets don't move.
        /// </summary>
        public C2S_PositionPacket DamagePos;

        /// <summary>True once IDamage.AddFake succeeded — gates RemoveFake.</summary>
        public bool RegisteredForDamage;

        /// <summary>LVZ object id (9400..9499) of the cannon overlay. -1 = no overlay.</summary>
        public short CannonLvzId = -1;
        /// <summary>Last rotation frame (0..N-1) we SetImage'd the cannon LVZ to.
        /// -1 means "never set". Used to skip redundant SetImage broadcasts when
        /// the bot's rotation hasn't changed enough to step into a new frame.</summary>
        public int LastCannonFrame = -1;
    }
#pragma warning restore CS0649

    internal sealed partial class ArenaData
    {
        public bool StaticTurretInitialized;
        public bool StaticTurretRunning;
        public Dictionary<string, StaticTurretType> StaticTurretTypes = new();
        public int StaticTurretBotCount;

        public bool StaticTurretHosted;
        public int StaticTurretMaxBots = -1;
        public int StaticTurretShipFavour = -1;
        public int StaticTurretTurretPlacementRange;

        public bool[] StaticTurretRespawnFrozen = new bool[StaticTurretStructuresFreqs];
        public uint[] StaticTurretRespawnFrozenTime = new uint[StaticTurretStructuresFreqs];
        public uint[] StaticTurretRespawnUnfrozenTime = new uint[StaticTurretStructuresFreqs];
        public int[] StaticTurretPower = new int[StaticTurretStructuresFreqs];
        public bool[] StaticTurretPowerSet = new bool[StaticTurretStructuresFreqs];

        /// <summary>Cannon-overlay LVZ pool. Lazily initialized on first allocation.</summary>
        public Stack<short> StaticTurretFreeCannonIds = new();
        public bool StaticTurretCannonPoolInitialized;
    }

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    private InterfaceRegistrationToken<IStaticTurret>? _staticTurretToken;

    /// <summary>
    /// Single global lock guarding _staticTurretBots and the per-arena
    /// Power/Freeze arrays. ASSS gets away with no lock because its mainloop
    /// is the only writer; SS.NET's IMainloopTimer + cross-arena APIs aren't
    /// quite that serialized so we lock explicitly.
    /// </summary>
    private readonly Lock _staticTurretGlobalLock = new();

    /// <summary>All active bots across all arenas (matches ASSS LinkedList bots).</summary>
    private readonly List<StaticTurretBotData> _staticTurretBots = new();

    /// <summary>
    /// Cached "is IDamage live on this same partial class" flag. Set in
    /// LoadStaticTurret if the Damage subsystem registered IDamage before us.
    /// We could just call ((IDamage)this).AddFake unconditionally — the
    /// implementation is in the same class — but checking _damageToken keeps
    /// behavior consistent if a future config disables the Damage subsystem
    /// (then ((IDamage)this).AddFake still runs but its data structures
    /// aren't initialized). The flag short-circuits cleanly.
    /// </summary>
    private bool _staticTurretDamageInterfaceAvailable;

    /// <summary>BotKilled event consumers register on the resolved IStaticTurret interface.</summary>
    public event Action<Arena, string, int, int, short, Player?>? BotKilled;
    public event Action<Arena, string, int, int, short, Player?>? BotDamaged;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Process-wide initialisation. Starts the AI tick and registers
    /// IStaticTurret on the broker. Per-arena state is initialised in
    /// <see cref="AttachStaticTurret"/>.
    /// </summary>
    private void LoadStaticTurret(IComponentBroker broker)
    {
        // Damage subsystem is loaded earlier in IAsyncModule.LoadAsync; the
        // _damageToken field on the umbrella is assigned by LoadDamage. If
        // it's non-null then ((IDamage)this) is wired and live.
        _staticTurretDamageInterfaceAvailable = _damageToken is not null;

        _mainloopTimer.SetTimer(OnTick_StaticTurret,
            StaticTurretTickCadenceMs, StaticTurretTickCadenceMs, this);

        _staticTurretToken = broker.RegisterInterface<IStaticTurret>(this);

        _logManager.LogM(LogLevel.Info, LogCategory,
            _staticTurretDamageInterfaceAvailable
                ? "StaticTurret subsystem loaded (damage-integrated)."
                : "StaticTurret subsystem loaded (no IDamage — bots invulnerable).");
    }

    /// <summary>
    /// Process-wide teardown. Tears down every active bot before the timer is
    /// cancelled so OnTick can't observe a half-cleaned _staticTurretBots list.
    /// </summary>
    private void UnloadStaticTurret(IComponentBroker broker)
    {
        if (_staticTurretToken is not null)
            broker.UnregisterInterface(ref _staticTurretToken);

        _mainloopTimer.ClearTimer(OnTick_StaticTurret, this);

        // Tear down every active bot before unload. Walk back-to-front to
        // keep the index walk deterministic even though RemoveBotInternal
        // doesn't mutate _staticTurretBots itself (it's the caller's job).
        lock (_staticTurretGlobalLock)
        {
            for (int i = _staticTurretBots.Count - 1; i >= 0; i--)
                RemoveBotInternal_StaticTurret(_staticTurretBots[i]);
            _staticTurretBots.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    // -------------------------------------------------------------------------

    /// <summary>Per-arena attach: subscribe ArenaAction + read conf for THIS arena.</summary>
    private void AttachStaticTurret(Arena arena)
    {
        ArenaActionCallback.Register(arena, OnArenaAction_StaticTurret);
        ReadStaticTurretArenaSettings(arena);
    }

    /// <summary>Per-arena detach: drop all bots that belong to this arena, clear conf cache.</summary>
    private void DetachStaticTurret(Arena arena)
    {
        ArenaActionCallback.Unregister(arena, OnArenaAction_StaticTurret);

        // Wave-style snapshot walk: RemoveBotInternal touches the LVZ pool +
        // EndFaked which can re-enter callbacks. Walk back-to-front and
        // mutate _staticTurretBots directly — no separate snapshot needed
        // because the only iterator here is this loop.
        lock (_staticTurretGlobalLock)
        {
            for (int i = _staticTurretBots.Count - 1; i >= 0; i--)
            {
                if (_staticTurretBots[i].Arena == arena)
                {
                    RemoveBotInternal_StaticTurret(_staticTurretBots[i]);
                    _staticTurretBots.RemoveAt(i);
                }
            }
        }

        if (arena.TryGetExtraData(_adKey, out ArenaData? ad))
        {
            ad.StaticTurretInitialized = false;
            ad.StaticTurretTypes.Clear();
        }
    }

    private void OnArenaAction_StaticTurret(Arena arena, ArenaAction action)
    {
        // ConfChanged can land mid-game (admin edits arena.conf and reloads).
        // Re-parsing wipes the type registry so any in-flight bots whose
        // TurretType reference has been swapped will safely keep running on
        // the old reference until they die (the per-instance bot.TurretType
        // captured at AddBot time stays valid).
        if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            ReadStaticTurretArenaSettings(arena);
    }

    // -------------------------------------------------------------------------
    // CONF READER — `[SectorWar]` (StaticTurret-prefixed) + `[staticturret_<key>]`
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parse `[SectorWar]` StaticTurret* keys and the `[staticturret_<key>]`
    /// per-type sections. Idempotent — wipes the type registry first so a
    /// ConfChanged callback re-parses cleanly.
    /// </summary>
    private void ReadStaticTurretArenaSettings(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        ConfigHandle? cfg = arena.Cfg;
        if (cfg is null) return;

        // Wipe and re-parse — see OnArenaAction_StaticTurret comment for
        // the in-flight-bot story.
        ad.StaticTurretTypes.Clear();

        ad.StaticTurretHosted = _configManager.GetInt(cfg, ConfSection, "StaticTurretHosted", 0) != 0;
        ad.StaticTurretMaxBots = _configManager.GetInt(cfg, ConfSection, "StaticTurretMaxBots", -1);
        // ASSS uses 1-based ship indices; subtract 1 to match ShipType enum (0..7).
        ad.StaticTurretShipFavour = _configManager.GetInt(cfg, ConfSection, "StaticTurretShipFavour", 0) - 1;
        ad.StaticTurretTurretPlacementRange =
            _configManager.GetInt(cfg, ConfSection, "StaticTurretTurretPlacementRange", 0);

        // Parse turret0..turret99 — first gap stops parsing (matches ASSS).
        // why: ASSS treats a missing key as "end of list", not "skip and try
        // turretN+1". Mirroring this behavior keeps existing zone configs
        // working without surprise gaps.
        for (int i = 0; i < 100; i++)
        {
            string? key = _configManager.GetStr(cfg, ConfSection, $"StaticTurretTurret{i}");
            if (string.IsNullOrEmpty(key)) break;

            if (ad.StaticTurretTypes.ContainsKey(key))
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"Turret with key '{key}' already defined while parsing " +
                    $"{ConfSection}:StaticTurretTurret{i}");
                continue;
            }

            // Per Hard rule 7: `[staticturret_<key>]` sections stay AS-IS;
            // they're the public turret-type registry surface.
            string section = $"staticturret_{key}";
            var tt = new StaticTurretType
            {
                Key = key,
                Name = _configManager.GetStr(cfg, section, "Name") ?? "~Turret",

                Energy = _configManager.GetInt(cfg, section, "Energy", 1000),
                Recharge = _configManager.GetInt(cfg, section, "Recharge", 1150),
                BuildSpeed = _configManager.GetInt(cfg, section, "BuildSpeed", 1000),
                WeaponType = (byte)_configManager.GetInt(cfg, section, "WeaponType", (int)WeaponCodes.Bullet),
                WeaponLevel = _configManager.GetInt(cfg, section, "WeaponLevel", 1) - 1,
                WeaponDelay = _configManager.GetInt(cfg, section, "WeaponDelay", 100),
                WeaponFireEnergy = _configManager.GetInt(cfg, section, "WeaponFireEnergy", 0),
                WeaponSightPixels = _configManager.GetInt(cfg, section, "WeaponSightPixels", 160),
                WeaponShrapnelLevel = _configManager.GetInt(cfg, section, "WeaponShrapnelLevel", 1) - 1,
                WeaponShrapnelCount = _configManager.GetInt(cfg, section, "WeaponShrapnelCount", 0),
                WeaponShrapnelBouncing = _configManager.GetInt(cfg, section, "WeaponShrapnelBouncing", 0) != 0,
                WeaponMultifire = _configManager.GetInt(cfg, section, "WeaponMultifire", 0) != 0,
                WeaponWaitForGoodShot = _configManager.GetInt(cfg, section, "WeaponWaitForGoodShot", 0) != 0,
                RotationSpeed = _configManager.GetInt(cfg, section, "RotationSpeed", -1),
                Timeout = _configManager.GetInt(cfg, section, "Timeout", 1500),
                RespawnDelay = _configManager.GetInt(cfg, section, "RespawnDelay", 6000),
                XRadar = _configManager.GetInt(cfg, section, "XRadar", 1) != 0,
                RequiredPower = _configManager.GetInt(cfg, section, "RequiredPower", 0),
                RespawnCount = _configManager.GetInt(cfg, section, "RespawnCount", 1),
                Ufo = _configManager.GetInt(cfg, section, "Ufo", 0) != 0,
                Bounty = _configManager.GetInt(cfg, section, "Bounty", 0),
                MaxBots = _configManager.GetInt(cfg, section, "MaxBots", -1),
                DoBuildSequence = _configManager.GetInt(cfg, section, "DoBuildSequence", 0) != 0,
                Ship = (byte)Math.Clamp(_configManager.GetInt(cfg, section, "Ship", 1) - 1, 0, 7),
            };

            // Clamp weapon type to known SubSpace codes — out-of-range values
            // would crash the client when the position packet renders.
            if (tt.WeaponType > (byte)WeaponCodes.Thor) tt.WeaponType = (byte)WeaponCodes.Bullet;

            // Wave-fix: per-turret ShipRadius override read from the turret's
            // OWN section, not the arena-wide [<Ship>] Radius. Hyperspace-style
            // configs set [<Ship>] Radius to 255 as a per-player CAP (not a
            // hitbox), which makes server-side hit detection absurdly large
            // if we use it for collision.
            tt.ShipRadius = _configManager.GetInt(cfg, section, "ShipRadius", 14);
            if (tt.ShipRadius <= 0) tt.ShipRadius = 14;

            // LVZ cannon overlay (Phase 5b-C). -1 disables the overlay and
            // keeps the visible ship sprite. >= 0 hides the ship + draws the
            // configured cannon image at the bot's center.
            tt.OverlayImageIndex = _configManager.GetInt(cfg, section, "OverlayImageIndex", -1);
            tt.OverlayImageRotationCount = Math.Max(1,
                _configManager.GetInt(cfg, section, "OverlayImageRotationCount", 1));

            // Wave-fix: fall back to SS.NET-conventional [<Ship>] BulletSpeed/
            // BombSpeed for projSpeed lookup so the turret's lead-prediction
            // matches what the client actually simulates.
            string shipSection = ((ShipType)tt.Ship).ToString();
            WeaponCodes wc = (WeaponCodes)tt.WeaponType;
            if (wc == WeaponCodes.Bullet || wc == WeaponCodes.BounceBullet)
            {
                tt.WeaponSpeed = _configManager.GetInt(cfg, shipSection, "BulletSpeed", 3000);
            }
            else if (wc == WeaponCodes.Bomb || wc == WeaponCodes.ProxBomb || wc == WeaponCodes.Thor)
            {
                tt.WeaponSpeed = _configManager.GetInt(cfg, shipSection, "BombSpeed", 2000);
                // Wave-fix: bombs can't multifire (matches the ASSS invariant).
                tt.WeaponMultifire = false;
            }
            else
            {
                tt.WeaponSpeed = 2500;
                tt.WeaponMultifire = false;
            }

            ad.StaticTurretTypes[key] = tt;
        }

        ad.StaticTurretInitialized = true;
    }

    // -------------------------------------------------------------------------
    // IStaticTurret — public surface for Pylon, ArenaDefenses, BossEncounter,
    // StationDeployer, DevCommands.
    // -------------------------------------------------------------------------

    /// <summary>Begin the turret game in this arena. Clears power state and auto-spawns from `StaticTurretSpawn0..99`.</summary>
    void IStaticTurret.StartGame(Arena arena) => StartGame_StaticTurret(arena);

    /// <summary>End the turret game in this arena. Removes all turrets in this arena.</summary>
    void IStaticTurret.StopGame(Arena arena) => StopGame_StaticTurret(arena);

    /// <summary>Spawn a turret at world coords (x, y) on the given freq. See <see cref="AddBotResult"/>.</summary>
    AddBotResult IStaticTurret.AddBot(Arena arena, string typeKey, int x, int y, short freq,
        bool infiniteRespawn, bool noLocationCheck)
    {
        return AddBotByKey_StaticTurret(arena, typeKey, x, y, freq, infiniteRespawn, noLocationCheck);
    }

    /// <summary>Pause/resume respawning for all turrets on the given freq. Out-of-range freqs are silently ignored.</summary>
    void IStaticTurret.FreezeRespawn(Arena arena, short freq, bool freeze)
    {
        if (freq < 0 || freq >= StaticTurretStructuresFreqs) return;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        lock (_staticTurretGlobalLock)
        {
            ad.StaticTurretRespawnFrozen[freq] = freeze;
            uint now = (uint)Environment.TickCount;
            if (freeze) ad.StaticTurretRespawnFrozenTime[freq] = now;
            else ad.StaticTurretRespawnUnfrozenTime[freq] = now;
        }
    }

    /// <summary>Set the power resource for a freq. Must be called at least once for any freq using turrets.</summary>
    void IStaticTurret.SetPower(Arena arena, short freq, int power)
    {
        if (freq < 0 || freq >= StaticTurretStructuresFreqs) return;
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        lock (_staticTurretGlobalLock)
        {
            ad.StaticTurretPower[freq] = power;
            ad.StaticTurretPowerSet[freq] = true;
        }
    }

    /// <summary>
    /// Apply server-authoritative splash damage. DEFERRED until full asss-damage
    /// parity (splash radius support) lands. Currently a no-op stub that logs
    /// at Drivel level so callers (Pylon detonations, etc.) see the path is
    /// being hit but no damage is applied.
    /// </summary>
    void IStaticTurret.DoDamage(Arena arena, Player killer, int x, int y, int damage, int radius, short immuneFreq)
    {
        _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
            $"DoDamage stub: ({x},{y}) dmg={damage} radius={radius} immune={immuneFreq} " +
            "(deferred until asss-damage splash port lands)");
    }

    /// <summary>Nuke every bot in the given arena. Returns count removed.</summary>
    int IStaticTurret.RemoveAllBots(Arena arena)
    {
        int removed = 0;
        lock (_staticTurretGlobalLock)
        {
            // Wave-style: walk back-to-front so the in-place removal doesn't
            // shift indexes underneath us. RemoveBotInternal_StaticTurret
            // doesn't touch _staticTurretBots itself (caller's responsibility),
            // so we explicitly RemoveAt(i) here.
            for (int i = _staticTurretBots.Count - 1; i >= 0; i--)
            {
                StaticTurretBotData bot = _staticTurretBots[i];
                if (bot.Arena != arena) continue;
                _staticTurretBots.RemoveAt(i);
                RemoveBotInternal_StaticTurret(bot);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>Move an existing bot to a new pixel position. Updates internal
    /// PixelX/PixelY and broadcasts a fresh position packet so clients see the
    /// teleport without losing the fake-player F2 entry. Returns true if a
    /// matching bot was found and moved.</summary>
    bool IStaticTurret.MoveBot(Arena arena, int oldPixelX, int oldPixelY, short freq,
        string? turretKey, int newPixelX, int newPixelY)
    {
        StaticTurretBotData? toMove = null;
        lock (_staticTurretGlobalLock)
        {
            foreach (var bot in _staticTurretBots)
            {
                if (bot.Arena != arena) continue;
                if (bot.Freq != freq) continue;
                if (bot.PixelX != oldPixelX || bot.PixelY != oldPixelY) continue;
                if (turretKey is not null
                    && !string.Equals(bot.TurretType.Key, turretKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                toMove = bot;
                break;
            }
            if (toMove is null) return false;

            toMove.PixelX = newPixelX;
            toMove.PixelY = newPixelY;
        }
        // Position broadcast outside the lock — FakePosition can call into
        // network code; nesting global-lock under that risks deadlock.
        SendPositionUpdate_StaticTurret(toMove, fireWeapon: false);

        // If this bot has an LVZ cannon overlay, the overlay was positioned
        // at AddBot-time and stays glued there. Re-position it to the new
        // bridge so a moving turret (HQ capital cannons) drags its visible
        // cannon along with it.
        if (toMove.CannonLvzId >= StaticTurretCannonPoolStart)
        {
            short bx = (short)(newPixelX - StaticTurretCannonHalfSize);
            short by = (short)(newPixelY - StaticTurretCannonHalfSize);
            try
            {
                _lvzObjects.SetPosition(arena, toMove.CannonLvzId, bx, by,
                    ScreenOffset.Normal, ScreenOffset.Normal);
            }
            catch { /* phong's no-crash rule */ }
        }
        return true;
    }

    /// <summary>Count live bots in <paramref name="arena"/> matching <paramref name="freq"/>
    /// (and optional <paramref name="turretKey"/>). Used by RoundManager to gate
    /// sudden-death on "all defenders cleared" before promoting a capital kill
    /// to a round-end.</summary>
    int IStaticTurret.CountBots(Arena arena, short freq, string? turretKey)
    {
        int count = 0;
        lock (_staticTurretGlobalLock)
        {
            foreach (var bot in _staticTurretBots)
            {
                if (bot.Arena != arena) continue;
                if (bot.Freq != freq) continue;
                if (bot.Killed) continue;
                if (turretKey is not null
                    && !string.Equals(bot.TurretType.Key, turretKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                count++;
            }
        }
        return count;
    }

    /// <summary>Remove a single bot matching position + freq (and optional turret-type key).</summary>
    bool IStaticTurret.RemoveBotAt(Arena arena, int pixelX, int pixelY, short freq, string? turretKey)
    {
        // Linear scan under the global lock. Static turrets don't move, so an
        // exact (PixelX, PixelY) match is correct — no fuzzy radius needed.
        StaticTurretBotData? toRemove = null;
        lock (_staticTurretGlobalLock)
        {
            foreach (var bot in _staticTurretBots)
            {
                if (bot.Arena != arena) continue;
                if (bot.Freq != freq) continue;
                if (bot.PixelX != pixelX || bot.PixelY != pixelY) continue;
                if (turretKey is not null
                    && !string.Equals(bot.TurretType.Key, turretKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                toRemove = bot;
                break;
            }
            if (toRemove is not null)
            {
                _staticTurretBots.Remove(toRemove);
                RemoveBotInternal_StaticTurret(toRemove);
            }
        }
        return toRemove is not null;
    }

    // -------------------------------------------------------------------------
    // StartGame / StopGame
    // -------------------------------------------------------------------------

    private void StartGame_StaticTurret(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (!ad.StaticTurretInitialized) return;

        ConfigHandle? cfg = arena.Cfg;
        if (cfg is null) return;

        // Reset power + freeze state for all configured freqs.
        for (int i = 0; i < StaticTurretStructuresFreqs; i++)
        {
            ad.StaticTurretPower[i] = 0;
            ad.StaticTurretPowerSet[i] = false;
            ad.StaticTurretRespawnFrozen[i] = false;
            ad.StaticTurretRespawnFrozenTime[i] = 0;
            ad.StaticTurretRespawnUnfrozenTime[i] = 0;
        }

        // Auto-spawn from StaticTurretSpawn0..StaticTurretSpawn99. Format:
        // `tx, ty, freq, type` — tile coords get converted to pixel center
        // via (tile << 4) + 8.
        for (int i = 0; i < 100; i++)
        {
            string? raw = _configManager.GetStr(cfg, ConfSection, $"StaticTurretSpawn{i}");
            if (string.IsNullOrWhiteSpace(raw)) break;

            string[] parts = raw.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4)
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"Invalid format for {ConfSection}:StaticTurretSpawn{i}. Use: x, y, freq, type");
                continue;
            }

            if (!int.TryParse(parts[0], out int tx) ||
                !int.TryParse(parts[1], out int ty) ||
                !short.TryParse(parts[2], out short freq))
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"Invalid numeric values for {ConfSection}:StaticTurretSpawn{i}");
                continue;
            }

            if (freq < 0 || freq > 9999)
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"Invalid freq for {ConfSection}:StaticTurretSpawn{i}");
                continue;
            }

            string typeKey = parts[3];
            // Tile coords -> pixel center: (tile << 4) + 8.
            AddBotByKey_StaticTurret(arena, typeKey, (tx << 4) + 8, (ty << 4) + 8, freq, true, true);
        }

        ad.StaticTurretRunning = true;
        _logManager.LogA(LogLevel.Info, LogCategory, arena,
            $"StaticTurret game started ({_staticTurretBots.Count} bots).");
    }

    private void StopGame_StaticTurret(Arena arena)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;
        if (!ad.StaticTurretInitialized) return;

        for (int i = 0; i < StaticTurretStructuresFreqs; i++)
        {
            ad.StaticTurretPower[i] = 0;
            ad.StaticTurretPowerSet[i] = false;
            ad.StaticTurretRespawnFrozen[i] = false;
            ad.StaticTurretRespawnFrozenTime[i] = 0;
            ad.StaticTurretRespawnUnfrozenTime[i] = 0;
        }

        // Drop all bots that belong to this arena (back-to-front index walk —
        // see DetachStaticTurret comment).
        lock (_staticTurretGlobalLock)
        {
            for (int i = _staticTurretBots.Count - 1; i >= 0; i--)
            {
                if (_staticTurretBots[i].Arena == arena)
                {
                    RemoveBotInternal_StaticTurret(_staticTurretBots[i]);
                    _staticTurretBots.RemoveAt(i);
                }
            }
        }

        ad.StaticTurretRunning = false;
        _logManager.LogA(LogLevel.Info, LogCategory, arena, "StaticTurret game stopped.");
    }

    // -------------------------------------------------------------------------
    // AddBot / RemoveBot
    // -------------------------------------------------------------------------

    private AddBotResult AddBotByKey_StaticTurret(Arena arena, string typeKey, int x, int y,
        short freq, bool infiniteRespawn, bool noLocationCheck)
    {
        if (!arena.TryGetExtraData(_adKey, out ArenaData? ad) || !ad.StaticTurretInitialized)
            return AddBotResult.IllegalArena;

        if (!ad.StaticTurretTypes.TryGetValue(typeKey, out StaticTurretType? tt))
            return AddBotResult.UnknownType;

        return AddBot_StaticTurret(arena, ad, tt, x, y, freq, infiniteRespawn, noLocationCheck);
    }

    private AddBotResult AddBot_StaticTurret(Arena arena, ArenaData ad, StaticTurretType tt,
        int x, int y, short freq, bool infiniteRespawn, bool noLocationCheck)
    {
        if (freq < 0 || freq > 9999) return AddBotResult.IllegalArena;

        // Pixel x clamp (matches ASSS CLIP(x, 0, 1023 << 4)).
        x = Math.Clamp(x, 0, 1023 << 4);

        lock (_staticTurretGlobalLock)
        {
            // Per-type quota.
            if (tt.MaxBots >= 0 && tt.BotCount >= tt.MaxBots)
                return AddBotResult.MaxReachedForBotType;

            // Arena-wide quota.
            if (ad.StaticTurretMaxBots >= 0 && ad.StaticTurretBotCount > ad.StaticTurretMaxBots)
                return AddBotResult.MaxReachedForArena;

            // Placement proximity check (skip when caller is auto-spawning from conf).
            if (!noLocationCheck && ad.StaticTurretTurretPlacementRange > 0)
            {
                foreach (StaticTurretBotData other in _staticTurretBots)
                {
                    if (other.Arena == arena)
                    {
                        long dx = x - other.PixelX;
                        long dy = y - other.PixelY;
                        long distSq = dx * dx + dy * dy;
                        long rangeSq = (long)ad.StaticTurretTurretPlacementRange
                                     * ad.StaticTurretTurretPlacementRange;
                        if (distSq < rangeSq)
                            return AddBotResult.TooCloseToOtherBot;
                    }
                }
            }

            // Solid-tile / safe-zone check.
            if (!FitsOnMap_StaticTurret(arena, x, y, tt.ShipRadius))
                return AddBotResult.CanNotBePlacedOnMap;

            // Spawn the fake.
            ShipType shipEnum = (ShipType)tt.Ship;
            Player? fakePlayer = _fake.CreateFakePlayer(tt.Name, arena, shipEnum, freq);
            if (fakePlayer is null) return AddBotResult.IllegalArena;

            uint nowTicks = (uint)Environment.TickCount;
            var bot = new StaticTurretBotData
            {
                Player = fakePlayer,
                Arena = arena,
                TurretType = tt,
                PixelX = x,
                PixelY = y,
                Freq = freq,
                InfiniteRespawn = infiniteRespawn,
                Energy = tt.Energy,
                Spawns = 1,
                CreatedOn = nowTicks,
                LastFire = nowTicks,
                LastRecharge = nowTicks,
                LastBuild = nowTicks,
                LastPositionUpdate = nowTicks,
                BuildSequence = tt.DoBuildSequence && !infiniteRespawn,
                BuildPoints = 0,
            };

            _staticTurretBots.Add(bot);
            tt.BotCount++;
            ad.StaticTurretBotCount++;

            // Send the initial position so clients see the turret.
            SendInitialPosition_StaticTurret(bot);

            // Register with IDamage so server-side bullet collision routes
            // hits to OnBotDamaged_StaticTurret. manageEnergy=false because
            // we own Energy/Recharge here — damageFunc subtracts and handles
            // its own death path.
            //
            // Wave-fix: re-check _damageToken at AddBot time. Belt-and-braces
            // even though the umbrella's IAsyncModule.LoadAsync orders Damage
            // before StaticTurret — config edits or future load reorders
            // shouldn't silently leak invulnerable bots.
            if (!_staticTurretDamageInterfaceAvailable && _damageToken is not null)
                _staticTurretDamageInterfaceAvailable = true;

            if (_staticTurretDamageInterfaceAvailable && bot.Player is not null)
            {
                bot.DamagePos = default;
                bot.DamagePos.Type = 0x03;
                bot.DamagePos.X = (short)bot.PixelX;
                bot.DamagePos.Y = (short)bot.PixelY;
                bot.DamagePos.XSpeed = 0;
                bot.DamagePos.YSpeed = 0;
                bot.DamagePos.Time = ServerTick.Now;

                // why: IDamage is implemented by THIS partial class (see
                // SectorWar.Damage.cs). No broker dance needed — direct
                // interface dispatch.
                ((IDamage)this).AddFake(bot.Player, ref bot.DamagePos,
                    manageEnergy: false,
                    killFunc: null,                    // we trigger death in damageFunc
                    respawnFunc: null,
                    damageFunc: OnBotDamaged_StaticTurret,
                    closure: bot,
                    radiusOverride: tt.ShipRadius);
                bot.RegisteredForDamage = true;
            }
            else
            {
                _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                    $"Spawned turret '{tt.Key}' but IDamage unavailable — bot is invulnerable.");
            }

            // Phase 5b-C: turret cannon LVZ overlay. If configured, allocate
            // a slot from the per-arena pool, position it at the bot's
            // center, set the per-type image, toggle on. The bot's ship
            // sprite is already cloaked via SendInitialPosition's
            // Cloak+Stealth+UFO bits.
            if (tt.OverlayImageIndex >= 0)
            {
                short cannonId = AllocateCannonSlot_StaticTurret(ad);
                if (cannonId >= StaticTurretCannonPoolStart)
                {
                    bot.CannonLvzId = cannonId;
                    short bx = (short)(bot.PixelX - StaticTurretCannonHalfSize);
                    short by = (short)(bot.PixelY - StaticTurretCannonHalfSize);
                    _lvzObjects.SetPosition(arena, cannonId, bx, by,
                        ScreenOffset.Normal, ScreenOffset.Normal);
                    _lvzObjects.SetImage(arena, cannonId, (byte)tt.OverlayImageIndex);
                    _lvzObjects.Toggle(arena, cannonId, true);
                }
                else
                {
                    _logManager.LogA(LogLevel.Warn, LogCategory, arena,
                        $"Cannon LVZ pool exhausted for turret '{tt.Key}' — overlay skipped.");
                }
            }

            _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
                $"Spawned turret '{tt.Key}' at ({x >> 4},{y >> 4}) on freq {freq}.");

            return AddBotResult.Ok;
        }
    }

    /// <summary>
    /// Lazy-initialize the per-arena cannon LVZ pool on first allocation.
    /// Pool covers ids 9400..9499 (100 slots). Returns -1 when exhausted.
    /// </summary>
    private short AllocateCannonSlot_StaticTurret(ArenaData ad)
    {
        if (!ad.StaticTurretCannonPoolInitialized)
        {
            // Push high-to-low so Pop returns the lowest id first — keeps
            // assigned ids monotonic for easier debugging.
            for (short id = StaticTurretCannonPoolEnd; id >= StaticTurretCannonPoolStart; id--)
                ad.StaticTurretFreeCannonIds.Push(id);
            ad.StaticTurretCannonPoolInitialized = true;
        }
        return ad.StaticTurretFreeCannonIds.Count > 0
            ? ad.StaticTurretFreeCannonIds.Pop()
            : (short)-1;
    }

    /// <summary>
    /// Tear down a bot's fake-player + IDamage registration + LVZ slot. Caller
    /// holds <see cref="_staticTurretGlobalLock"/> AND is responsible for
    /// removing the bot from <c>_staticTurretBots</c> itself.
    /// </summary>
    private void RemoveBotInternal_StaticTurret(StaticTurretBotData bot)
    {
        if (bot.Player is not null)
        {
            if (bot.RegisteredForDamage && _staticTurretDamageInterfaceAvailable)
            {
                ((IDamage)this).RemoveFake(bot.Player);
                bot.RegisteredForDamage = false;
            }
            _fake.EndFaked(bot.Player);
            bot.Player = null;
        }

        // Hide the cannon LVZ + return its slot to the per-arena pool.
        if (bot.CannonLvzId >= StaticTurretCannonPoolStart
            && bot.Arena.TryGetExtraData(_adKey, out ArenaData? cAd))
        {
            _lvzObjects.Toggle(bot.Arena, bot.CannonLvzId, false);
            cAd.StaticTurretFreeCannonIds.Push(bot.CannonLvzId);
            bot.CannonLvzId = -1;
        }

        bot.TurretType.BotCount--;
        if (bot.Arena.TryGetExtraData(_adKey, out ArenaData? ad))
            ad.StaticTurretBotCount--;
    }

    /// <summary>
    /// IDamage damage callback. Decrements bot.Energy; on death, fires
    /// BotKilled event and tears down the fake-player so it visibly dies on
    /// clients. Runs on the IDamage tick thread (mainloop).
    /// </summary>
    /// <remarks>
    /// Wave-fix: <c>bot.Killed</c> short-circuits late hits. Per the IDamage
    /// threading-contract, the Damage tick snapshots its bullet list before
    /// iterating — so multiple bullets in the same tick can call this for the
    /// same bot AFTER the first hit triggered death.
    /// </remarks>
    private void OnBotDamaged_StaticTurret(Player fake, Player firedBy, int dist, int damage,
        WeaponCodes wtype, int level, bool bouncing, int empTime, object? closure)
    {
        if (closure is not StaticTurretBotData bot) return;
        if (bot.Killed) return;             // Wave-fix: ignore late hits
        if (bot.Player is null) return;

        bot.Energy -= damage;

        // Fire BotDamaged before the lethal-check so subscribers see every hit
        // (including the fatal one). Snapshot identity here in case subscribers
        // hold refs across the death path below.
        BotDamaged?.Invoke(bot.Arena, bot.TurretType.Key,
            bot.PixelX, bot.PixelY, bot.Freq, firedBy);

        if (bot.Energy > 0) return;

        // Death.
        bot.Energy = 0;
        bot.Killed = true;
        bot.DeathOn = ServerTick.Now;

        // Snapshot identity BEFORE teardown — subscribers may hold these
        // references across the BotKilled invocation.
        Arena arena = bot.Arena;
        string turretKey = bot.TurretType.Key;
        int x = bot.PixelX;
        int y = bot.PixelY;
        short freq = bot.Freq;

        // Visible death on clients. FakeKill credits firedBy.
        _game.FakeKill(firedBy, bot.Player, pts: 0, flags: 0);

        // Remove from IDamage tracking + remove the bot itself. Take the lock
        // explicitly here — OnBotDamaged is called from the Damage tick
        // OUTSIDE its snapshot iteration, but our _staticTurretBots needs its
        // own lock either way.
        lock (_staticTurretGlobalLock)
        {
            if (_staticTurretBots.Remove(bot))
                RemoveBotInternal_StaticTurret(bot);
        }

        // Drivel — every bot kill emits one of these; in heavy combat with
        // 12+ HQ fakes per freq + warstation defenders, dozens fire per
        // round. Subscribers (Pylon / StationDeployer / Hq / RoundManager)
        // re-log at Info on actually-significant kills (capital,
        // structure-destroyed, etc.) so the channel still carries the
        // round-shaping events.
        _logManager.LogA(LogLevel.Drivel, LogCategory, arena,
            $"Turret '{turretKey}' killed at ({x >> 4},{y >> 4}) freq {freq} by {firedBy.Name}.");

        BotKilled?.Invoke(arena, turretKey, x, y, freq, firedBy);
    }

    /// <summary>
    /// Send the bot's initial position packet so clients render it. When the
    /// turret type has an LVZ cannon overlay configured, the ship sprite is
    /// hidden via Cloak+Stealth+UFO so only the cannon graphic shows.
    /// </summary>
    private void SendInitialPosition_StaticTurret(StaticTurretBotData bot)
    {
        if (bot.Player is null) return;

        C2S_PositionPacket pkt = default;
        pkt.Type = 0x03;
        pkt.X = (short)bot.PixelX;
        pkt.Y = (short)bot.PixelY;
        pkt.XSpeed = 0;
        pkt.YSpeed = 0;
        pkt.Rotation = 0;
        pkt.Bounty = (ushort)bot.TurretType.Bounty;
        pkt.Energy = (short)Math.Min(bot.Energy, short.MaxValue);
        pkt.Time = ServerTick.Now;
        // Cloak ship sprite when LVZ cannon overlay is configured. Otherwise
        // honour the legacy Ufo flag (treated as a "fully invisible" hint by
        // the original conf design).
        // Cloak hides the ship sprite (LVZ cannon graphic shows in its place).
        // UFO frees the bot from wall physics. Stealth INTENTIONALLY OMITTED
        // so the energy bar + name still display above the cannon overlay —
        // players need to see HP draining to know they're hurting the turret.
        if (bot.TurretType.OverlayImageIndex >= 0 || bot.TurretType.Ufo)
            pkt.Status = PlayerPositionStatus.Cloak | PlayerPositionStatus.Ufo;

        _game.FakePosition(bot.Player, ref pkt);
    }

    // -------------------------------------------------------------------------
    // Map / line-of-sight helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Check that a turret-shaped pixel rectangle (radius&#xD7;radius around the
    /// center) doesn't intersect a solid tile or a safe zone (tile 171).
    /// ASSS solid range: 1-169 (incl. doors), 191-252 (special solids/warps).
    /// </summary>
    private bool FitsOnMap_StaticTurret(Arena arena, int pixelX, int pixelY, int radius)
    {
        int startTileX = (pixelX - radius) >> 4;
        int endTileX = (pixelX + radius) >> 4;
        int startTileY = (pixelY - radius) >> 4;
        int endTileY = (pixelY + radius) >> 4;

        for (short tx = (short)startTileX; tx <= endTileX; tx++)
        {
            for (short ty = (short)startTileY; ty <= endTileY; ty++)
            {
                MapTile tile = _mapData.GetTile(arena, new TileCoordinates(tx, ty));
                byte b = (byte)tile;
                if ((b >= 1 && b <= 169) ||
                    (b >= 191 && b <= 252) ||
                    b == 171)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Bresenham line-of-sight check using TILE coords (x1,y1) -&gt; (x2,y2).
    /// Mirrors ASSS is_pathclear. <paramref name="isThor"/> relaxes the test
    /// (only blocks on tile 242 / 220 = wormhole).
    /// </summary>
    private bool IsPathClear_StaticTurret(Arena arena, int x1, int y1, int x2, int y2, bool isThor)
    {
        int dx = Math.Abs(x2 - x1);
        int dy = Math.Abs(y2 - y1);

        int numpixels, d, dinc1, dinc2, xinc1, xinc2, yinc1, yinc2;
        if (dx > dy)
        {
            numpixels = dx + 1;
            d = (2 * dy) - dx;
            dinc1 = dy << 1;
            dinc2 = (dy - dx) << 1;
            xinc1 = 1; xinc2 = 1; yinc1 = 0; yinc2 = 1;
        }
        else
        {
            numpixels = dy + 1;
            d = (2 * dx) - dy;
            dinc1 = dx << 1;
            dinc2 = (dx - dy) << 1;
            xinc1 = 0; xinc2 = 1; yinc1 = 1; yinc2 = 1;
        }

        if (x1 > x2) { xinc1 = -xinc1; xinc2 = -xinc2; }
        if (y1 > y2) { yinc1 = -yinc1; yinc2 = -yinc2; }

        int x = x1, y = y1;
        for (int i = 1; i < numpixels; i++)
        {
            MapTile tile = _mapData.GetTile(arena, new TileCoordinates((short)x, (short)y));
            byte b = (byte)tile;
            if (isThor)
            {
                if (b == 242 || b == 220) return false;
            }
            else
            {
                if ((b >= 1 && b <= 161) || (b >= 191 && b <= 251))
                    return false;
            }

            if (d < 0)
            {
                d += dinc1;
                x += xinc1;
                y += yinc1;
            }
            else
            {
                d += dinc2;
                x += xinc2;
                y += yinc2;
            }
        }
        return true;
    }

    // -------------------------------------------------------------------------
    // Targeting / fire control
    // -------------------------------------------------------------------------

    /// <summary>Convert a (dx, dy) vector into a SubSpace 0..39 rotation index. Mirrors ASSS FireAngle.</summary>
    private static byte FireAngle_StaticTurret(double x, double y)
    {
        // [-Pi, Pi] + Pi -> [0, 2Pi]
        double angle = Math.Atan2(y, x) + StaticTurretPi;
        int a = (int)Math.Round(angle * 40.0 / (2.0 * StaticTurretPi) + 30);
        return (byte)(((a % 40) + 40) % 40);
    }

    /// <summary>
    /// Lead-prediction fire-control. Solves for a rotation index that will
    /// hit a target at <c>(dstX, dstY)</c> moving at <c>(dxSpeed, dySpeed)</c>
    /// with a projectile of speed <paramref name="projSpeed"/>. Mirrors ASSS
    /// FireControl (staticturret.c:1519-1541).
    /// </summary>
    private static byte FireControl_StaticTurret(double srcX, double srcY,
        double dstX, double dstY, double dxSpeed, double dySpeed, double projSpeed)
    {
        if (projSpeed <= 0) projSpeed = 2000;

        double bestDx = dstX - srcX;
        double bestDy = dstY - srcY;
        double bestErr = 20000.0;
        double tt = 10, pt;
        do
        {
            double dx = (dstX + dxSpeed * tt / 1000.0) - srcX;
            double dy = (dstY + dySpeed * tt / 1000.0) - srcY;
            pt = Math.Sqrt(dx * dx + dy * dy) * 1000.0 / projSpeed;
            double err = Math.Abs(pt - tt);
            if (err < bestErr)
            {
                bestErr = err;
                bestDx = dx;
                bestDy = dy;
            }
            else if (err > bestErr)
            {
                break;
            }
            tt += 10;
        } while (pt > tt && tt <= 250);

        return FireAngle_StaticTurret(bestDx, bestDy);
    }

    /// <summary>
    /// Find the closest enemy player within sight range, applying ASSS-style
    /// favours (humans &gt; bots, non-cloak &gt; cloak, ShipFavour bias).
    /// Returns null if nothing in line-of-sight + range.
    /// </summary>
    private Player? GetNearestPlayer_StaticTurret(StaticTurretBotData bot, ArenaData ad, bool enemyOnly)
    {
        if (bot.Player is null) return null;
        Arena arena = bot.Arena;
        Player? best = null;
        long bestDist = long.MaxValue;
        // Sticky target tracking — record the previous target's current
        // distance + LOS-clear state so we can apply stickiness at the end.
        Player? sticky = bot.Targeting;
        long stickyDist = long.MaxValue;
        bool stickyValid = false;
        long sightSq = (long)bot.TurretType.WeaponSightPixels * bot.TurretType.WeaponSightPixels;

        _playerData.Lock();
        try
        {
            foreach (Player p in _playerData.Players)
            {
                if (p.Arena != arena) continue;
                if (p.Ship == ShipType.Spec) continue;
                if (enemyOnly && p.Freq == bot.Freq) continue;
                if (p.Flags.IsDead) continue;

                ref readonly var pos = ref p.Position;
                if ((pos.Status & PlayerPositionStatus.Safezone) != 0) continue;

                bool isUfo = (pos.Status & PlayerPositionStatus.Ufo) != 0;
                bool isCloak = (pos.Status & PlayerPositionStatus.Cloak) != 0;
                bool isStealth = (pos.Status & PlayerPositionStatus.Stealth) != 0;
                // Skip cloakers if turret has no XRadar — but always shoot UFO targets.
                if (!bot.TurretType.XRadar && !isUfo && isCloak && isStealth)
                    continue;

                // Project the player ahead by BotProjectFavour ticks.
                int x1 = pos.X + (pos.XSpeed * StaticTurretBotProjectFavour) / 1000;
                int y1 = pos.Y + (pos.YSpeed * StaticTurretBotProjectFavour) / 1000;

                long dx = bot.PixelX - x1;
                long dy = bot.PixelY - y1;
                long distSq = dx * dx + dy * dy;
                if (distSq > sightSq) continue;

                long dist = (long)Math.Sqrt(distSq);
                if (p.Type == ClientType.Fake) dist += StaticTurretBotHumanFavour;

                if (!isUfo)
                {
                    if (isCloak) dist += StaticTurretCloakFavour;
                    if (isStealth) dist += StaticTurretStealthFavour;
                }

                if ((int)p.Ship != ad.StaticTurretShipFavour)
                    dist += StaticTurretSpecificShipFavour;

                bool losClear = IsPathClear_StaticTurret(arena, bot.PixelX >> 4, bot.PixelY >> 4,
                    x1 >> 4, y1 >> 4,
                    bot.TurretType.WeaponType == (byte)WeaponCodes.Thor);

                // Track the sticky target's current distance — only counts if
                // sight + LOS still hold (else fall back to nearest).
                if (sticky is not null && ReferenceEquals(p, sticky) && losClear)
                {
                    stickyDist = dist;
                    stickyValid = true;
                }

                if (dist < bestDist && losClear)
                {
                    best = p;
                    bestDist = dist;
                }
            }
        }
        finally
        {
            _playerData.Unlock();
        }

        // Stickiness gate: if the previous target is still in sight + LOS,
        // stay on it unless a new candidate is significantly closer. Cuts
        // the per-tick jitter when two enemies are roughly equidistant
        // (capital + perimeter gun, players in formation, etc.).
        if (stickyValid && bestDist > (stickyDist * StaticTurretTargetStickyPercent) / 100)
            return sticky;
        return best;
    }

    // -------------------------------------------------------------------------
    // Tick / AI
    // -------------------------------------------------------------------------

    /// <summary>50ms AI tick: recharge / target / rotate / fire / position broadcast for every bot.</summary>
    private bool OnTick_StaticTurret()
    {
        lock (_staticTurretGlobalLock)
        {
            uint now = (uint)Environment.TickCount;
            for (int i = 0; i < _staticTurretBots.Count; i++)
                UpdateBot_StaticTurret(_staticTurretBots[i], now);
        }
        return true;
    }

    private void UpdateBot_StaticTurret(StaticTurretBotData bot, uint now)
    {
        if (bot.Player is null) return;
        if (bot.DeathOn != 0)
        {
            // Death/respawn handling deferred until full asss-damage parity.
            // Bots die on first kill and stay dead until DetachModule clears
            // them — see top-of-file note.
            return;
        }
        if (!bot.Arena.TryGetExtraData(_adKey, out ArenaData? ad)) return;

        StaticTurretType tt = bot.TurretType;

        // Recharge — paused during BuildSequence and during EMP shutdown.
        if (bot.Energy < tt.Energy && !bot.BuildSequence && now >= bot.EmpShutdownExpiresAt)
        {
            int diff = (tt.Recharge * (int)(now - bot.LastRecharge)) / 1000;
            if (diff > 0)
            {
                bot.Energy += diff;
                bot.LastRecharge = now;
            }
        }
        else
        {
            bot.LastRecharge = now;
        }

        if (bot.Energy > tt.Energy) bot.Energy = tt.Energy;

        // Build progress.
        if (bot.BuildSequence)
        {
            int diff = (tt.BuildSpeed * (int)(now - bot.LastBuild)) / 1000;
            if (diff > 0)
            {
                bot.BuildPoints += diff;
                bot.LastBuild = now;
                if (bot.BuildPoints > tt.Energy)
                {
                    bot.BuildSequence = false;
                }
            }
        }

        // Power gate + targeting. Freqs >= StaticTurretStructuresFreqs are
        // exempt from the power-resource check (matches ASSS — only "structure"
        // freqs 0/1/2 have a power resource).
        bool freqHasEnoughPower =
            bot.Freq >= StaticTurretStructuresFreqs ||
            !ad.StaticTurretPowerSet[bot.Freq] ||
            tt.RequiredPower <= ad.StaticTurretPower[bot.Freq];

        if (freqHasEnoughPower && !bot.BuildSequence)
        {
            Player? target = GetNearestPlayer_StaticTurret(bot, ad, enemyOnly: true);
            bot.Targeting = target;

            if (target is not null)
            {
                ref readonly var tpos = ref target.Position;
                bot.DesiredRotation = FireControl_StaticTurret(
                    bot.PixelX, bot.PixelY,
                    tpos.X, tpos.Y,
                    tpos.XSpeed, tpos.YSpeed,
                    tt.WeaponSpeed);
            }

            RotateBot_StaticTurret(bot);

            // Cannon overlay frame update — for rotation-aware cannons,
            // SetImage to the frame matching the bot's current rotation.
            // No-op for static cannons (RotationCount=1) and for bots
            // without a cannon overlay.
            UpdateCannonRotationFrame_StaticTurret(bot);

            // ASSS WeaponDelay is in 10ms ticks; convert to ms.
            if (target is not null &&
                (now - bot.LastFire) >= (uint)(tt.WeaponDelay * 10) &&
                bot.Energy >= tt.WeaponFireEnergy)
            {
                FireWeapon_StaticTurret(bot, now);
            }
        }

        // Periodic position broadcast (clients drop fakes that haven't
        // moved/talked in a while — keeps the turret visible).
        if ((now - bot.LastPositionUpdate) >= StaticTurretPositionPacketIntervalMs)
        {
            bot.LastPositionUpdate = now;
            SendPositionUpdate_StaticTurret(bot, fireWeapon: false);
        }
    }

    /// <summary>
    /// For cannon overlays with a rotation set (OverlayImageRotationCount &gt; 1),
    /// pick the frame matching the bot's current rotation and SetImage if it
    /// differs from the last broadcast. No-op for static-image cannons and
    /// bots without an overlay. Called once per AI tick from
    /// OnTick_StaticTurret after RotateBot_StaticTurret updates the bot's
    /// smoothed rotation.
    /// </summary>
    private void UpdateCannonRotationFrame_StaticTurret(StaticTurretBotData bot)
    {
        var tt = bot.TurretType;
        if (tt.OverlayImageRotationCount <= 1) return;
        if (tt.OverlayImageIndex < 0) return;
        if (bot.CannonLvzId < StaticTurretCannonPoolStart) return;

        // Continuum rotation is 0..39 (40 steps). Map to the frame set's
        // resolution (could be 8, 20, 40, ...). Frame 0 = barrel north,
        // frames advance clockwise.
        int rot40 = bot.ARotation / 1000;
        if (rot40 < 0) rot40 = 0;
        if (rot40 >= 40) rot40 = 39;
        int frame = (rot40 * tt.OverlayImageRotationCount) / 40;
        if (frame < 0) frame = 0;
        if (frame >= tt.OverlayImageRotationCount) frame = tt.OverlayImageRotationCount - 1;
        if (frame == bot.LastCannonFrame) return;

        try
        {
            _lvzObjects.SetImage(bot.Arena, bot.CannonLvzId,
                (byte)(tt.OverlayImageIndex + frame));
            bot.LastCannonFrame = frame;
        }
        catch { /* phong's no-crash rule */ }
    }

    /// <summary>Step the bot's smoothed rotation toward DesiredRotation. -1 RotationSpeed = instant.</summary>
    private static void RotateBot_StaticTurret(StaticTurretBotData bot)
    {
        int desired = bot.DesiredRotation * 1000;
        if (bot.ARotation == desired) return;

        int rotSpeed = bot.TurretType.RotationSpeed;
        if (rotSpeed < 0)
        {
            // -1 = instant rotation
            bot.ARotation = desired;
            return;
        }

        int currentRotation = bot.ARotation / 1000;
        // Pick the shorter direction around the 40-step circle.
        if (((bot.DesiredRotation - currentRotation + 40) % 40) < 20)
            bot.ARotation = (bot.ARotation + rotSpeed) % 40000;
        else
            bot.ARotation = (bot.ARotation + 40000 - rotSpeed) % 40000;
    }

    private void FireWeapon_StaticTurret(StaticTurretBotData bot, uint now)
    {
        if (bot.Player is null) return;
        StaticTurretType tt = bot.TurretType;

        bot.Energy -= tt.WeaponFireEnergy;
        if (bot.Energy < 0) bot.Energy = 0;     // clamp (matches ASSS)

        bot.LastFire = now;
        bot.LastPositionUpdate = now;

        SendPositionUpdate_StaticTurret(bot, fireWeapon: true);
    }

    private void SendPositionUpdate_StaticTurret(StaticTurretBotData bot, bool fireWeapon)
    {
        if (bot.Player is null) return;

        StaticTurretType tt = bot.TurretType;

        C2S_PositionPacket pkt = default;
        pkt.Type = 0x03;
        pkt.X = (short)bot.PixelX;
        pkt.Y = (short)bot.PixelY;
        pkt.XSpeed = 0;
        pkt.YSpeed = 0;
        pkt.Rotation = (sbyte)(bot.ARotation / 1000);
        pkt.Bounty = (ushort)tt.Bounty;
        pkt.Energy = (short)Math.Min(bot.Energy, short.MaxValue);
        pkt.Time = ServerTick.Now;
        // Cloak + UFO only — Stealth omitted so HP/name show above the cannon
        // overlay (players see turret HP drain). See SendInitialPosition_StaticTurret
        // for the same omission rationale.
        if (tt.Ufo || tt.OverlayImageIndex >= 0)
            pkt.Status = PlayerPositionStatus.Cloak | PlayerPositionStatus.Ufo;

        if (fireWeapon)
        {
            WeaponData w = default;
            w.Type = (WeaponCodes)tt.WeaponType;
            w.Level = (byte)tt.WeaponLevel;
            w.ShrapLevel = (byte)tt.WeaponShrapnelLevel;
            w.Shrap = (byte)tt.WeaponShrapnelCount;
            w.ShrapBouncing = tt.WeaponShrapnelBouncing;
            w.Alternate = tt.WeaponMultifire;
            pkt.Weapon = w;
        }

        _game.FakePosition(bot.Player, ref pkt);
    }
}
