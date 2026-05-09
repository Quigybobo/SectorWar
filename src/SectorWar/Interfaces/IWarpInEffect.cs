using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.SectorWar.Interfaces;

/// <summary>
/// Plays a one-shot "warp-in" LVZ animation at a world position. Used by
/// Pylon, StationDeployer, and any other system that needs a "thing
/// materializes" effect.
///
/// The animation is logically: a brief flash/expand at (x, y) lasting
/// durationMs. Implementation toggles a tracked LVZ object on, schedules
/// the off-toggle after durationMs.
/// </summary>
public interface IWarpInEffect : IComponentInterface
{
    /// <summary>
    /// Play the warp-in effect at world (x, y) in pixels for `durationMs`.
    /// `flavor` selects the visual variant — for now just `Default`. Future:
    /// per-structure-type flavors (PylonCyan, OutpostBlue, FortressRed, etc.).
    /// </summary>
    void Play(Arena arena, int pixelX, int pixelY, int durationMs, WarpInFlavor flavor = WarpInFlavor.Default);
}

public enum WarpInFlavor
{
    Default = 0,
    PylonCyan = 1,
    OutpostBlue = 2,
    FortressRed = 3,
    FactoryYellow = 4,
}
