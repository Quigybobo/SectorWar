namespace SS.SectorWar.Persist;

internal static class PersistKeys
{
    public const int Rpg = 200;
    public const int Market = 201;
    public const int Inventory = 202;
    public const int Guild = 203;
    public const int Achievements = 204;
    /// Per-arena: list of pylons (freq, ownerName, x, y, level, deployedAtUtc).
    public const int Pylons = 205;
    /// Per-arena: list of structures (typeKey, freq, ownerName, x, y, level).
    public const int Structures = 206;
}
