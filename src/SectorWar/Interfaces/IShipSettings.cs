using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

public interface IShipSettings : IComponentInterface
{
    // Recompute floor + equipped-modifier overrides for this player and resend the settings packet.
    // Called by Inventory after equip/unequip, by Rpg after prestige (future), etc.
    void RefreshPlayer(Player player);
}
