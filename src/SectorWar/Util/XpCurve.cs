namespace SS.SectorWar.Util;

internal static class XpCurve
{
    public static long XpForLevel(int level, long baseXp)
    {
        if (level < 2) return 0;
        long n = level - 1;
        return baseXp * n * n;
    }
}
