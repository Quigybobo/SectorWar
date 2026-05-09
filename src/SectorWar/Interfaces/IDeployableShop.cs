using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

/// <summary>
/// One offering in the deployable shop — what kind, what it costs, what
/// to put on the menu line. Used by Inventory's ?menu to render the
/// Deployables sub-dialog without hardcoding prices.
/// </summary>
public sealed record DeployableOffering(string Kind, string DisplayName, long Cost, string Description);

/// <summary>
/// Callable surface for the Phase 3a deployable shop. Lets the existing
/// Inventory ?menu system route "Deployables" sub-menu picks into this
/// module without each menu-callsite duplicating the pylon-power /
/// credit-spend / refund logic.
/// </summary>
public interface IDeployableShop : IComponentInterface
{
    /// <summary>
    /// Try to buy + deploy the named deployable at the player's current
    /// position. Returns true on success. On failure, `reason` carries a
    /// human-readable explanation suitable for chat display.
    /// </summary>
    bool TryBuy(Player player, string kind, out string message);

    /// <summary>
    /// Returns the current shop offerings. UI callsites (Inventory ?menu,
    /// ?deployshop chat command) should query this rather than hardcoding
    /// strings/prices that drift away from the actual TryBuy implementation.
    /// </summary>
    IReadOnlyList<DeployableOffering> GetOfferings();
}
