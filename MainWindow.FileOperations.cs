using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Microsoft.VisualBasic.FileIO;

namespace Lilium;

public sealed partial class MainWindow
{
    private bool _fileOperationBusy;

    private async void Up_Click(object sender, RoutedEventArgs e) => await GoUpAsync();

    private void Files_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ArchiveLocation.IsVirtual(_currentFolder)) return;
        var menu = new MenuFlyout();
        var pasteDestination = FolderAt(e.OriginalSource) ?? _currentFolder;
        var selected = ThumbnailGrid.SelectedItems.OfType<LibraryItem>().ToArray();
        void Add(string label, string command, bool enabled)
        {
            var entry = new MenuFlyoutItem { Text = label, IsEnabled = enabled && !_fileOperationBusy };
            entry.Click += async (_, _) => await FileCommandAsync(command, command == "paste" ? pasteDestination : null, command == "paste");
            menu.Items.Add(entry);
        }
        Add(L10n.Get("MainWindow_FileOperations_001"), "copy", selected.Length > 0);
        Add(L10n.Get("MainWindow_FileOperations_002"), "cut", selected.Length > 0);
        Add(L10n.Get("MainWindow_FileOperations_003"), "paste", _currentFolder is not null);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(L10n.Get("MainWindow_FileOperations_004"), "rename", selected.Length == 1);
        Add(L10n.Get("MainWindow_FileOperations_005"), "delete", selected.Length > 0);
        menu.Items.Add(new MenuFlyoutSeparator());
        var ratings = new MenuFlyoutSubItem { Text = L10n.Get("MainWindow_FileOperations_006"), IsEnabled = selected.Length == 1 && !_fileOperationBusy };
        for (int r = 0; r <= 5; r++)
        {
            int rating = r;
            var entry = new MenuFlyoutItem { Text = r == 0 ? L10n.Get("MainWindow_FileOperations_007") : $"★{r}" };
            entry.Click += async (_, _) => { if (selected.Length == 1) await ChangeRatingAsync(selected[0], rating); };
            ratings.Items.Add(entry);
        }
        menu.Items.Add(ratings);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(L10n.Get("MainWindow_FileOperations_008"), "new", _currentFolder is not null);
        Add(L10n.Get("MainWindow_FileOperations_009"), "undo", true);
        Add(L10n.Get("MainWindow_FileOperations_010"), "selectall", true);
        Add(L10n.Get("MainWindow_FileOperations_011"), "deselect", true);
        menu.Items.Add(new MenuFlyoutSeparator());
        if (selected.Length == 1 && selected[0].IsFolder) Add(L10n.Get("MainWindow_FileOperations_012"), "window", true);
        Add(L10n.Get("MainWindow_FileOperations_013"), "explorer", selected.Length == 1);
        if (selected.Length == 1 && selected[0].IsFolder)
        {
            Add(L10n.Get("MainWindow_FileOperations_014"), "cache", true);
            menu.Items.Add(new MenuFlyoutSeparator());
            var folder = selected[0].Path;
            var add = new MenuFlyoutItem { Text = L10n.Get("MainWindow_FileOperations_015") };
            add.Click += (_, _) => TryAddQuickAccess(folder);
            menu.Items.Add(add);
        }
        menu.ShowAt(ThumbnailGrid, e.GetPosition(ThumbnailGrid));
        e.Handled = true;
    }

    private async Task FileCommandAsync(string command, string? pasteDestination = null, bool allowDuplicate = false, LibraryItem[]? targets = null)
    {
        if (_fileOperationBusy) return;
        var selected = targets ?? ThumbnailGrid.SelectedItems.OfType<LibraryItem>().ToArray();
        var destination = pasteDestination ?? _currentFolder;
        if (command is not ("refresh" or "selectall" or "deselect") &&
            ((targets is null && ArchiveLocation.IsVirtual(_currentFolder)) ||
             selected.Any(item => item.IsVirtual) || ArchiveLocation.IsVirtual(destination))) return;
        _fileOperationBusy = true;
        try
        {
            if (command == "refresh") { await RefreshItemsAsync(); return; }
            if (command == "selectall") { ThumbnailGrid.SelectAll(); return; }
            if (command == "deselect") { ThumbnailGrid.SelectedItems.Clear(); return; }
            if (command == "undo") { await UndoLastAsync(); return; }
            if (command == "cache" && selected.Length == 1)
            {
                await FileCatalog.ClearFolderThumbnailsAsync(selected[0].Path);
                await RefreshItemsAsync();
                return;
            }
            if (command == "window" && selected.Length == 1) { new MainWindow(selected[0].Path).Activate(); return; }
            if (command == "explorer" && selected.Length == 1)
            {
                var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.Arguments = "/select," + (char)34 + selected[0].Path + (char)34;
                System.Diagnostics.Process.Start(start);
                return;
            }
            if (command == "new" && destination is not null)
            {
                var input = new TextBox { Text = L10n.Get("MainWindow_FileOperations_016") };
                var dialog = new ContentDialog { Title = L10n.Get("MainWindow_FileOperations_017"), Content = input, PrimaryButtonText = L10n.Get("MainWindow_FileOperations_018"), CloseButtonText = L10n.Get("MainWindow_FileOperations_019"), XamlRoot = RootGrid.XamlRoot };
                if (await ShowOwnedDialog(dialog) != ContentDialogResult.Primary) return;
                var name = input.Text.Trim();
                if (!IsValidItemName(name)) throw new IOException(L10n.Get("MainWindow_FileOperations_020"));
                var target = Path.Combine(destination, name);
                await RunRequestsAsync([new() { Kind = "New", Target = target }]);
            }
            else if (command is "copy" or "cut")
            {
                if (selected.Length == 0) return;
                var storageItems = new List<IStorageItem>();
                foreach (var item in selected)
                    storageItems.Add(item.IsFolder ? await StorageFolder.GetFolderFromPathAsync(item.Path) : await StorageFile.GetFileFromPathAsync(item.Path));
                var package = new DataPackage { RequestedOperation = command == "cut" ? DataPackageOperation.Move : DataPackageOperation.Copy };
                package.SetStorageItems(storageItems);
                Clipboard.SetContent(package);
            }
            else if (command == "paste" && destination is not null)
            {
                var clipboard = Clipboard.GetContent();
                if (!clipboard.Contains(StandardDataFormats.StorageItems)) return;
                var sources = await clipboard.GetStorageItemsAsync();
                bool move = clipboard.RequestedOperation == DataPackageOperation.Move;
                if (await TransferAsync(sources, destination, move, allowDuplicate))
                    clipboard.ReportOperationCompleted(move ? DataPackageOperation.Move : DataPackageOperation.Copy);
            }
            else if (command == "rename" && selected.Length == 1)
            {
                var item = selected[0];
                var input = new TextBox { Text = item.DisplayName, Width = Math.Min(640, Math.Max(200, RootGrid.ActualWidth - 100)) };
                var dialog = new ContentDialog { Title = L10n.Get("MainWindow_FileOperations_021"), Content = input, PrimaryButtonText = L10n.Get("MainWindow_FileOperations_022"), CloseButtonText = L10n.Get("MainWindow_FileOperations_023"), XamlRoot = RootGrid.XamlRoot };
                dialog.Resources["ContentDialogMaxWidth"] = 1100.0;
                if (await ShowOwnedDialog(dialog) != ContentDialogResult.Primary) return;
                var name = input.Text.Trim();
                if (!IsValidItemName(name))
                    throw new IOException(L10n.Get("MainWindow_FileOperations_024"));
                var target = Path.Combine(Path.GetDirectoryName(item.Path)!, name);
                if (item.Path == target) return;
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException(L10n.Get("MainWindow_FileOperations_025"));
                await RunRequestsAsync([new() { Kind = "Move", Source = item.Path, Target = target }]);
            }
            else if (command == "delete" && selected.Length > 0)
            {
                var dialog = new ContentDialog
                {
                    Title = L10n.Get("MainWindow_FileOperations_026"), Content = L10n.Format("MainWindow_FileOperations_027", selected.Length) + string.Join("\n", selected.Take(8).Select(i => i.DisplayName)),
                    PrimaryButtonText = L10n.Get("MainWindow_FileOperations_028"), CloseButtonText = L10n.Get("MainWindow_FileOperations_029"), DefaultButton = ContentDialogButton.Close, XamlRoot = RootGrid.XamlRoot
                };
                if (await ShowOwnedDialog(dialog) != ContentDialogResult.Primary) return;
                var requests = new List<FileOperationJournal.Request>();
                foreach (var item in selected)
                {
                    if (selected.Any(parent => parent.IsFolder && !ReferenceEquals(parent, item) &&
                        item.Path.StartsWith(parent.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                    requests.Add(new() { Kind = "Delete", Source = item.Path });
                }
                await RunRequestsAsync(requests);
            }
        }
        catch (OperationCanceledException) { await ShowMessageAsync(L10n.Get("MainWindow_FileOperations_030"), L10n.Get("MainWindow_FileOperations_031")); }
        catch (Exception ex) { await ShowMessageAsync(L10n.Get("MainWindow_FileOperations_032"), ex.Message); }
        finally
        {
            _transferCancellation?.Dispose();
            _transferCancellation = null;
            try
            {
            if (command is "paste" or "rename" or "delete" or "undo" or "new")
            {
                try { await RefreshAfterFileOperationAsync(); }
                catch (Exception ex) { await ShowMessageAsync(L10n.Get("MainWindow_FileOperations_033"), L10n.Get("MainWindow_FileOperations_034") + ex.Message); }
            }
            }
            finally { _fileOperationBusy = false; }
            
        }
    }

    private static bool IsValidItemName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.');

}
