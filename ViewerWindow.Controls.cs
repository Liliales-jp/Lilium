using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Lilium;

public sealed partial class ViewerWindow
{
    private readonly Border _readerPanel = new()
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 0, 0, 0)),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(16),
        Margin = new Thickness(16, 0, 16, 56),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Visibility = Visibility.Collapsed,
        RequestedTheme = ElementTheme.Dark
    };
    private readonly StackPanel _navigationButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Dictionary<string, ButtonBase> _readerButtons = new();
    private bool? _panelRightBinding;

    private void InitializeReaderPanel()
    {
        var rows = new StackPanel { Spacing = 8 };
        rows.Children.Add(_navigationButtons);
        var settings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var action in new[] { "LastPage", "Next", "Previous", "FirstPage", "CloseReader" })
            _readerButtons.Add(action, CreateReaderButton(action, false));
        foreach (var action in new[] { "SinglePage", "TwoPages", "LeftBinding", "RightBinding" })
        {
            var button = CreateReaderButton(action, true);
            _readerButtons.Add(action, button);
            settings.Children.Add(button);
        }
        rows.Children.Add(settings);
        // The panel remains usable on small displays without cropping its buttons.
        _readerPanel.Child = new ScrollViewer
        {
            Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled
        };
        RootGrid.Children.Add(_readerPanel);
        RootGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ReaderPointerMoved), true);
        RootGrid.PointerExited += (_, e) =>
        {
            var p = e.GetCurrentPoint(RootGrid).Position;
            if (p.X < 0 || p.Y < 0 || p.X >= RootGrid.ActualWidth || p.Y >= RootGrid.ActualHeight)
                _readerPanel.Visibility = Visibility.Collapsed;
        };
        RootGrid.SizeChanged += (_, _) => _readerPanel.MaxWidth = Math.Max(1, RootGrid.ActualWidth - 32);
        UpdateReaderPanel();
    }

    private ButtonBase CreateReaderButton(string action, bool toggle)
    {
        ButtonBase button = toggle ? new ToggleButton() : new Button();
        button.Content = L10n.Get("ReaderPanel_" + action);
        button.MinWidth = 88;
        button.MinHeight = 36;
        button.Click += async (_, _) =>
        {
            var command = action switch
            {
                "Next" => _rightBinding ? "NextRight" : "NextLeft",
                "Previous" => _rightBinding ? "PreviousRight" : "PreviousLeft",
                _ => action
            };
            await RunReaderInputAsync(command);
            // Pointer clicks must not leave subsequent reading shortcuts on a button.
            if (!_closed) RootGrid.Focus(FocusState.Programmatic);
        };
        return button;
    }

    private void UpdateReaderPanel()
    {
        if (_panelRightBinding != _rightBinding)
        {
            _navigationButtons.Children.Clear();
            string[] order = ["LastPage", "Next", "Previous", "FirstPage"];
            if (!_rightBinding) Array.Reverse(order);
            foreach (var action in order) _navigationButtons.Children.Add(_readerButtons[action]);
            _navigationButtons.Children.Add(_readerButtons["CloseReader"]);
            _panelRightBinding = _rightBinding;
        }
        ((ToggleButton)_readerButtons["SinglePage"]).IsChecked = _mode == "one";
        ((ToggleButton)_readerButtons["TwoPages"]).IsChecked = _mode == "two";
        ((ToggleButton)_readerButtons["RightBinding"]).IsChecked = _rightBinding;
        ((ToggleButton)_readerButtons["LeftBinding"]).IsChecked = !_rightBinding;
    }

    private void ReaderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_closed || _inputDialogDepth > 0) return;
        var p = e.GetCurrentPoint(RootGrid).Position;
        bool visible = p.Y >= RootGrid.ActualHeight * 0.9;
        if (_readerPanel.Visibility == Visibility.Visible)
        {
            // Include the full two-row panel even when it extends above the bottom 10%.
            var topLeft = _readerPanel.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point());
            visible |= p.X >= topLeft.X && p.X <= topLeft.X + _readerPanel.ActualWidth &&
                p.Y >= topLeft.Y && p.Y <= RootGrid.ActualHeight;
        }
        _readerPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsReaderPanelSource(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, _readerPanel)) return true;
        return false;
    }

    private Task ChangeBindingAsync(bool right)
    {
        string binding = right ? "right" : "left";
        // Save before publishing either window's new state.
        AppServices.Store.Set("binding", binding);
        _libraryOwner?.ApplyBindingFromReader(binding);
        if (_rightBinding == right) return Task.CompletedTask;
        _rightBinding = right;
        _wheelDelta = 0;
        // Swap the already loaded spread, avoiding I/O and preserving the page index.
        if (LeftImage.Visibility == Visibility.Visible)
            (LeftImage.Source, RightImage.Source) = (RightImage.Source, LeftImage.Source);
        LayoutPages();
        return Task.CompletedTask;
    }
}
