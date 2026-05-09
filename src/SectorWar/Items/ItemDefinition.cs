using SS.Packets.Game;

namespace SS.SectorWar.Items;

public sealed record ItemModifier(string Section, string Key, int Addend);

// Optional twin-wing turret grant — when an item with this is equipped, both
// LeftWing and RightWing hardpoints attach turrets matching the equipped ship's class.
public sealed record TurretGrant(WeaponCodes Weapon, byte Level);

public sealed class ItemDefinition
{
    public required int Id { get; init; }
    public required string DisplayName { get; init; }
    public required EquipmentSlot Slot { get; init; }
    public required int Tier { get; init; }
    public required long Cost { get; init; }
    public required IReadOnlyList<ItemModifier> Modifiers { get; init; }
    public TurretGrant? Grant { get; init; }
}
