namespace Lilium;

internal sealed record ThumbnailRefreshState(string Folder, string[] SelectedPaths, string? FocusPath,
    bool RestoreFocus, double HorizontalOffset, double VerticalOffset)
{
    internal ThumbnailRefreshState Renamed(string source, string target, bool isFolder)
    {
        string Map(string path)
        {
            if (string.Equals(path, source, StringComparison.OrdinalIgnoreCase)) return target;
            var prefix = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return isFolder && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(target, path[prefix.Length..]) : path;
        }
        return this with
        {
            SelectedPaths = SelectedPaths.Select(Map).ToArray(),
            FocusPath = FocusPath is null ? null : Map(FocusPath)
        };
    }
}
