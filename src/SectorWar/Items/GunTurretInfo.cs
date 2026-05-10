using SS.Core;
using SS.Packets.Game;

namespace SS.SectorWar.Items;

// Configuration for a single attached gun turret.
// Port of D1st0rt's GunTurretInfo struct (gunturret.h, ASSS).
//
// Position is an offset from the anchor's hull, in world units (1 tile = 16 units).
// Rotation is a delta added to the anchor's rotation, in 9°-step units (0..39).
// When the anchor fires, the turret fires its own weapon from the offset position.
//
// AutoFire (default false) — set true to enable independent target acquisition.
// When the anchor isn't firing (anchor-priority window has elapsed), the
// turret scans for nearest enemy in LOS and fires at it. See
// SectorWar.GunTurret.cs's auto-fire tick for full behavior. Default false
// preserves the slave-only behavior all existing callers expect.
public sealed record GunTurretInfo(
    string Name,
    ShipType Ship,
    int OffsetX,
    int OffsetY,
    int RotationOffset,
    WeaponCodes Weapon,
    byte WeaponLevel,
    bool AutoFire = false);
