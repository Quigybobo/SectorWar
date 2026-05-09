using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

// Public surface of the CompositeHitbox module (Phase 2 of modular capital
// ship). ModularShip uses this to drive both visual + hitbox layers from a
// single ?modulebuild command.
//
// See docs/MODULAR_CAPITAL_SHIP.md for the full architecture.

public interface ICompositeHitbox : IComponentInterface
{
    /// <summary>
    /// Spawn invisible turret-fakes around the anchor at the default capital
    /// layout offsets, register each with the Damage module pointing at a
    /// shared HP closure. Bullets from other freqs that hit any turret will
    /// route damage to <paramref name="hp"/>; when HP <= 0, the anchor dies
    /// and all turrets despawn.
    /// </summary>
    /// <param name="anchor">The real player whose ship is the bridge.</param>
    /// <param name="hp">Starting HP for the shared pool.</param>
    void BuildCapital(Player anchor, int hp);

    /// <summary>
    /// Tear down the active capital hitbox for <paramref name="anchor"/>.
    /// </summary>
    /// <param name="anchor">The real player.</param>
    /// <param name="killAnchor">If true, also fakekill the anchor (used when
    /// HP runs out). If false, just despawn the turrets cleanly.</param>
    /// <returns>True if a capital was active and was cleared.</returns>
    bool ClearCapital(Player anchor, bool killAnchor);
}
