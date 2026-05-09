using SS.SectorWar.Items;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

public interface IInventory : IComponentInterface
{
    // Returns the ItemDefinition currently equipped in this ship's slot, or null.
    ItemDefinition? GetEquipped(Player player, ShipType ship, EquipmentSlot slot);

    // Returns the items equipped on a specific ship across the 4 slots.
    IReadOnlyList<ItemDefinition> GetEquippedForShip(Player player, ShipType ship);
}
