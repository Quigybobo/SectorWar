using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.SectorWar.Interfaces;

// Phase 1 port of JoWie's damage.h interface (ASSS).
// Original: bitbucket.org/jowie/asss-damage  damage.h
//
// SCOPE OF PHASE 1: bullet-only damage tracking on registered fake players.
// Bombs, prox bombs, thors, EMP, splash, bouncing bullets, tile damage,
// multifire, and shrapnel are DEFERRED to later phases. The interface shape
// matches asss-damage's so future phases can extend without breaking
// callers that only use Phase 1's bullet path.
//
// Architecture (matches asss-damage):
//   - Damage module hooks PlayerPositionPacketCallback to capture bullet
//     fires from real players.
//   - Per-arena weapon list tracks each in-flight bullet's position + velocity.
//   - 10 ms tick advances all bullets, checks tile collision (wall hits) and
//     fake-player collision (point_collision against registered fakes).
//   - On fake-hit: fires the fake's FakeDamageFunc callback. User code (e.g.
//     CompositeHitbox) decides damage routing.
//
// Receiver-authoritative model preserved for REAL players: bullets passing
// through a real player still compute damage on that player's client. The
// damage module here only adds server-authoritative damage on REGISTERED
// FAKES (which have no client to run their own collision).

#region Delegate types (from damage.h)

/// <summary>
/// Called when a tracked fake player is killed. Phase 1 may fire this when
/// energy reaches 0 if manageEnergy=true. Otherwise user code calls
/// IDamage.KillFake explicitly.
/// </summary>
public delegate void DamageKilledFunc(Player player, Player? killer, object? closure);

/// <summary>
/// Called when a tracked fake player is to be respawned. User module sets
/// new x/y coordinates by mutating the position packet.
/// </summary>
public delegate void DamageRespawnFunc(Player player, ref C2S_PositionPacket newPos, object? closure);

/// <summary>
/// Called when a tile in a tracked region takes damage. NOT FIRED IN PHASE 1
/// (tile damage requires AddRegion + RegionData which are deferred).
/// </summary>
public delegate void TileDamageFunc(Arena arena, int x, int y, Player firedBy,
    int damageDealt, WeaponCodes weaponType, int level,
    bool bouncingBomb, int empTime, object? closure);

/// <summary>
/// Called when a registered fake player takes damage. The closure is whatever
/// user code passed to AddFake — typically a state object that routes the
/// damage somewhere (e.g. a shared HP pool for a composite ship).
/// </summary>
/// <param name="fake">The fake player that took the hit.</param>
/// <param name="firedBy">The player who fired the weapon.</param>
/// <param name="dist">Distance from explosion center (always 0 for bullets in Phase 1).</param>
/// <param name="damageDealt">The amount of damage to deal.</param>
/// <param name="weaponType">The weapon type that did the damage.</param>
/// <param name="level">The weapon level (0..3).</param>
/// <param name="bouncing">True for bouncing bullet/bomb. (Phase 1 only fires for non-bouncing bullets.)</param>
/// <param name="empTime">EMP shutdown duration in ticks. (Phase 1 always 0.)</param>
/// <param name="closure">User-supplied state from AddFake.</param>
public delegate void FakeDamageFunc(Player fake, Player firedBy,
    int dist, int damageDealt, WeaponCodes weaponType, int level,
    bool bouncing, int empTime, object? closure);

#endregion

/// <summary>
/// Server-side damage tracking for fake players. Phase 1 = bullet-only.
/// </summary>
public interface IDamage : IComponentInterface
{
    /// <summary>
    /// Register a fake player for server-side damage tracking. Bullets that
    /// hit this fake (via point_collision) will fire the supplied
    /// <paramref name="damageFunc"/> callback.
    /// </summary>
    /// <param name="fake">The fake player to register.</param>
    /// <param name="pos">
    /// Reference to a position packet that the damage module updates with
    /// energy/bounty changes if <paramref name="manageEnergy"/> is true.
    /// User code is expected to keep this packet's x/y/xspeed/yspeed up to
    /// date via game.FakePosition (Phase 1 doesn't update position itself).
    /// </param>
    /// <param name="manageEnergy">
    /// If true, damage module tracks the fake's energy and auto-fires
    /// <paramref name="killFunc"/> when energy hits 0. If false, user code's
    /// <paramref name="damageFunc"/> handles HP routing entirely.
    /// </param>
    /// <param name="killFunc">
    /// Called when the fake dies (energy hits 0 if managed, or KillFake
    /// invoked directly).
    /// </param>
    /// <param name="respawnFunc">
    /// Called when the fake should respawn. Phase 1 does not auto-respawn
    /// (user code can call this manually if desired).
    /// </param>
    /// <param name="damageFunc">
    /// Called on every bullet hit.
    /// THREADING + LIFECYCLE CONTRACT (important):
    ///   - Invoked on the same thread as <see cref="IMainloopTimer"/> ticks
    ///     (i.e. the mainloop thread). Safe to call into other modules.
    ///   - The Damage tick snapshots its bot list before iterating. If a
    ///     single tick has multiple bullets that hit the SAME fake AND the
    ///     first hit triggers death (caller calls RemoveFake / EndFaked),
    ///     the remaining snapshotted bullets WILL still invoke this
    ///     damageFunc on the (now-dead) fake. Callers MUST guard with an
    ///     "already dead" flag in their closure. See StaticTurret.OnBotDamaged
    ///     and WarStationMinions.OnMinionDamaged for reference.
    /// </param>
    /// <param name="closure">User-supplied state passed to all callbacks.</param>
    /// <param name="radiusOverride">
    /// Optional per-fake collision radius (pixels). If null, the arena-wide
    /// per-ship [ShipName] Radius is used. Pass an explicit value when the
    /// arena config sets Radius to a non-collision value (e.g. Hyperspace
    /// uses Radius=255 as a cap, not as a hitbox).
    /// </param>
    /// <remarks>
    /// The <paramref name="pos"/> parameter is captured by value at
    /// registration time — collision uses the live Player.Position, not
    /// the packet. Callers don't need to keep the backing struct alive or
    /// updated; the parameter is effectively informational.
    /// </remarks>
    void AddFake(Player fake, ref C2S_PositionPacket pos, bool manageEnergy,
        DamageKilledFunc? killFunc, DamageRespawnFunc? respawnFunc,
        FakeDamageFunc? damageFunc, object? closure, int? radiusOverride = null);

    /// <summary>Force-kill a registered fake. Fires <see cref="DamageKilledFunc"/>.</summary>
    void KillFake(Player fake, Player killer);

    /// <summary>Unregister a fake. Call before <see cref="IFake.EndFaked"/>.</summary>
    void RemoveFake(Player fake);
}
