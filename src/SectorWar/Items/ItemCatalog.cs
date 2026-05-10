using SS.Packets.Game;

namespace SS.SectorWar.Items;

// Generated catalog: 4 slots × 100 tiers = 400 items.
// IDs are encoded as (slotPrefix * 1000) + tier:
//   Engine:      1001..1100
//   Shield:      2001..2100
//   WeaponMod:   3001..3100
//   HullPlating: 4001..4100
//
// Costs scale quadratically: cost = 50 * tier^2 (Mk.1 = 50 cr, Mk.50 = 125k, Mk.100 = 500k).
// Modifiers scale linearly with tier — small increments so progression feels granular.
//
// Note: ItemModifier section "__SHIP__" is a placeholder that ShipSettings expands per-ship.
public static class ItemCatalog
{
    public const int MaxTier = 100;

    private const int EngineIdBase = 1000;
    private const int ShieldIdBase = 2000;
    private const int WeaponModIdBase = 3000;
    private const int HullPlatingIdBase = 4000;

    public static readonly ItemDefinition[] All = Generate();

    private static ItemDefinition[] Generate()
    {
        var list = new List<ItemDefinition>(MaxTier * 4 + 8);
        for (int tier = 1; tier <= MaxTier; tier++)
        {
            list.Add(MakeEngine(tier));
            list.Add(MakeShield(tier));
            list.Add(MakeWeaponMod(tier));
            list.Add(MakeHullPlating(tier));
        }

        // Twin-wing turret cannons (occupy WeaponMod slot — competes with Bullets Mk.X buffs).
        // Bullet cannons: 4 tiers (slave-fire — fires only when the anchor fires).
        list.Add(MakeTurret(5001, 1, "Twin Cannon Mk.1",  WeaponCodes.Bullet, 0, 2_000));
        list.Add(MakeTurret(5002, 2, "Twin Cannon Mk.2",  WeaponCodes.Bullet, 1, 8_000));
        list.Add(MakeTurret(5003, 3, "Twin Cannon Mk.3",  WeaponCodes.Bullet, 2, 32_000));
        list.Add(MakeTurret(5004, 4, "Twin Cannon Mk.4",  WeaponCodes.Bullet, 3, 128_000));
        // Bomb pods: 4 tiers (slave-fire).
        list.Add(MakeTurret(5011, 1, "Twin Bomb Pod Mk.1", WeaponCodes.Bomb,  0, 4_000));
        list.Add(MakeTurret(5012, 2, "Twin Bomb Pod Mk.2", WeaponCodes.Bomb,  1, 16_000));
        list.Add(MakeTurret(5013, 3, "Twin Bomb Pod Mk.3", WeaponCodes.Bomb,  2, 64_000));
        list.Add(MakeTurret(5014, 4, "Twin Bomb Pod Mk.4", WeaponCodes.Bomb,  3, 256_000));

        // AUTO-FIRE variants (cost ~2× the slave equivalent, autoFire flag set).
        // Same hardpoints, same ship class, but each turret independently scans
        // for nearest enemy in LOS and fires when the anchor isn't firing.
        // Auto Cannons: 4 tiers
        list.Add(MakeTurret(5021, 1, "Auto Cannon Mk.1",  WeaponCodes.Bullet, 0,   4_000, autoFire: true));
        list.Add(MakeTurret(5022, 2, "Auto Cannon Mk.2",  WeaponCodes.Bullet, 1,  16_000, autoFire: true));
        list.Add(MakeTurret(5023, 3, "Auto Cannon Mk.3",  WeaponCodes.Bullet, 2,  64_000, autoFire: true));
        list.Add(MakeTurret(5024, 4, "Auto Cannon Mk.4",  WeaponCodes.Bullet, 3, 256_000, autoFire: true));
        // Auto Bomb Pods: 4 tiers
        list.Add(MakeTurret(5031, 1, "Auto Bomb Pod Mk.1", WeaponCodes.Bomb,  0,   8_000, autoFire: true));
        list.Add(MakeTurret(5032, 2, "Auto Bomb Pod Mk.2", WeaponCodes.Bomb,  1,  32_000, autoFire: true));
        list.Add(MakeTurret(5033, 3, "Auto Bomb Pod Mk.3", WeaponCodes.Bomb,  2, 128_000, autoFire: true));
        list.Add(MakeTurret(5034, 4, "Auto Bomb Pod Mk.4", WeaponCodes.Bomb,  3, 512_000, autoFire: true));

        return list.ToArray();
    }

    private static ItemDefinition MakeTurret(int id, int tier, string displayName, WeaponCodes weapon, byte level, long cost, bool autoFire = false) =>
        new ItemDefinition
        {
            Id = id,
            DisplayName = displayName,
            Slot = EquipmentSlot.WeaponMod,
            Tier = tier,
            Cost = cost,
            Modifiers = Array.Empty<ItemModifier>(),
            Grant = new TurretGrant(weapon, level, autoFire),
        };

    private static long CostForTier(int tier) => 50L * tier * tier;

    private static ItemDefinition MakeEngine(int tier) => new ItemDefinition
    {
        Id = EngineIdBase + tier,
        DisplayName = $"Engine Mk.{tier}",
        Slot = EquipmentSlot.Engine,
        Tier = tier,
        Cost = CostForTier(tier),
        Modifiers = new[]
        {
            new ItemModifier("__SHIP__", "MaximumThrust", tier),
            new ItemModifier("__SHIP__", "MaximumSpeed", tier * 20),
        },
    };

    private static ItemDefinition MakeShield(int tier) => new ItemDefinition
    {
        Id = ShieldIdBase + tier,
        DisplayName = $"Shield Mk.{tier}",
        Slot = EquipmentSlot.Shield,
        Tier = tier,
        Cost = CostForTier(tier),
        Modifiers = new[]
        {
            new ItemModifier("__SHIP__", "MaximumEnergy", tier * 20),
            new ItemModifier("__SHIP__", "MaximumRecharge", tier * 50),
        },
    };

    private static ItemDefinition MakeWeaponMod(int tier) => new ItemDefinition
    {
        Id = WeaponModIdBase + tier,
        DisplayName = $"Bullets Mk.{tier}",
        Slot = EquipmentSlot.WeaponMod,
        Tier = tier,
        Cost = CostForTier(tier),
        Modifiers = new[]
        {
            new ItemModifier("__SHIP__", "BulletSpeed", tier * 20),
            new ItemModifier("__SHIP__", "BombSpeed", tier * 20),
        },
    };

    private static ItemDefinition MakeHullPlating(int tier) => new ItemDefinition
    {
        Id = HullPlatingIdBase + tier,
        DisplayName = $"Hull Mk.{tier}",
        Slot = EquipmentSlot.HullPlating,
        Tier = tier,
        Cost = CostForTier(tier),
        Modifiers = new[]
        {
            new ItemModifier("__SHIP__", "MaximumRotation", tier * 5),
            new ItemModifier("__SHIP__", "Radius", HullRadiusBonus(tier)),
        },
    };

    // Hull Mk.X radius bonus curve. Linear +10 per decade up to Mk.79, then the
    // top 3 size bands jump dramatically — Mk.80+ enters dreadnought territory,
    // Mk.100 lands at R255 (Continuum's hard 8-bit Radius cap).
    //   Mk.1-9    -> +0    (R = floor 40)
    //   Mk.10-19  -> +10   R50
    //   Mk.20-29  -> +20   R60
    //   Mk.30-39  -> +30   R70
    //   Mk.40-49  -> +40   R80
    //   Mk.50-59  -> +50   R90
    //   Mk.60-69  -> +60   R100
    //   Mk.70-79  -> +70   R110
    //   Mk.80-89  -> +120  R160  <- huge jump (dreadnought tier 1)
    //   Mk.90-99  -> +175  R215  <- bigger jump (dreadnought tier 2)
    //   Mk.100    -> +215  R255  <- max cap (dreadnought tier 3, fully ascended)
    private static int HullRadiusBonus(int tier)
    {
        if (tier >= 100) return 215;
        if (tier >= 90)  return 175;
        if (tier >= 80)  return 120;
        return (tier / 10) * 10;
    }

    public static ItemDefinition? Find(int id)
    {
        foreach (var item in All)
            if (item.Id == id) return item;
        return null;
    }

    public static IEnumerable<ItemDefinition> ForSlot(EquipmentSlot slot)
    {
        foreach (var item in All)
            if (item.Slot == slot) yield return item;
    }
}
