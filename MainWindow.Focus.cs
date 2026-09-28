using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace Lilium;

public sealed partial class MainWindow
{
    private LibraryItem? _pendingNavigationFocus;
    private FolderRequests.Request? _navigationFocusRequest;
    private bool _navigationFocusQueued;
    private int _dialogDepth;
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private void CancelNavigationFocus()
    {
        _pendingNavigationFocus = null;
        _navigationFocusRequest = null;
    }

    private bool CanApplyNavigationSelection(FolderRequests.Request request) =>
        !_closed && _dialogDepth == 0 && _folderRequests.IsCurrent(request) &&
        ReferenceEquals(_navigationFocusRequest, request);

    private void QueueFolderSelection(FolderRequests.Request request, LibraryItem[] selection)
    {
        _navigationFocusRequest = request;
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
                    CancelNavigationFocus();
                    container.Focus(FocusState.Programmatic);
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
