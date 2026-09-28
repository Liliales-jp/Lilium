namespace Lilium;

internal static class ViewerPagePlanner
{
    internal static int SpreadLength(int count, int index, string mode, Func<int, bool?> isPortrait)
    {
        if (count == 0 || index < 0 || index >= count) return 0;
        if (mode == "one") return 1;
        return index + 1 < count && isPortrait(index) == true && isPortrait(index + 1) == true ? 2 : 1;
    }

    internal static int NextIndex(int count, int index, string mode, Func<int, bool?> isPortrait)
    {
        if (count == 0) return 0;
        index = Math.Clamp(index, 0, count - 1);
        int next = index + SpreadLength(count, index, mode, isPortrait);
        return next < count ? next : index;
    }

    internal static int PreviousIndex(int count, int index, string mode, Func<int, bool?> isPortrait)
    {
        if (count == 0 || index <= 0) return 0;
        index = Math.Clamp(index, 0, count - 1);
        if (mode == "one") return index - 1;
        int twoPageStart = index - 2;
        return twoPageStart >= 0 && SpreadLength(count, twoPageStart, mode, isPortrait) == 2
            ? twoPageStart
            : index - 1;
    }
}
