using SS.Core;
using SS.Packets.Game;

namespace SS.SectorWar.Items;

// Configuration for a single attached gun turret.
// Port of D1st0rt's GunTurretInfo struct (gunturret.h, ASSS).
//
// Position is an offset from the anchor's hull, in world units (1 tile = 16 units).
// Rotation is a delta added to the anchor's rotation, in 9°-step units (0..39).
// When the anchor fires, the turret fires its own weapon from the offset position.
public sealed record GunTurretInfo(
    string Name,
    ShipType Ship,
    int OffsetX,
    int OffsetY,
    int RotationOffset,
    WeaponCodes Weapon,
    byte WeaponLevel);
