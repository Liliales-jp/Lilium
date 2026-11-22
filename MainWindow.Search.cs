namespace Lilium;

public sealed partial class MainWindow
{
    private void RememberCurrentFolderView() =>
        _folderRequests.SaveView(_folderLoad.Previous?.View ?? _navigationRefreshState ?? CaptureThumbnailRefreshState());

    private Task<FolderLoadResult> ApplySearchAsync() => RefreshItemsAsync(
        filters: _folderRequests.RequestedFilters with
        {
            Keyword = KeywordBox.Text,
            IncludeSubfolders = RecursiveSearch.IsChecked
        }, updateSearchInputs: true);

    private void SyncFilterControls(FolderFilterState filters, bool updateSearchInputs)
    {
        bool wasInitializing = _initializing;
        _initializing = true;
        try
        {
            SelectCombo(FolderRatingBox, filters.FolderRating.ToString());
            SelectCombo(FileRatingBox, filters.FileRating.ToString());
            if (updateSearchInputs)
            {
                KeywordBox.Text = filters.Keyword;
                RecursiveSearch.IsChecked = filters.IncludeSubfolders;
            }
        }
        finally { _initializing = wasInitializing; }
    }
}
