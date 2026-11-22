namespace Lilium;

// Applied search state. Text being edited in the UI is deliberately kept separate.
internal sealed record FolderFilterState(int FolderRating = 0, int FileRating = 0,
    string Keyword = "", bool IncludeSubfolders = false)
{
    internal static FolderFilterState Empty { get; } = new();
    internal bool Recursive => IncludeSubfolders && !string.IsNullOrWhiteSpace(Keyword);
}

internal sealed record FolderHistoryEntry(string Folder, FolderFilterState Filters,
    ThumbnailRefreshState? View = null);
