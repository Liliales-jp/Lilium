using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using Windows.System;

namespace Lilium;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<QuickAccessItem> _quickAccess = [];
    private readonly ObservableCollection<LibraryItem> _items = [];
    private readonly FolderRequests _folderRequests = new();
    private string? _currentFolder => _folderRequests.CurrentFolder;
    private bool _initializing = true;
    private bool _syncingTree;
    private bool _rootLoaded;

    private readonly string? _startFolder;
    public MainWindow(string? startFolder = null)
    {
        _startFolder = startFolder;
        InitializeComponent();
        RootGrid.Language = L10n.Language;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Lilium.ico"));
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        WindowTitleBarTheme.Attach(this, RootGrid);
        InitializeWindowPlacement();
        QuickAccessList.ItemsSource = _quickAccess;
        ThumbnailGrid.ItemsSource = _items;
        ThumbnailGrid.LayoutUpdated += (_, _) => ApplyNavigationFocus();
        ThumbnailGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ThumbnailBlank_PointerPressed), true);
        RootGrid.PreviewKeyDown += GlobalKeyDown;
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(GlobalPointerPressed), true);
        InitializeExplorer();
        InitializeThumbnails();
        InitializePreview();
        AppServices.Store.QuickAccessChanged += Store_QuickAccessChanged;
        ((App)Application.Current).RegisterLibraryWindow(this);
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_rootLoaded || _closed) return;
        _rootLoaded = true;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string startFolder;
        bool saveInitialFolder;
        _initializing = true;
        try
        {
            _savedBinding = ReadChoiceSetting("binding", "right", "right", "left");
            _savedThumbnailSize = ReadChoiceSetting("thumbnail_size", "M", "S", "M", "L");
            _savedFolderSort = ReadChoiceSetting("sort", "name", "name", "modified");
            _savedFileSort = ReadChoiceSetting("file_sort", "name", "name", "modified");
            RestorePreviewSettings();
            SelectCombo(BindingBox, _savedBinding);
            SelectCombo(ThumbnailSizeBox, _savedThumbnailSize);
            SelectCombo(SortBox, _savedFolderSort);
            SelectCombo(FileSortBox, _savedFileSort);
            RefreshQuickAccess();
            BuildDriveTree();
            if (_startFolder is not null)
            {
                startFolder = _startFolder;
                saveInitialFolder = true;
            }
            else startFolder = ReadSetting("last_folder", desktop, out saveInitialFolder);
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Main window initialization failed", ex);
            startFolder = desktop;
            saveInitialFolder = false;
            await ShowMessageAsync(L10n.Get("MainWindow_xaml_001"), L10n.Get("MainWindow_xaml_002") + ex.Message);
        }
        finally { _initializing = false; }

        try
        {
            var result = await OpenFolderAsync(startFolder, persistFolder: saveInitialFolder);
            if (_folderRequests.IsCurrent(result.Request) && result.Failure is not null &&
                !string.Equals(startFolder, desktop, StringComparison.OrdinalIgnoreCase))
                await OpenFolderAsync(desktop, persistFolder: false);
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Initial folder open failed", ex);
            if (!_closed) await ShowMessageAsync(L10n.Get("MainWindow_xaml_003"), ex.Message);
        }
        if (!_closed) RevealInitialFolderInTree();
        if (!_closed) await CheckOperationNoticesAsync();
        if (!_closed && AppServices.Inputs.TakeLoadNotice())
            await ShowMessageAsync(L10n.Get("SettingsWindow_006"), L10n.Get("Keys_LoadError"));
    }

    private void BuildDriveTree()
    {
        var wasSyncing = _syncingTree;
        _syncingTree = true;
        try
        {
            var pc = FolderTree.RootNodes.FirstOrDefault();
            if (pc is null)
            {
                pc = new TreeViewNode { Content = new FolderNode("PC", string.Empty, false), IsExpanded = true };
                FolderTree.RootNodes.Add(pc);
            }
            // Reuse nodes instead of clearing RootNodes: their expansion state
            // (including branches unrelated to the current folder) stays intact.
            RefreshTreeChildren(pc, DriveInfo.GetDrives().Where(d => d.IsReady)
                .Select(d => new FolderNode(d.Name, d.RootDirectory.FullName, true)).ToArray());
        }
        finally { _syncingTree = wasSyncing; }
    }

    private void RefreshTreeChildren(TreeViewNode parent, IEnumerable<FolderNode> folders)
    {
        var existing = parent.Children.Where(n => n.Content is FolderNode { IsPlaceholder: false })
            .ToDictionary(n => ((FolderNode)n.Content).Path, StringComparer.OrdinalIgnoreCase);
        var desired = folders.Select(folder => existing.TryGetValue(folder.Path, out var node)
            ? node : FileCatalog.CreateFolderNode(folder.Path, folder.Name)).ToArray();
        var retained = desired.ToHashSet();
        for (int i = parent.Children.Count - 1; i >= 0; i--)
            if (!retained.Contains(parent.Children[i])) parent.Children.RemoveAt(i);
        for (int i = 0; i < desired.Length; i++)
        {
            var node = desired[i];
            if (i >= parent.Children.Count || !ReferenceEquals(parent.Children[i], node))
            {
                parent.Children.Remove(node);
                parent.Children.Insert(i, node);
            }
            // Refresh only branches already loaded. Never crawl unopened folders.
            bool unloaded = node.Children.Count == 1 && node.Children[0].Content is FolderNode { IsPlaceholder: true };
            if (!unloaded && node.Content is FolderNode data)
            {
                var result = FileCatalog.ReadDirectories(data.Path);
                if (result.IsSuccess)
                    RefreshTreeChildren(node, result.Entries.Select(entry => new FolderNode(entry.Name, entry.Path, true)));
                else ShowFolderReadFailure(data.Path, result.Status, result.Error, tree: true);
            }
        }
    }

    private void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is FolderNode node) TryExpandTreeNode(args.Node, node);
    }

    private async void FolderTree_SelectedItemChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (!_syncingTree && args.AddedItems.FirstOrDefault() is TreeViewNode { Content: FolderNode { CanExpand: true } node })
            await OpenFolderAsync(node.Path);
    }

    private async void QuickAccessList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not QuickAccessItem item) return;
        var result = await OpenFolderAsync(item.Path);
        if (!_folderRequests.IsCurrent(result.Request) || result.Failure != FolderReadStatus.NotFound) return;
        var dialog = new ContentDialog
        {
            Title = L10n.Get("MainWindow_xaml_004"),
            Content = L10n.Format("MainWindow_xaml_005", item.Path),
            PrimaryButtonText = L10n.Get("MainWindow_xaml_006"), CloseButtonText = L10n.Get("MainWindow_xaml_007"),
            DefaultButton = ContentDialogButton.Close, XamlRoot = RootGrid.XamlRoot
        };
        if (await ShowOwnedDialog(dialog) == ContentDialogResult.Primary)
            TryRemoveQuickAccess(item.Path);
    }

    private sealed record FolderLoadResult(FolderRequests.Request? Request, FolderReadStatus? Failure = null);

    private Task<FolderLoadResult> OpenFolderAsync(string path, string? focusPath = null, bool persistFolder = true)
    {
        path = Path.GetFullPath(path);
        return ArchiveLocation.IsArchiveFile(path) && File.Exists(path)
            ? OpenArchiveFolderAsync(ArchiveLocation.Folder(path, ""))
            : LoadFolderAsync(_folderRequests.Navigate(path, focusPath, persistFolder));
    }

    private Task<FolderLoadResult> OpenArchiveFolderAsync(string location) =>
        LoadFolderAsync(_folderRequests.Navigate(location, persistFolder: false));

    private Task<FolderLoadResult> RefreshItemsAsync(bool automatic = false, bool refreshTree = false,
        ThumbnailRefreshState? restore = null) =>
        LoadFolderAsync(_folderRequests.Refresh(automatic), refreshTree, restore);

    private async Task<FolderLoadResult> LoadFolderAsync(FolderRequests.Request? request, bool refreshTree = false,
        ThumbnailRefreshState? restore = null)
    {
        if (request is null) return new FolderLoadResult(null);
        try
        {
        CancelNavigationFocus();
        CancelAddressSuggestions();
        UpdateHistoryButtons();
        var folder = request.Folder;
        var cancellationToken = request.CancellationToken;
        // Capture all controls before the first await so a request is internally consistent.
        var size = SelectedTag(ThumbnailSizeBox, "M");
        var keyword = KeywordBox.Text;
        var recursive = RecursiveSearch.IsChecked && !string.IsNullOrWhiteSpace(keyword);
        var folderRating = int.Parse(SelectedTag(FolderRatingBox, "0"));
        var fileRating = int.Parse(SelectedTag(FileRatingBox, "0"));
        var folderSort = SelectedTag(SortBox, "name");
        var fileSort = SelectedTag(FileSortBox, "name");

        async Task<FolderLoadResult> FailedAsync(FolderReadStatus status, Exception? error, string? failedPath = null)
        {
            if (!_folderRequests.TryFail(request)) return new FolderLoadResult(request);
            UpdateHistoryButtons();
            ArchiveLocation.TryParse(_currentFolder, out var previousArchive);
            CurrentFolderText.Text = previousArchive?.DisplayPath ?? _currentFolder ?? "";
            CurrentFolderText.IsEnabled = previousArchive is null;
            if (_currentFolder is not null && previousArchive is null) SyncTree(_currentFolder);
            ArchiveLocation.TryParse(failedPath ?? folder, out var failedArchive);
            if (error is ArchivePasswordException)
            {
                App.WriteDiagnosticLog("Password-protected archive open failed", error);
                if (!_closed)
                {
                    try { await ShowMessageAsync(L10n.Get("Archive_OpenError"), L10n.Get("Archive_Password")); }
                    catch (Exception ex) { App.WriteDiagnosticLog("Archive password dialog failed", ex); }
                }
            }
            else ShowFolderReadFailure(failedArchive?.DisplayPath ?? failedPath ?? folder, status, error);
            return new FolderLoadResult(request, status);
        }

        List<LibraryItem> items;
        List<LibraryItem> visible;
        try
        {
            ArchiveLocation.TryParse(folder, out var archive);
            var read = archive is null
                ? await FileCatalog.ReadFolderAsync(folder, size, cancellationToken)
                : await ArchiveCatalog.ReadFolderAsync(archive, size, recursive, cancellationToken);
            if (!_folderRequests.IsCurrent(request)) return new FolderLoadResult(request);
            if (!read.IsSuccess) return await FailedAsync(read.Status, read.Error);
            items = read.Items;
            if (recursive && archive is null)
            {
                var descendants = await FileCatalog.ReadDescendantFoldersAsync(folder, size, cancellationToken);
                if (!_folderRequests.IsCurrent(request)) return new FolderLoadResult(request);
                if (!descendants.IsSuccess)
                    return await FailedAsync(FolderReadStatus.Partial, descendants.Error, descendants.FailedPath);
                items.AddRange(descendants.Items);
            }
            visible = await Task.Run(() => FileCatalog.FilterAndSort(items,
                archive is null ? folderRating : 0, archive is null ? fileRating : 0,
                keyword, folderSort, fileSort, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new FolderLoadResult(request);
        }
        catch (Exception ex)
        {
            return await FailedAsync(FolderReadStatus.Failed, ex);
        }

        if (!_folderRequests.IsCurrent(request)) return new FolderLoadResult(request);
        // No await between this guard and the entire UI/navigation commit.
        // Superseded requests cannot roll back history, resync the tree or show errors.
        if (!_folderRequests.TryCommit(request)) return new FolderLoadResult(request);
        if (request.Navigation) restore = null;
        else if (restore is null || !string.Equals(restore.Folder, folder, StringComparison.OrdinalIgnoreCase))
            restore = CaptureThumbnailRefreshState();
        var selectedPaths = (restore?.SelectedPaths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        StopThumbnails();
        CancelNavigationFocus();
        PreparePreviewRefresh();
        ArchiveLocation.TryParse(folder, out var currentArchive);
        CurrentFolderText.Text = currentArchive?.DisplayPath ?? folder;
        CurrentFolderText.IsEnabled = currentArchive is null;
        ThumbnailGrid.ResetNavigationFocus();
        _items.Clear(); foreach (var item in visible) _items.Add(item);
        if (request.Navigation)
        {
            if (currentArchive is null) RecordNavigation(folder);
            WatchFolder(currentArchive?.ArchivePath ?? folder);
            if (request.PersistFolder && currentArchive is null) TrySaveSetting("last_folder", folder);
            if (currentArchive is null) SyncTree(folder);
        }
        UpdateHistoryButtons();
        StartThumbnails();
        if (refreshTree) BuildDriveTree();
        var target = visible.FirstOrDefault(i => string.Equals(i.Path, request.FocusPath, StringComparison.OrdinalIgnoreCase)) ?? visible.FirstOrDefault();
        QueueFolderSelection(request, request.Navigation
            ? target is null ? [] : [target]
            : visible.Where(i => selectedPaths.Contains(i.Path)).ToArray(), restore);
        return new FolderLoadResult(request);
        }
        finally { request.Dispose(); }
    }

    private async void Filters_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (ReferenceEquals(sender, FolderRatingBox) || ReferenceEquals(sender, FileRatingBox))
        {
            await RefreshItemsAsync();
            return;
        }
        if (ReferenceEquals(sender, SortBox))
        {
            var folderSort = SelectedTag(SortBox, "name");
            if (!TrySaveSetting("sort", folderSort))
            {
                _initializing = true;
                try { SelectCombo(SortBox, _savedFolderSort); }
                finally { _initializing = false; }
                return;
            }
            _savedFolderSort = folderSort;
        }
        else if (ReferenceEquals(sender, FileSortBox))
        {
            var fileSort = SelectedTag(FileSortBox, "name");
            if (!TrySaveSetting("file_sort", fileSort))
            {
                _initializing = true;
                try { SelectCombo(FileSortBox, _savedFileSort); }
                finally { _initializing = false; }
                return;
            }
            _savedFileSort = fileSort;
        }
        else return;
        await RefreshItemsAsync();
    }

    private async void KeywordBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) await RefreshItemsAsync(); }
    private async void ThumbnailSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var size = SelectedTag(ThumbnailSizeBox, "M");
        if (!TrySaveSetting("thumbnail_size", size))
        {
            _initializing = true;
            try { SelectCombo(ThumbnailSizeBox, _savedThumbnailSize); }
            finally { _initializing = false; }
            return;
        }
        _savedThumbnailSize = size;
        await RefreshItemsAsync();
    }
    private void BindingBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var binding = SelectedTag(BindingBox, "right");
        if (!TrySaveSetting("binding", binding))
        {
            _initializing = true;
            try { SelectCombo(BindingBox, _savedBinding); }
            finally { _initializing = false; }
            return;
        }
        _savedBinding = binding;
    }

    private void Thumbnail_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryItem item)
        {
            e.Handled = true;
            var request = _folderRequests.Latest;
            // Finish the pointer gesture before activating another window.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                async () => { if (_folderRequests.IsCurrent(request)) await ActivateItemAsync(item); });
        }
    }

    private Task ActivateItemAsync(LibraryItem item)
    {
        if (item.IsPdf)
        {
            var reader = new ViewerWindow(item.Path, SelectedTag(BindingBox, _savedBinding), this);
            reader.ShowForeground();
            return Task.CompletedTask;
        }
        if (item.IsArchive) return OpenArchiveFolderAsync(ArchiveLocation.Folder(item.Path, ""));
        if (item.IsVirtual && item.IsFolder) return OpenArchiveFolderAsync(item.Path);
        if (item.IsFolder) return OpenFolderAsync(item.Path);
        if (item.IsVirtual && !item.IsImage)
            return ShowMessageAsync(L10n.Get("MainWindow_xaml_008"), L10n.Get("Archive_UnsupportedFile"));
        if (!item.IsImage) return OpenAssociatedAsync(item.Path);
        var images = _items.Where(i => i.IsImage).ToList();
        var viewer = new ViewerWindow(images, images.FindIndex(i => i.Path == item.Path),
            SelectedTag(BindingBox, _savedBinding), this);
        viewer.ShowForeground();
        return Task.CompletedTask;
    }

    private async Task OpenAssociatedAsync(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { await ShowMessageAsync(L10n.Get("MainWindow_xaml_008"), ex.Message); }
    }

    private async Task ChangeRatingAsync(LibraryItem item, int rating)
    {
        if (item.IsVirtual) return;
        if (_fileOperationBusy) return;
        _fileOperationBusy = true;
        try
        {
            var restore = CaptureThumbnailRefreshState(restoreGridFocus: true);
            var target = FileCatalog.RatingTarget(item.Path, item.IsFolder, rating);
            if (!string.Equals(target, item.Path, StringComparison.OrdinalIgnoreCase))
            {
                if (await RunRequestsAsync([new() { Kind = "Move", Source = item.Path, Target = target }]))
                    restore = restore?.Renamed(item.Path, target, item.IsFolder);
            }
            await RefreshAfterFileOperationAsync(restore);
        }
        catch (Exception ex) { await ShowMessageAsync(L10n.Get("MainWindow_xaml_009"), ex.Message); }
        finally { _fileOperationBusy = false;  }
    }

    private void Thumbnail_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryItem item && !ThumbnailGrid.SelectedItems.Contains(item))
        { ThumbnailGrid.SelectedItems.Clear(); ThumbnailGrid.SelectedItems.Add(item); }
    }
    private void RefreshQuickAccess()
    {
        try
        {
            var items = AppServices.Store.GetQuickAccess().Select(path => new QuickAccessItem(path)).ToArray();
            _quickAccess.Clear();
            foreach (var item in items) _quickAccess.Add(item);
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Quick access read failed", ex);
            ShowSettingsReadFailure(ex);
        }
    }
    private void Store_QuickAccessChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        if (DispatcherQueue.HasThreadAccess) RefreshQuickAccess();
        else DispatcherQueue.TryEnqueue(RefreshQuickAccess);
    }
    private Task GoUpAsync()
    {
        var folder = _folderRequests.Pending?.Folder ?? _currentFolder;
        if (ArchiveLocation.TryParse(folder, out var archive))
            return archive!.EntryName.Length == 0
                ? OpenFolderAsync(archive.Parent, archive.ArchivePath)
                : OpenArchiveFolderAsync(archive.Parent);
        return folder is null ? Task.CompletedTask : OpenFolderAsync(Directory.GetParent(folder)?.FullName ?? folder, folder);
    }

    private async void GlobalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        CancelNavigationFocus();
        if (e.Handled || _fileOperationBusy || _dialogDepth > 0 || _closed) return;
        if (await ExplorerKeyAsync(e)) return;
        var focus = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        if (InputRouting.IsControl(focus)) return;
        var gesture = InputRouting.Key(e);
        bool inPreview = IsPreviewElement(focus);
        if (ReferenceEquals(focus, PreviewSplitter)) return; // Arrow keys resize the divider.
        bool inGrid = false;
        for (var node = focus as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (node == ThumbnailGrid) { inGrid = true; break; }
        bool inReadingArea = inGrid || inPreview;
        if (inPreview && gesture.Key is >= 33 and <= 40 &&
            (gesture.Modifiers & ~(KeyModifiers.Control | KeyModifiers.Shift)) == 0)
        {
            ThumbnailGrid.ForwardNavigationKey(e);
            e.Handled = true;
            return;
        }
        if (inReadingArea)
        {
            string? fixedCommand = (gesture.Key, gesture.Modifiers) switch
            {
                (67, KeyModifiers.Control) => "copy", (88, KeyModifiers.Control) => "cut",
                (113, KeyModifiers.None) => "rename", (46, KeyModifiers.None) => "delete", _ => null
            };
            if (fixedCommand is not null) { e.Handled = true; await FileCommandAsync(fixedCommand); return; }
        }
        var action = AppServices.Inputs.Match(gesture, BindingRules.Library);
        if (action is not null && (action is "Back" or "Forward" or "Up" || inReadingArea))
        {
            e.Handled = true;
            await RunLibraryInputAsync(action);
            return;
        }
        if (inReadingArea && _previewOpen) await _previewReader.HandleKeyAsync(e);
    }

    private async Task RunLibraryInputAsync(string action)
    {
        try
        {
            if (action == "Back") { await NavigateHistoryAsync(-1); return; }
            if (action == "Forward") { await NavigateHistoryAsync(1); return; }
            if (action == "Up") { await GoUpAsync(); return; }
            if (ThumbnailGrid.SelectedItems.Count != 1 || ThumbnailGrid.SelectedItem is not LibraryItem item) return;
            if (action == "Open") await ActivateItemAsync(item);
            else if (action.StartsWith("Rating", StringComparison.Ordinal)) await ChangeRatingAsync(item, action[^1] - '0');
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Library input failed", ex);
            if (!_closed) await ShowMessageAsync(L10n.Get("MainWindow_FileOperations_032"), ex.Message);
        }
    }
    private void ThumbnailBlank_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(ThumbnailGrid).Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed) return;
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element != ThumbnailGrid)
        {
            // The entire item container (including image padding) remains selectable.
            // Scrollbar interactions must also preserve selection.
            if (element is GridViewItem or Microsoft.UI.Xaml.Controls.Primitives.ScrollBar or
                Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or Microsoft.UI.Xaml.Controls.Primitives.Thumb) return;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        if (element != ThumbnailGrid) return;
        CancelNavigationFocus();
        ThumbnailGrid.SelectedItems.Clear();
        e.Handled = true;
    }

    private async void GlobalPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CancelNavigationFocus();
        if (IsPreviewElement(e.OriginalSource)) return;
        if (_fileOperationBusy || _dialogDepth > 0 || _closed || InputRouting.Modifiers != 0 || InputRouting.IsControl(e.OriginalSource)) return;
        var mouse = InputRouting.Mouse(e, RootGrid);
        if (mouse is null) return;
        var action = AppServices.Inputs.Match(new InputGesture(Mouse: mouse), BindingRules.Library);
        if (action is null) return;
        bool inGrid = false;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (node == ThumbnailGrid) { inGrid = true; break; }
        if (action is not ("Back" or "Forward" or "Up") && !inGrid) return;
        e.Handled = true;
        await RunLibraryInputAsync(action);
    }
    private void SyncTree(string path)
    {
        _syncingTree = true;
        try
        {
            var current = FolderTree.RootNodes.FirstOrDefault();
            if (current is null) return;
            var target = Path.GetFullPath(path).TrimEnd('\\');
            while (true)
            {
                current.IsExpanded = true;
                if (current.Content is FolderNode data && data.CanExpand) TryExpandTreeNode(current, data);
                var next = current.Children.FirstOrDefault(n => n.Content is FolderNode f &&
                    (target.Equals(f.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                     target.StartsWith(f.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));
                if (next is null) break;
                current = next;
                if (current.Content is FolderNode selected && target.Equals(selected.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    current.IsExpanded = true;
                    TryExpandTreeNode(current, selected);
                    FolderTree.SelectedNode = current;
                    break;
                }
            }
        }
        finally { _syncingTree = false; }
    }

    private void RevealInitialFolderInTree()
    {
        var folder = _currentFolder;
        if (folder is null || ArchiveLocation.IsVirtual(folder)) return;

        // Wait for the tree's first layout before expanding the restored path.
        // A queued callback alone can still run before its flattened list is ready.
        AfterTreeLayout(() =>
        {
            if (_closed || !string.Equals(_currentFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
            SyncTree(folder);
            if (FolderTree.SelectedNode is not { Content: FolderNode selected } node ||
                !string.Equals(selected.Path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

            // Expansion updates the flattened list during layout. Scroll only a
            // current list item, and let ScrollIntoView realize its container.
            AfterTreeLayout(() =>
            {
                if (_closed || !string.Equals(_currentFolder, folder, StringComparison.OrdinalIgnoreCase) ||
                    FindTreeViewList(FolderTree) is not { IsLoaded: true } list) return;
                int index = list.Items.IndexOf(node);
                if (index >= 0) list.ScrollIntoView(list.Items[index]);
            });
        });
    }

    private void AfterTreeLayout(Action action)
    {
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            FolderTree.LayoutUpdated -= handler;
            if (!_closed) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_closed) action();
            });
        };
        FolderTree.LayoutUpdated += handler;
        FolderTree.InvalidateArrange();
    }

    private static TreeViewList? FindTreeViewList(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TreeViewList list) return list;
            if (FindTreeViewList(child) is { } nested) return nested;
        }
        return null;
    }

    private void QuickAccess_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: QuickAccessItem item } element) return;
        var menu = new MenuFlyout();
        var remove = new MenuFlyoutItem { Text = L10n.Get("MainWindow_xaml_010") };
        remove.Click += (_, _) => TryRemoveQuickAccess(item.Path);
        menu.Items.Add(remove);
        menu.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }
    private async Task ShowMessageAsync(string title, string message) => await ShowOwnedDialog(new ContentDialog { Title = title, Content = message, CloseButtonText = L10n.Get("MainWindow_xaml_011"), XamlRoot = RootGrid.XamlRoot });
    private static string SelectedTag(ComboBox comboBox, string fallback) => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
    private static void SelectCombo(ComboBox comboBox, string tag) => comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == tag) ?? comboBox.SelectedItem;
}
