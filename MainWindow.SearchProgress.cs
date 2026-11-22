using Microsoft.UI.Xaml;

namespace Lilium;

public sealed partial class MainWindow
{
    private readonly FolderLoadState<LibraryItem> _folderLoad = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _folderSearchTimer;

    private void InitializeFolderSearchProgress()
    {
        _folderSearchTimer = DispatcherQueue.CreateTimer();
        _folderSearchTimer.Interval = TimeSpan.FromMilliseconds(100);
        _folderSearchTimer.Tick += (_, _) => UpdateFolderSearchProgress();
        Closed += (_, _) =>
        {
            _folderSearchTimer.Stop();
            FolderSearchRing.IsActive = false;
            _folderLoad.Clear();
        };
    }

    private FolderSearchProgress? BeginFolderSearch(FolderRequests.Request request, bool showProgress)
    {
        if (!showProgress && _folderLoad.Previous is null) return null;
        var progress = _folderLoad.Begin(request, () => new(_items.ToArray(), _folderRequests.CurrentFilters,
            _navigationRefreshState ?? CaptureThumbnailRefreshState(), FolderNoMatches.Visibility == Visibility.Visible));
        StopThumbnails();
        CancelNavigationFocus();
        PreparePreviewRefresh();
        ThumbnailGrid.ResetNavigationFocus();
        _items.Clear();
        ThumbnailGrid.IsEnabled = false;
        FolderNoMatches.Visibility = Visibility.Collapsed;
        FolderSearchStatus.Visibility = Visibility.Visible;
        FolderSearchRing.IsActive = true;
        CancelFolderSearchButton.IsEnabled = true;
        UpdateFolderSearchProgress();
        _folderSearchTimer!.Start();
        return progress;
    }

    private void UpdateFolderSearchProgress()
    {
        if (_closed || _folderLoad.Request is not { } request || !_folderRequests.IsCurrent(request) ||
            _folderLoad.Progress is not { } progress) return;
        var snapshot = progress.Snapshot();
        bool cancelling = request.CancellationToken.IsCancellationRequested;
        FolderSearchPhaseText.Text = L10n.Get(cancelling ? "Search_Cancelling" : snapshot.Phase switch
        {
            FolderSearchPhase.Descendants => "Search_Descendants",
            FolderSearchPhase.Sorting => "Search_Sorting",
            _ => "Search_Reading"
        });
        FolderSearchCounts.Text = L10n.Format("Search_Counts", snapshot.Folders, snapshot.Items, snapshot.Seconds);
        CancelFolderSearchButton.IsEnabled = !cancelling;
    }

    private void CancelFolderSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_folderLoad.Request is not { } request || !_folderRequests.IsCurrent(request)) return;
        _folderRequests.CancelPending();
        UpdateFolderSearchProgress();
    }

    private FolderLoadState<LibraryItem>.Snapshot? EndFolderSearch(FolderRequests.Request request)
    {
        var previous = _folderLoad.Finish(request);
        if (previous is null) return null;
        _folderSearchTimer!.Stop();
        FolderSearchRing.IsActive = false;
        FolderSearchStatus.Visibility = Visibility.Collapsed;
        ThumbnailGrid.IsEnabled = true;
        return previous;
    }

    private void RestoreFolderSearch(FolderRequests.Request request)
    {
        if (EndFolderSearch(request) is not { } previous) return;
        SyncFilterControls(previous.Filters, updateSearchInputs: true);
        StopThumbnails();
        CancelNavigationFocus();
        PreparePreviewRefresh();
        _items.Clear();
        foreach (var item in previous.Items) _items.Add(item);
        FolderNoMatches.Visibility = previous.NoMatches ? Visibility.Visible : Visibility.Collapsed;
        StartThumbnails();
        var selected = (previous.View?.SelectedPaths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        QueueFolderSelection(request, _items.Where(item => selected.Contains(item.Path)).ToArray(), previous.View);
    }

    private void RestoreCommittedFolderLocation()
    {
        ArchiveLocation.TryParse(_currentFolder, out var archive);
        CurrentFolderText.Text = archive?.DisplayPath ?? _currentFolder ?? "";
        CurrentFolderText.IsEnabled = archive is null;
        if (_currentFolder is not null && archive is null) SyncTree(_currentFolder);
    }
}
