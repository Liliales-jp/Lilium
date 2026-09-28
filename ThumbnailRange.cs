namespace Lilium;

internal readonly record struct ThumbnailRange(int First, int Last, int KeepFirst, int KeepLast)
{
    internal static bool ShouldLimit(int thumbnailCount) => thumbnailCount >= 300;
    internal static ThumbnailRange Create(int count, int first, int last)
    {
        first = Math.Clamp(first, 0, Math.Max(0, count - 1));
        last = Math.Clamp(last, first, Math.Max(first, count - 1));
        var buffer = (last - first + 1) * 2;
        return new(first, last, Math.Max(0, first - buffer), Math.Min(count - 1, last + buffer));
    }
    internal bool Keeps(int index) => index >= KeepFirst && index <= KeepLast;
    internal int Distance(int index) => index < First ? First - index : index > Last ? index - Last : 0;
}
