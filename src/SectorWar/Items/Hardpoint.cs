using SS.Core;

namespace SS.SectorWar.Items;

public enum Hardpoint
{
    LeftWing,
    RightWing,
}

public static class Hardpoints
{
    // Approximate wing-tip offsets per ship class, in world units (1 unit = 1 pixel).
    // SS ship sprites are 36x36 px; wings extend roughly to Â±13-17 px from sprite center.
    // Y values use a small positive offset to drop turrets slightly behind the nose
    // for ships where the visual wing-line is below the geometric center.
    // Tunable per-ship; current values eyeballed, will refine after in-game testing.
    public static (int X, int Y) Offset(ShipType ship, Hardpoint hp)
    {
        int absX = ship switch
        {
            ShipType.Warbird   => 14,
            ShipType.Javelin   => 15,
            ShipType.Spider    => 13,
            ShipType.Leviathan => 16,
            ShipType.Terrier   => 13,
            ShipType.Weasel    => 12,
            ShipType.Lancaster => 17,
            ShipType.Shark     => 14,
            _                  => 14,
        };

        // +Y is "back" of the ship (toward the tail when facing north).
        // Wing turrets sit ~5 px behind the geometric wing tip so the muzzle line
        // reads as a wing-mounted gun rather than a nose-aligned cannon.
        int y = ship switch
        {
            ShipType.Javelin   => 7,
            ShipType.Leviathan => 7,
            ShipType.Lancaster => 7,
            _                  => 5,
        };

        int x = hp == Hardpoint.LeftWing ? -absX : absX;
        return (x, y);
    }
}
