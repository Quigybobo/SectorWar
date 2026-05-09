using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

// Port of D1st0rt's staticturret.h interface (ASSS).
// Original: bitbucket.org/jowie/asss-staticturret
//
// Server-spawned destroyable turrets on the map. Each turret is a fake player
// with HP, weapons, and AI rotation/firing toward enemies. Per-freq power resource.
// Build sequences (turrets construct over time, not instant). Splash-radius damage
// API for one-shot effects.

public enum AddBotResult
{
    Ok = 0,
    IllegalArena,                // module has not been attached in this arena
    UnknownType,                 // turret-type key not found in [StaticTurret] config
    MaxReachedForBotType,        // per-type quota exhausted
    MaxReachedForArena,          // per-arena quota exhausted
    CanNotBePlacedOnMap,         // would intersect a wall tile
    TooCloseToOtherBot,          // proximity check failed (only if !noLocationCheck)
    BuildingInProgress,          // build sequence enabled and too many bots being built
}

public interface IStaticTurret : IComponentInterface
{
    /// <summary>Begin the turret game in this arena. Allocates resources.</summary>
    void StartGame(Arena arena);

    /// <summary>End the turret game in this arena. Removes all turrets.</summary>
    void StopGame(Arena arena);

    /// <summary>
    /// Spawn a turret at world coords (x, y) on the given freq.
    /// Coords are in pixels — to convert from tile coords use: (tileX &lt;&lt; 4) + 8.
    /// Returns OK on success, or one of the AddBotResult error codes.
    /// </summary>
    AddBotResult AddBot(Arena arena, string typeKey, int x, int y, short freq,
        bool infiniteRespawn, bool noLocationCheck);

    /// <summary>Pause/resume respawning for all turrets on a freq.</summary>
    void FreezeRespawn(Arena arena, short freq, bool freeze);

    /// <summary>Set the power resource for a freq. Must be called at least once for any freq using turrets.</summary>
    void SetPower(Arena arena, short freq, int power);

    /// <summary>
    /// Apply server-authoritative splash damage to all players within radius of (x, y),
    /// excluding members of immuneFreq. The killer is credited if anyone dies.
    /// This is the workaround for receiver-authoritative damage — see SUBSPACE_DAMAGE_MODEL.md.
    /// </summary>
    void DoDamage(Arena arena, Player killer, int x, int y, int damage, int radius, short immuneFreq);

    /// <summary>
    /// Remove a single bot matching the given position + freq (and optionally a
    /// specific turret-type key). Use this to tear down deployable turrets when
    /// their parent registry entry (Pylon / Structure) is despawned — without
    /// it, the underlying static-turret fake keeps firing and appears in the
    /// roster after the registry says it should be gone.
    ///
    /// Returns true if a bot was found and removed.
    /// </summary>
    bool RemoveBotAt(Arena arena, int pixelX, int pixelY, short freq, string? turretKey = null);

    /// <summary>
    /// Nuke every bot in the given arena, regardless of freq or type. Used by
    /// the ?wipearena debug command to fully reset state — including AI
    /// defense turrets that were spawned outside the Pylon/StationDeployer
    /// registries. Returns the count of bots removed.
    /// </summary>
    int RemoveAllBots(Arena arena);

    /// <summary>
    /// Move an existing bot to a new pixel position WITHOUT churning the
    /// underlying fake-player record. Internal PixelX/PixelY are updated and
    /// a fresh position packet is broadcast. Used by Hq's patrolling capital
    /// (and any other "warp this turret" scenario) to keep the fake's F2
    /// identity stable across teleports — RemoveBotAt + AddBot would destroy
    /// and recreate the fake, causing F2 flicker.
    ///
    /// Returns true if a matching bot was found and moved.
    /// </summary>
    bool MoveBot(Arena arena, int oldPixelX, int oldPixelY, short freq,
        string? turretKey, int newPixelX, int newPixelY);

    /// <summary>
    /// Fired on the mainloop thread when a registered turret bot's energy hits
    /// zero from real-player bullet damage (server-side detection via IDamage).
    /// Args: (arena, turretKey, pixelX, pixelY, freq, killer). Subscribers
    /// (e.g. Pylon, StationDeployer) match against their registries by
    /// position + freq to clean up the corresponding deployable.
    /// </summary>
    event Action<Arena, string, int, int, short, Player?>? BotKilled;
}
