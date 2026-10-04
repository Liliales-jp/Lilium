using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Lilium;

// Navigation originating in the reader cannot be routed to GridView's native
// key handler: the event retains the reader as its original source.
public sealed class ThumbnailGridView : GridView
{
    private int _focusIndex = -1;
    private int _anchorIndex = -1;
    private object? _pendingFocus;

    internal object? LastFocusedItem => _focusIndex >= 0 && _focusIndex < Items.Count ? Items[_focusIndex] : null;

    internal void ResetNavigationFocus()
    {
        _pendingFocus = null;
        _focusIndex = _anchorIndex = -1;
    }

    internal void RememberNavigationFocus(object item)
    {
        _focusIndex = _anchorIndex = Items.IndexOf(item);
    }

    public ThumbnailGridView()
    {
        GotFocus += (_, e) =>
        {
            for (var node = e.OriginalSource as DependencyObject; node is not null && node != this;
                node = VisualTreeHelper.GetParent(node))
                if (node is GridViewItem item)
                {
                    _focusIndex = IndexFromContainer(item);
                    if (!InputRouting.Modifiers.HasFlag(KeyModifiers.Shift)) _anchorIndex = _focusIndex;
                    break;
                }
        };
        LayoutUpdated += (_, _) => ApplyPendingFocus();
    }

    internal void ForwardNavigationKey(KeyRoutedEventArgs args)
    {
        args.Handled = true;
        if (Items.Count == 0) return;
        var current = _focusIndex >= 0 && _focusIndex < Items.Count ? _focusIndex : Math.Max(0, SelectedIndex);
        var container = ContainerFromIndex(current) as FrameworkElement ??
            ContainerFromIndex(Math.Max(0, SelectedIndex)) as FrameworkElement;
        var panel = ItemsPanelRoot as ItemsWrapGrid;
        var width = container?.ActualWidth ?? 1;
        var height = container?.ActualHeight ?? 1;
        var columns = Math.Max(1, (int)Math.Floor((panel?.ActualWidth ?? ActualWidth) / Math.Max(1, width)));
        var rows = Math.Max(1, (int)Math.Floor(ActualHeight / Math.Max(1, height)));
        var target = ThumbnailNavigation.Target(current, Items.Count, columns, rows, (int)args.Key,
            FlowDirection == FlowDirection.RightToLeft);
        var modifiers = InputRouting.Modifiers;
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            if (_anchorIndex < 0 || _anchorIndex >= Items.Count) _anchorIndex = current;
            if (!modifiers.HasFlag(KeyModifiers.Control)) SelectedItems.Clear();
            for (var i = Math.Min(_anchorIndex, target); i <= Math.Max(_anchorIndex, target); i++)
                if (!SelectedItems.Contains(Items[i])) SelectedItems.Add(Items[i]);
        }
        else
        {
            _anchorIndex = target;
            if (!modifiers.HasFlag(KeyModifiers.Control))
            {
                SelectedItems.Clear();
                SelectedItem = Items[target];
            }
        }
        _focusIndex = target;
        _pendingFocus = Items[target];
        ScrollIntoView(_pendingFocus);
        ApplyPendingFocus();
    }

    private void ApplyPendingFocus()
    {
        if (_pendingFocus is not { } item || ContainerFromItem(item) is not Control container) return;
        _pendingFocus = null;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Items.Contains(item) && ReferenceEquals(ContainerFromItem(item), container))
                container.Focus(FocusState.Keyboard);
        });
    }
}
