using SS.SectorWar.Items;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

public interface IGunTurret : IComponentInterface
{
    bool HasTurret(Player player, GunTurretInfo info);

    // Replace all turrets with a single one.
    bool SetTurret(Player player, GunTurretInfo info);

    // Append a turret to the player's loadout.
    bool AddTurret(Player player, GunTurretInfo info);

    bool RemoveTurret(Player player, GunTurretInfo info);

    bool RemoveAllTurrets(Player player);

    IReadOnlyList<GunTurretInfo> GetTurrets(Player player);
}
