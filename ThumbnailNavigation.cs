namespace Lilium;

internal static class ThumbnailNavigation
{
    internal static int Target(int current, int count, int columns, int rows, int key, bool rightToLeft)
    {
        if (count == 0) return -1;
        var horizontal = rightToLeft ? -1 : 1;
        var target = key switch
        {
            37 => current - horizontal,
            39 => current + horizontal,
            38 => current - columns,
            40 => current + columns,
            33 => current - columns * rows,
            34 => current + columns * rows,
            36 => 0,
            35 => count - 1,
            _ => current
        };
        return Math.Clamp(target, 0, count - 1);
    }
}
