using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace Lilium;

public sealed partial class MainWindow
{
    private LibraryItem? _pendingNavigationFocus;
    private FolderRequests.Request? _navigationFocusRequest;
    private bool _navigationFocusQueued;
    private ThumbnailRefreshState? _navigationRefreshState;
    private int _dialogDepth;
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private void CancelNavigationFocus()
    {
        _pendingNavigationFocus = null;
        _navigationFocusRequest = null;
        _navigationRefreshState = null;
    }

    private ThumbnailRefreshState? CaptureThumbnailRefreshState(bool restoreGridFocus = false)
    {
        if (_currentFolder is null) return null;
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        string? focusPath = null;
        bool inGrid = false;
        for (var node = focused as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is GridViewItem { Content: LibraryItem item }) focusPath = item.Path;
            if (ReferenceEquals(node, ThumbnailGrid)) { inGrid = true; break; }
        }
        focusPath ??= (ThumbnailGrid.LastFocusedItem as LibraryItem)?.Path;
        var selected = ThumbnailGrid.SelectedItems.OfType<LibraryItem>().Select(item => item.Path).ToArray();
        if (restoreGridFocus) focusPath ??= selected.FirstOrDefault();
        var scroll = FindThumbnailScrollViewer(ThumbnailGrid);
        return new ThumbnailRefreshState(_currentFolder, selected, focusPath,
            inGrid || (restoreGridFocus && !IsPreviewElement(focused)),
            scroll?.HorizontalOffset ?? 0, scroll?.VerticalOffset ?? 0);
    }

    private static ScrollViewer? FindThumbnailScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindThumbnailScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private void QueueThumbnailViewportRestore(FolderRequests.Request request, ThumbnailRefreshState? restore)
    {
        if (restore is null) return;
        // Focus/selection can ask WinUI to bring an item into view. Restore the
        // viewport after that layout, outside the layout event itself.
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            ThumbnailGrid.LayoutUpdated -= handler;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_closed || _dialogDepth != 0 || !_folderRequests.IsCurrent(request)) return;
                try
                {
                    if (FindThumbnailScrollViewer(ThumbnailGrid) is { } scroll)
                        scroll.ChangeView(Math.Min(restore.HorizontalOffset, scroll.ScrollableWidth),
                            Math.Min(restore.VerticalOffset, scroll.ScrollableHeight), null, disableAnimation: true);
                }
                catch (Exception ex) { App.WriteDiagnosticLog("Deferred thumbnail viewport restore failed", ex); }
            });
        };
        ThumbnailGrid.LayoutUpdated += handler;
        ThumbnailGrid.InvalidateArrange();
    }

    private bool CanApplyNavigationSelection(FolderRequests.Request request) =>
        !_closed && _dialogDepth == 0 && _folderRequests.IsCurrent(request) &&
        ReferenceEquals(_navigationFocusRequest, request);

    private void QueueFolderSelection(FolderRequests.Request request, LibraryItem[] selection,
        ThumbnailRefreshState? restore = null)
    {
        _navigationFocusRequest = request;
        _navigationRefreshState = restore;
        // Leave the collection/tree update and input event stack before selection
        // can trigger WinUI's synchronous scrolling and layout.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!CanApplyNavigationSelection(request)) return;
            try
            {
                if (!request.Navigation)
                {
                    foreach (var item in selection)
                    {
                        if (!CanApplyNavigationSelection(request)) return;
                        if (_items.Contains(item)) ThumbnailGrid.SelectedItems.Add(item);
                    }
                    var focusedItem = _items.FirstOrDefault(item =>
                        string.Equals(item.Path, restore?.FocusPath, StringComparison.OrdinalIgnoreCase));
                    if (focusedItem is not null) ThumbnailGrid.RememberNavigationFocus(focusedItem);
                    if (restore?.RestoreFocus == true && focusedItem is not null)
                    {
                        _pendingNavigationFocus = focusedItem;
                        ThumbnailGrid.ScrollIntoView(focusedItem);
                        ApplyNavigationFocus();
                    }
                    else
                    {
                        CancelNavigationFocus();
                        QueueThumbnailViewportRestore(request, restore);
                    }
                    return;
                }
                var target = selection.FirstOrDefault();
                if (target is null)
                {
                    CancelNavigationFocus();
                    ThumbnailGrid.Focus(FocusState.Programmatic);
                    return;
                }
                if (!_items.Contains(target)) return;
                // Do not focus the grid before setting SelectedItem: that makes
                // WinUI immediately move focus and re-enter layout from the setter.
                ThumbnailGrid.SelectedItem = target;
                if (!CanApplyNavigationSelection(request)) return;
                _pendingNavigationFocus = target;
                ThumbnailGrid.ScrollIntoView(target);
                ApplyNavigationFocus();
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_navigationFocusRequest, request)) CancelNavigationFocus();
                App.WriteDiagnosticLog("Deferred folder selection failed", ex);
            }
        })) CancelNavigationFocus();
    }

    private void ApplyNavigationFocus()
    {
        if (_navigationFocusQueued || _navigationFocusRequest is not { } request ||
            !CanApplyNavigationSelection(request) || _pendingNavigationFocus is not { } item) return;
        _navigationFocusQueued = true;
        // LayoutUpdated and container-generation notifications must not move
        // focus themselves. Coalesce them into one callback outside layout.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _navigationFocusQueued = false;
            if (!CanApplyNavigationSelection(request) || !ReferenceEquals(_pendingNavigationFocus, item))
            {
                ApplyNavigationFocus(); // A newer request may be waiting behind this callback.
                return;
            }
            try
            {
                if (ThumbnailGrid.ContainerFromItem(item) is Control container)
                {
                    // Clear before Focus, which can synchronously raise layout events.
                    var restore = _navigationRefreshState;
                    CancelNavigationFocus();
                    container.Focus(FocusState.Programmatic);
                    QueueThumbnailViewportRestore(request, restore);
                }
            }
            catch (Exception ex)
            {
                CancelNavigationFocus();
                App.WriteDiagnosticLog("Deferred folder focus failed", ex);
            }
        }))
        {
            _navigationFocusQueued = false;
            CancelNavigationFocus();
        }
    }

    private void ActivateDialogOwner()
    {
        if (_closed) return;
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private Windows.Foundation.IAsyncOperation<ContentDialogResult> ShowOwnedDialog(ContentDialog dialog)
    {
        dialog.XamlRoot = RootGrid.XamlRoot;
        dialog.DefaultButton = !string.IsNullOrEmpty(dialog.PrimaryButtonText) ? ContentDialogButton.Primary : ContentDialogButton.Close;
        CancelNavigationFocus();
        ActivateDialogOwner();
        // WinUI can activate a different app window while opening its popup.
        // Correct the owner once after opening, without a repeating focus-stealing timer.
        void Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            dialog.Opened -= Opened;
            ActivateDialogOwner();
            if (dialog.Content is TextBox input) { input.Focus(FocusState.Programmatic); input.SelectAll(); }
        }
        dialog.Opened += Opened;
        void Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            dialog.Closed -= Closed;
            _dialogDepth--;
        }
        dialog.Closed += Closed;
        _dialogDepth++;
        try { return dialog.ShowAsync(); }
        catch
        {
            dialog.Opened -= Opened;
            dialog.Closed -= Closed;
            _dialogDepth--;
            throw;
        }
    }
}
