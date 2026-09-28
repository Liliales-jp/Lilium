namespace Lilium;

internal static class ViewerImageOrientation
{
    internal static bool IsPortrait(uint orientedWidth, uint orientedHeight) =>
        orientedHeight >= orientedWidth;
}
