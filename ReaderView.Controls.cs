using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Lilium;

internal sealed partial class ReaderView
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
        if (IsPreview)
        {
            _navigationButtons.HorizontalAlignment = HorizontalAlignment.Center;
            _readerPanel.Margin = new Thickness(8, 0, 8, 8);
            _readerPanel.Padding = new Thickness(4);
            _readerPanel.CornerRadius = new CornerRadius(4);
            _readerPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(_readerPanel, 1);
            _readerButtons["CloseReader"].Visibility = Visibility.Collapsed;
            var fullscreen = CreateReaderButton("Fullscreen", false);
            fullscreen.Content = L10n.Get("Preview_Fullscreen");
            fullscreen.HorizontalAlignment = HorizontalAlignment.Center;
            rows.Children.Add(fullscreen);
            _readerButtons.Add("Fullscreen", fullscreen);
        }
        // The panel remains usable on small displays without cropping its buttons.
        var panelScroll = new ScrollViewer
        {
            Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled
        };
        if (IsPreview) panelScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _readerPanel.Child = panelScroll;
        RootGrid.Children.Add(_readerPanel);
        RootGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ReaderPointerMoved), true);
        RootGrid.PointerExited += (_, e) =>
        {
            if (IsPreview) return;
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
        string label = L10n.Get(action == "Fullscreen" ? "Preview_Fullscreen" : "ReaderPanel_" + action);
        button.Content = IsPreview ? action switch
        {
            "FirstPage" => "|◀", "LastPage" => "▶|", "Next" => "◀", "Previous" => "▶",
            "SinglePage" => "1", "TwoPages" => "2",
            "LeftBinding" => L10n.Get("Preview_LeftBinding"), "RightBinding" => L10n.Get("Preview_RightBinding"), _ => label
        } : label;
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.MinWidth = IsPreview ? 44 : 88;
        button.Padding = IsPreview ? new Thickness(8, 4, 8, 4) : new Thickness(12, 8, 12, 8);
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
            if (!_closed && action != "Fullscreen") Focus(FocusState.Programmatic);
        };
        return button;
    }

    private void UpdateReaderPanel()
    {
        if (IsPreview)
        {
            _readerPanel.Visibility = _source is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in _readerButtons.Values) button.IsEnabled = !_loading;
        }
        if (_panelRightBinding != _rightBinding)
        {
            _navigationButtons.Children.Clear();
            string[] order = ["LastPage", "Next", "Previous", "FirstPage"];
            if (!_rightBinding) Array.Reverse(order);
            foreach (var action in order) _navigationButtons.Children.Add(_readerButtons[action]);
            _navigationButtons.Children.Add(_readerButtons["CloseReader"]);
            _panelRightBinding = _rightBinding;
        }
        if (IsPreview)
        {
            _readerButtons["Next"].Content = _rightBinding ? "◀" : "▶";
            _readerButtons["Previous"].Content = _rightBinding ? "▶" : "◀";
            _readerButtons["FirstPage"].Content = _rightBinding ? "▶|" : "|◀";
            _readerButtons["LastPage"].Content = _rightBinding ? "|◀" : "▶|";
        }
        ((ToggleButton)_readerButtons["SinglePage"]).IsChecked = _mode == "one";
        ((ToggleButton)_readerButtons["TwoPages"]).IsChecked = _mode == "two";
        ((ToggleButton)_readerButtons["RightBinding"]).IsChecked = _rightBinding;
        ((ToggleButton)_readerButtons["LeftBinding"]).IsChecked = !_rightBinding;
    }

    private void ReaderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (IsPreview) return;
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

}

