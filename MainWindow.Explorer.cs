using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using System.Text.Json;

namespace Lilium;

public sealed partial class MainWindow
{
    private FileSystemWatcher? _watcher;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchTimer;
    private int _suggestionVersion;
    private CancellationTokenSource? _suggestionCancellation;
    private bool _closed;
    private string? _lastDragTrace;
    [System.Diagnostics.Conditional("DEBUG")]
    private void TraceDrag(string message)
    {
        if (message == _lastDragTrace) return;
        _lastDragTrace = message;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "dragdrop.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1048576) File.WriteAllText(path, "");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n");
        }
        catch { }
    }
    private void InitializeExplorer()
    {
        TraceDrag("InitializeExplorer");
        ThumbnailGrid.SelectionChanged += (_, _) => TraceDrag($"Selection count={ThumbnailGrid.SelectedItems.Count}");
        _watchTimer = DispatcherQueue.CreateTimer();
        _watchTimer.Interval = TimeSpan.FromMilliseconds(600);
        _watchTimer.IsRepeating = false;
        _watchTimer.Tick += async (_, _) =>
        {
            if (_closed) return;
            // Defer automatic updates while a load is pending. A navigation commit
            // replaces the watcher and stops this old folder's queued timer.
            if (_fileOperationBusy || _folderRequests.Pending is not null) { _watchTimer.Start(); return; }
            try { await RefreshItemsAsync(automatic: true, refreshTree: true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            AppServices.Store.QuickAccessChanged -= Store_QuickAccessChanged;
            _folderRequests.Close();
            CancelNavigationFocus();
            CancelAddressSuggestions();
            _watcher?.Dispose();
            _watchTimer.Stop();
            _transferCancellation?.Cancel();
        };
        AppWindow.Closing += (_, args) =>
        {
            if (!_fileOperationBusy) return;
            args.Cancel = true;
            ShowBusyCloseNotice();
        };
    }
    private void RecordNavigation(string path)
    {
        var recent = ReadRecent(out var recentLoaded);
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, path);
        if (recentLoaded) TrySaveSetting("recent_folders", JsonSerializer.Serialize(recent.Take(30)));
    }
    private List<string> ReadRecent() => ReadRecent(out _);
    private List<string> ReadRecent(out bool succeeded)
    {
        try
        {
            var recent = JsonSerializer.Deserialize<List<string>>(AppServices.Store.Get("recent_folders", "[]")) ?? [];
            succeeded = true;
            return recent;
        }
        catch (JsonException ex)
        {
            succeeded = false;
            App.WriteDiagnosticLog("Invalid recent folder setting", ex);
            ShowInvalidSetting("recent_folders");
            return [];
        }
        catch (Exception ex)
        {
            succeeded = false;
            App.WriteDiagnosticLog("Recent folder setting read failed", ex);
            ShowSettingsReadFailure(ex);
            return [];
        }
    }
    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = _folderRequests.CanGoBack;
        ForwardButton.IsEnabled = _folderRequests.CanGoForward;
    }
    private Task NavigateHistoryAsync(int delta) => LoadFolderAsync(_folderRequests.NavigateHistory(delta, (source, destination) =>
        ArchiveLocation.TryParse(source, out var archive) ? archive!.FocusPathWhenReturningTo(destination) : null));
    private async void HistoryBack_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(-1);
    private async void HistoryForward_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(1);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshItemsAsync();
    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var path in ReadRecent())
        {
            var entry = new MenuFlyoutItem { Text = path };
            entry.Click += async (_, _) => await OpenFolderAsync(path);
            menu.Items.Add(entry);
        }
        menu.ShowAt((FrameworkElement)sender);
    }
    private void WatchFolder(string path)
    {
        _watchTimer?.Stop();
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _watcher = ArchiveLocation.IsArchiveFile(path) && !Directory.Exists(path)
                ? new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
                : new FileSystemWatcher(path);
            _watcher.IncludeSubdirectories = false;
            _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
            var watcher = _watcher;
            FileSystemEventHandler changed = (_, _) => QueueRefresh(watcher);
            _watcher.Changed += changed; _watcher.Created += changed; _watcher.Deleted += changed;
            _watcher.Renamed += (_, _) => QueueRefresh(watcher);
            _watcher.Error += (_, _) => QueueRefresh(watcher);
            _watcher.EnableRaisingEvents = true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private void QueueRefresh(FileSystemWatcher watcher) => DispatcherQueue.TryEnqueue(() =>
    { if (!_closed && ReferenceEquals(watcher, _watcher)) { _watchTimer!.Stop(); _watchTimer.Start(); } });

    private async Task RefreshAfterFileOperationAsync()
    {
        if (_closed || _currentFolder is null) return;
        if (ArchiveLocation.IsVirtual(_currentFolder)) { await RefreshItemsAsync(); return; }
        // The explicit scan includes changes made by this operation. Retire its
        // queued watcher callbacks before scanning, including dispatcher callbacks.
        // Re-arm first so genuinely new changes during/after the scan are not lost.
        if (_folderRequests.Pending is null) WatchFolder(_currentFolder);
        await RefreshItemsAsync(refreshTree: true);
    }

    private async void Address_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        CancelAddressSuggestions();
        int version = _suggestionVersion;
        var cancellation = new CancellationTokenSource();
        _suggestionCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        string text = Environment.ExpandEnvironmentVariables(sender.Text.Trim('"'));
        try
        {
            await Task.Delay(150, cancellationToken);
            var suggestions = await Task.Run(() =>
            {
                string? parent = Path.GetDirectoryName(text);
                string prefix = Path.GetFileName(text);
                if (Directory.Exists(text) && text.EndsWith(Path.DirectorySeparatorChar)) { parent = text; prefix = ""; }
                if (parent is null || !Directory.Exists(parent)) return Array.Empty<string>();
                var result = new List<string>();
                foreach (var path in Directory.EnumerateDirectories(parent))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(path) & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) != 0) continue;
                    result.Add(path);
                    if (result.Count == 30) break;
                }
                return result.ToArray();
            }, cancellationToken);
            if (!_closed && version == _suggestionVersion) sender.ItemsSource = suggestions;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { if (!_closed && version == _suggestionVersion) sender.ItemsSource = Array.Empty<string>(); }
        finally
        {
            if (ReferenceEquals(_suggestionCancellation, cancellation)) _suggestionCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelAddressSuggestions()
    {
        ++_suggestionVersion;
        var cancellation = _suggestionCancellation;
        _suggestionCancellation = null;
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }
    private async void Address_Submitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        sender.Text = args.ChosenSuggestion as string ?? args.QueryText;
        try
        {
            string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sender.Text.Trim().Trim('"')));
            await OpenFolderAsync(path);
        }
        catch (Exception ex) { await ShowMessageAsync(L10n.Get("MainWindow_Explorer_001"), ex.Message); }
    }
    private async void SearchSplit_Click(SplitButton sender, SplitButtonClickEventArgs args) => await RefreshItemsAsync();
    private async void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        KeywordBox.Text = "";
        RecursiveSearch.IsChecked = false;
        _initializing = true;
        try { FolderRatingBox.SelectedIndex = 0; FileRatingBox.SelectedIndex = 0; }
        finally { _initializing = false; }
        await RefreshItemsAsync();
    }
    private async Task<bool> ExplorerKeyAsync(KeyRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        bool editing = focused is TextBox || ReferenceEquals(focused, CurrentFolderText);
        var modifiers = InputRouting.Modifiers;
        string? command = (e.Key, modifiers) switch
        {
            (VirtualKey.F5, KeyModifiers.None) => "refresh",
            (VirtualKey.N, KeyModifiers.Control | KeyModifiers.Shift) when !editing => "new",
            (VirtualKey.Z, KeyModifiers.Control) when !editing => "undo",
            (VirtualKey.A, KeyModifiers.Control) when !editing => "selectall",
            (VirtualKey.V, KeyModifiers.Control) when !editing => "paste",
            _ => null
        };
        if (command is null) return false;
        e.Handled = true;
        // Keyboard paste always uses the right-hand list, even when the tree has focus.
        // Text inputs retain their normal text-paste behavior.
        var destination = command == "paste" && ThumbnailGrid.SelectedItems.Count == 1 &&
            ThumbnailGrid.SelectedItems[0] is LibraryItem { IsFolder: true } folder
                ? folder.Path : _currentFolder;
        await FileCommandAsync(command, command == "paste" ? destination : null);
        return true;
    }
    private async void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryItem item } element) return;
        try
        {
            ArchiveLocation.TryParse(item.Path, out var archiveItem);
            string info = (archiveItem?.DisplayPath ?? item.Path) + L10n.Get("MainWindow_Explorer_002") + item.Modified.ToString("yyyy/MM/dd HH:mm");
            if (!item.IsFolder && !item.IsVirtual)
            {
                info += L10n.Get("MainWindow_Explorer_003") + new FileInfo(item.Path).Length.ToString("N0") + " bytes";
                if (item.IsImage)
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.Path);
                    var properties = await file.Properties.GetImagePropertiesAsync();
                    info += L10n.Format("MainWindow_Explorer_004", properties.Width, properties.Height);
                }
            }
            if (ReferenceEquals(element.DataContext, item)) ToolTipService.SetToolTip(element, info);
        }
        catch { }
    }
    private void ThumbnailContainer_Changing(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        // Keep pointer/selection ownership on GridViewItem. A draggable child
        // consumes clicks before the container's Extended selection logic.
        var container = e.ItemContainer;
        container.DragStarting -= Files_DragStarting;
        container.DropCompleted -= Files_DropCompleted;
        if (e.InRecycleQueue) return;
        container.DragStarting += Files_DragStarting;
        container.DropCompleted += Files_DropCompleted;
        if (ReferenceEquals(e.Item, _pendingNavigationFocus)) ApplyNavigationFocus();
    }

    private async void Files_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        var origin = (sender as ContentControl)?.Content as LibraryItem ?? (sender as FrameworkElement)?.DataContext as LibraryItem;
        if (_fileOperationBusy || origin is null || origin.IsVirtual) { e.Cancel = true; return; }
        var selection = ThumbnailGrid.SelectedItems.Contains(origin)
            ? ThumbnailGrid.SelectedItems.OfType<LibraryItem>().ToArray() : new[] { origin };
        if (selection.Any(item => item.IsVirtual)) { e.Cancel = true; return; }
        if (!ThumbnailGrid.SelectedItems.Contains(origin))
        {
            ThumbnailGrid.SelectedItems.Clear();
            ThumbnailGrid.SelectedItems.Add(origin);
        }
        TraceDrag($"Starting count={selection.Length}");
        // Advertise both effects before yielding: the native drag operation can
        // snapshot its allowed effects as soon as this event handler returns.
        e.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        var deferral = e.GetDeferral();
        try
        {
            var items = new List<IStorageItem>();
            foreach (var item in selection)
                items.Add(item.IsFolder ? await StorageFolder.GetFolderFromPathAsync(item.Path) : await StorageFile.GetFileFromPathAsync(item.Path));
            e.Data.SetStorageItems(items);
            e.Data.SetData("Lilium.FilePaths", JsonSerializer.Serialize(selection.Select(item => item.Path)));
            TraceDrag($"Starting ready allowed={e.AllowedOperations} requested={e.Data.RequestedOperation}");
        }
        catch (Exception ex) { e.Cancel = true; TraceDrag("Starting failed: " + ex); }
        finally { deferral.Complete(); }
    }
    private void Files_DropCompleted(UIElement sender, DropCompletedEventArgs e) => TraceDrag($"Completed result={e.DropResult}");
    private void Files_DragOver(object sender, DragEventArgs e)
    {
        var destination = FolderAt(sender) ?? DropDestination(e);
        e.AcceptedOperation = !_fileOperationBusy && destination is not null && HasDragFiles(e)
            ? DragOperation(e) & e.AllowedOperations : DataPackageOperation.None;
        TraceDrag($"Over source={e.OriginalSource?.GetType().Name} destination={destination} modifiers={e.Modifiers} allowed={e.AllowedOperations} requested={e.DataView.RequestedOperation} accepted={e.AcceptedOperation} busy={_fileOperationBusy}");
        if (e.AcceptedOperation != DataPackageOperation.None)
        {
            e.DragUIOverride.Caption = L10n.Format("MainWindow_Explorer_007", Path.GetFileName(destination!.TrimEnd('\\')), (e.AcceptedOperation == DataPackageOperation.Copy ? L10n.Get("MainWindow_Explorer_005") : L10n.Get("MainWindow_Explorer_006")));
            e.DragUIOverride.IsCaptionVisible = true;
        }
        e.Handled = true;
    }
    private static bool HasDragFiles(DragEventArgs e) => e.DataView.Contains("Lilium.FilePaths") || e.DataView.Contains(StandardDataFormats.StorageItems);

    private string? DropDestination(DragEventArgs e)
    {
        // OriginalSource can be the GridView's drag overlay, not the card below the pointer.
        var hits = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(e.GetPosition(RootGrid), RootGrid).ToArray();
        foreach (var hit in hits)
        {
            var folder = FolderAt(hit);
            if (folder is not null) return folder;
        }
        return hits.Contains(ThumbnailGrid) && !ArchiveLocation.IsVirtual(_currentFolder) ? _currentFolder : null;
    }

    private static async Task<IReadOnlyList<IStorageItem>> ReadDragFilesAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains("Lilium.FilePaths")) return await e.DataView.GetStorageItemsAsync();
        var json = await e.DataView.GetDataAsync("Lilium.FilePaths") as string;
        var paths = JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? [];
        var items = new List<IStorageItem>();
        foreach (var path in paths)
            items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
        return items;
    }
    private static DataPackageOperation DragOperation(DragEventArgs e) =>
        (e.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control) != 0
            ? DataPackageOperation.Copy : DataPackageOperation.Move;

    private string? FolderAt(object source)
    {
        for (var element = source as DependencyObject; element is not null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            string? path = element switch
            {
                FrameworkElement { DataContext: LibraryItem { IsFolder: true, IsVirtual: false } item } => item.Path,
                FrameworkElement { DataContext: TreeViewNode { Content: FolderNode node } } => node.Path,
                FrameworkElement { DataContext: FolderNode node } => node.Path,
                TreeViewItem container when FolderTree.NodeFromContainer(container)?.Content is FolderNode node => node.Path,
                _ => null
            };
            if (!string.IsNullOrEmpty(path)) return path;
        }
        return null;
    }

    private void Tree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var destination = FolderAt(e.OriginalSource);
        if (destination is null) return;
        var menu = new MenuFlyout();
        var info = new DirectoryInfo(destination);
        var target = new LibraryItem { Path = destination, DisplayName = info.Name, IsFolder = true, Modified = info.LastWriteTime, Rating = 0, ThumbnailSourcePath = "", CardWidth = 0, CardHeight = 0 };
        void Add(string text, string command)
        {
            var entry = new MenuFlyoutItem { Text = text, IsEnabled = !_fileOperationBusy };
            entry.Click += async (_, _) => await FileCommandAsync(command, destination, command == "paste", [target]);
            menu.Items.Add(entry);
        }
        Add(L10n.Get("MainWindow_Explorer_008"), "copy"); Add(L10n.Get("MainWindow_Explorer_009"), "cut"); Add(L10n.Get("MainWindow_Explorer_010"), "paste");
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(L10n.Get("MainWindow_Explorer_011"), "window"); Add(L10n.Get("MainWindow_Explorer_012"), "explorer"); Add(L10n.Get("MainWindow_Explorer_013"), "cache");
        menu.Items.Add(new MenuFlyoutSeparator());
        var quick = new MenuFlyoutItem { Text = L10n.Get("MainWindow_Explorer_014") };
        quick.Click += (_, _) => TryAddQuickAccess(destination);
        menu.Items.Add(quick);
        menu.ShowAt(FolderTree, e.GetPosition(FolderTree));
        e.Handled = true;
    }
    private async void Files_Drop(object sender, DragEventArgs e)
    {
        var destination = FolderAt(sender) ?? DropDestination(e);
        TraceDrag($"Drop destination={destination} operation={DragOperation(e)} busy={_fileOperationBusy}");
        if (_fileOperationBusy || destination is null || !HasDragFiles(e)) return;
        var operation = DragOperation(e) & e.AllowedOperations;
        if (operation == DataPackageOperation.None) return;
        e.AcceptedOperation = operation;
        e.Handled = true;
        var deferral = e.GetDeferral();
        bool dropReleased = false;
        try
        {
            _fileOperationBusy = true;
            var sources = await ReadDragFilesAsync(e);
            // Snapshot the payload and end the native drag before showing modal UI.
            // We perform the entire transfer ourselves: the drag source must not delete anything.
            e.AcceptedOperation = DataPackageOperation.None;
            deferral.Complete();
            dropReleased = true;
            var resumed = new TaskCompletionSource();
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => resumed.SetResult()))
                throw new OperationCanceledException(L10n.Get("MainWindow_Explorer_015"));
            await resumed.Task;
            ActivateDialogOwner();
            TraceDrag($"Transfer count={sources.Count} destination={destination}");
            var completed = await TransferAsync(sources, destination, operation == DataPackageOperation.Move);
            TraceDrag($"Transfer completed={completed}");
        }
        catch (Exception ex)
        {
            if (!dropReleased)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                deferral.Complete();
                dropReleased = true;
            }
            if (!_closed) await ShowMessageAsync(L10n.Get("MainWindow_Explorer_016"), ex.Message);
        }
        finally
        {
            if (!dropReleased) deferral.Complete();
            try { await RefreshAfterFileOperationAsync(); }
            catch (Exception ex) { await ShowMessageAsync(L10n.Get("MainWindow_Explorer_017"), ex.Message); }
            finally { _fileOperationBusy = false; }
            
        }
    }
}
