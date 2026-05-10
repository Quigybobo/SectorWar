using SS.Packets.Game;

namespace SS.SectorWar.Items;

public sealed record ItemModifier(string Section, string Key, int Addend);

// Optional twin-wing turret grant — when an item with this is equipped, both
// LeftWing and RightWing hardpoints attach turrets matching the equipped ship's class.
//
// AutoFire = false (default) → slave-only. Turrets fire only when the anchor
// (player) fires, mirroring the anchor's weapon class.
// AutoFire = true → turrets ALSO scan for nearest enemy in line of sight and
// fire independently when the anchor isn't firing (anchor-priority window
// suppresses for ~500ms after each anchor shot — see SectorWar.GunTurret.cs).
public sealed record TurretGrant(WeaponCodes Weapon, byte Level, bool AutoFire = false);

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
