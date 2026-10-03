using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Lilium;

// The same rendering, spread planning and controls run in both reader hosts.
internal sealed partial class ReaderView : UserControl, IDisposable
{
    private readonly Grid RootGrid = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
    private readonly Grid _viewport = new();
    private readonly Grid SpreadGrid = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image LeftImage = new() { Stretch = Stretch.Uniform };
    private readonly Image RightImage = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock PageText = new()
    {
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), Opacity = 0.8,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(12, 0, 12, 24), TextWrapping = TextWrapping.Wrap
    };
    private readonly ViewerLoadRequests _loadRequests = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _resizeTimer;
    private ViewerSource? _source;
    private ReaderDocument? _document;
    private int _index;
    private string _mode = "auto";
    private bool _rightBinding = true;
    private bool _loading;
    private bool _closed;
    private int _inputDialogDepth;
    private int _wheelDelta;
    private int _inputRevision = -1;

    internal bool IsPreview { get; }
    internal Func<bool>? CanReceiveInput { get; set; }
    internal Action<string>? BindingChanged { get; set; }
    internal Action? CloseRequested { get; set; }
    internal Action<ReaderDocument, int, string>? FullscreenRequested { get; set; }
    internal int PageIndex => _index;
    internal string Mode => _mode;
    internal string Binding => _rightBinding ? "right" : "left";
    internal ReaderDocument? CurrentDocument => _document;
    internal Task ReportIssueAsync(string title, string message) => ShowIssueAsync(title, message);
    private bool InputAvailable => !_closed && _inputDialogDepth == 0 && (CanReceiveInput?.Invoke() ?? true);
    private int InputScope => _rightBinding ? BindingRules.RightReader : BindingRules.LeftReader;

    internal ReaderView(bool preview)
    {
        IsPreview = preview;
        IsTabStop = true;
        RequestedTheme = ElementTheme.Dark;
        Language = L10n.Language;
        Content = RootGrid;
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SpreadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SpreadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SpreadGrid.Children.Add(LeftImage);
        SpreadGrid.Children.Add(RightImage);
        Grid.SetColumn(RightImage, 1);
        _viewport.Children.Add(SpreadGrid);
        _viewport.Children.Add(PageText);
        RootGrid.Children.Add(_viewport);
        InitializeReaderPanel();
        KeyDown += ReaderKeyDown;
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(MousePressed), true);
        RootGrid.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(MouseWheel), true);
        RootGrid.PointerExited += (_, _) => _wheelDelta = 0;
        _resizeTimer = DispatcherQueue.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(180);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += async (_, _) =>
        {
            if (_closed || _source is null) return;
            if (_loading) { _resizeTimer.Start(); return; }
            await ShowSpreadAsync(_index);
        };
        _viewport.SizeChanged += (_, _) =>
        {
            LayoutPages();
            if (_source is not null && !_closed) { _resizeTimer.Stop(); _resizeTimer.Start(); }
        };
    }

    internal void Clear()
    {
        _loadRequests.Cancel();
        _resizeTimer.Stop();
        _source?.Dispose();
        _source = null;
        _document = null;
        _loading = false;
        _wheelDelta = 0;
        _index = 0;
        LeftImage.Source = RightImage.Source = null;
        PageText.Text = "";
        _readerPanel.Visibility = Visibility.Collapsed;
    }

    internal void ShowStatus(string text)
    {
        Clear();
        PageText.Text = text;
    }

    internal async Task OpenAsync(ReaderDocument document, int index, string binding, string mode)
    {
        Clear();
        if (_closed) return;
        _document = document;
        _source = document.CreateSource();
        var source = _source;
        _index = Math.Max(0, index);
        _rightBinding = binding != "left";
        _mode = mode is "one" or "two" or "auto" ? mode : "auto";
        UpdateReaderPanel();
        var request = _loadRequests.Begin()!;
        _loading = true;
        PageText.Text = L10n.Get("Reader_Loading");
        bool ready = false;
        try
        {
            await source.InitializeAsync(request.CancellationToken);
            request.CancellationToken.ThrowIfCancellationRequested();
            ready = _loadRequests.IsCurrent(request);
            if (ready && source.Count == 0) PageText.Text = L10n.Get("Preview_EmptyFolder");
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Reader document open failed", ex);
            if (_loadRequests.IsCurrent(request))
            {
                await ShowIssueAsync(L10n.Get(source.IsPdf ? "Pdf_OpenError" : "Preview_OpenError"), ex.Message);
                if (!IsPreview && !_closed) CloseRequested?.Invoke();
            }
        }
        finally
        {
            if (_loadRequests.Complete(request)) _loading = false;
            request.Dispose();
        }
        if (ready && ReferenceEquals(_source, source) && !_closed) await ShowSpreadAsync(_index);
    }

    internal void ApplyBinding(string binding)
    {
        bool right = binding != "left";
        if (_rightBinding == right) return;
        _rightBinding = right;
        _wheelDelta = 0;
        if (LeftImage.Visibility == Visibility.Visible)
            (LeftImage.Source, RightImage.Source) = (RightImage.Source, LeftImage.Source);
        LayoutPages();
        UpdateReaderPanel();
    }

    private async void ReaderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || !InputAvailable || InputRouting.IsControl(FocusManager.GetFocusedElement(XamlRoot))) return;
        var gesture = InputRouting.Key(e);
        var action = AppServices.Inputs.Match(gesture, InputScope);
        if (action is null) return;
        if (IsPreview && PreviewRules.DisabledInputReason(action, gesture, AppServices.Inputs.Snapshot()) is not null) return;
        e.Handled = true;
        await RunReaderInputAsync(action);
    }

    private async Task RunReaderInputAsync(string action)
    {
        if (!InputAvailable) return;
        try
        {
            if (action == "CloseReader") { if (!IsPreview) CloseRequested?.Invoke(); return; }
            if (_loading || _source is null || _source.Count == 0) return;
            switch (action)
            {
                case "NextRight": case "NextLeft": await MovePageAsync(true); break;
                case "PreviousRight": case "PreviousLeft": await MovePageAsync(false); break;
                case "StepNext": await ShowSpreadAsync(_index + 1); break;
                case "StepPrevious": await ShowSpreadAsync(_index - 1); break;
                case "SinglePage": await ChangeModeAsync("one"); break;
                case "TwoPages": await ChangeModeAsync("two"); break;
                case "FirstPage": await ShowSpreadAsync(0); break;
                case "LastPage": await ShowSpreadAsync(_source.Count - 1); break;
                case "RightBinding": await ChangeBindingAsync(true); break;
                case "LeftBinding": await ChangeBindingAsync(false); break;
                case "Fullscreen":
                    if (_document is not null) FullscreenRequested?.Invoke(_document, _index, Binding);
                    break;
            }
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Reader input failed", ex);
            if (!_closed) await ShowIssueAsync(L10n.Get("MainWindow_FileOperations_032"), ex.Message);
        }
        finally { if (!_closed) UpdateReaderPanel(); }
    }

    private async Task ChangeModeAsync(string mode)
    {
        AppServices.Store.Set(IsPreview ? "preview_mode" : "viewer_mode", mode);
        _mode = mode;
        await ShowSpreadAsync(_index);
    }

    private Task ChangeBindingAsync(bool right)
    {
        string binding = right ? "right" : "left";
        AppServices.Store.Set("binding", binding);
        BindingChanged?.Invoke(binding);
        ApplyBinding(binding);
        return Task.CompletedTask;
    }

    private async Task ShowIssueAsync(string title, string message)
    {
        if (IsPreview) { PageText.Text = title + ": " + message; return; }
        _inputDialogDepth++;
        _wheelDelta = 0;
        try
        {
            await new ContentDialog
            {
                Title = title, Content = message, CloseButtonText = L10n.Get("ViewerWindow_xaml_007"), XamlRoot = XamlRoot
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Reader message failed", ex);
            if (!_closed) PageText.Text = title + ": " + message;
        }
        finally { _inputDialogDepth--; }
    }

    private async void MousePressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled || !InputAvailable) return;
        var mouse = InputRouting.Mouse(e, RootGrid);
        if (IsPreview && mouse == "Back") { e.Handled = true; return; }
        if (IsReaderPanelSource(e.OriginalSource) || InputRouting.Modifiers != 0 || InputRouting.IsControl(e.OriginalSource)) return;
        if (IsPreview && mouse == "Left") Focus(FocusState.Pointer);
        if (mouse is null) return;
        var action = AppServices.Inputs.Match(new InputGesture(Mouse: mouse), InputScope);
        if (action is null) return;
        e.Handled = true; // Includes disabled CloseReader bindings: never bubble to the list.
        if (IsPreview && action == "CloseReader") return;
        await RunReaderInputAsync(action);
    }

    private async void MouseWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled || !InputAvailable || IsReaderPanelSource(e.OriginalSource) || InputRouting.Modifiers != 0 || InputRouting.IsControl(e.OriginalSource)) { _wheelDelta = 0; return; }
        var properties = e.GetCurrentPoint(RootGrid).Properties;
        if (properties.IsHorizontalMouseWheel) return;
        if (_inputRevision != AppServices.Inputs.Revision) { _wheelDelta = 0; _inputRevision = AppServices.Inputs.Revision; }
        var delta = properties.MouseWheelDelta;
        if (delta == 0) return;
        var action = AppServices.Inputs.Match(new InputGesture(Mouse: delta < 0 ? "WheelDown" : "WheelUp"), InputScope);
        if (action is null) { _wheelDelta = 0; return; }
        e.Handled = true;
        if ((IsPreview && action == "CloseReader") || (_loading && action != "CloseReader")) { _wheelDelta = 0; return; }
        if (Math.Sign(_wheelDelta) != Math.Sign(delta)) _wheelDelta = 0;
        _wheelDelta += delta;
        if (Math.Abs(_wheelDelta) < 120) return;
        _wheelDelta = 0;
        await RunReaderInputAsync(action);
    }

    private async Task MovePageAsync(bool next)
    {
        var source = _source;
        if (source is null || source.Count == 0) return;
        if (next)
        {
            int target = ViewerPagePlanner.NextIndex(source.Count, _index, _mode, source.IsPortrait);
            if (target != _index) await ShowSpreadAsync(target);
        }
        else if (_index > 0)
        {
            int index = _index;
            await ShowSpreadAsync(async cancellation =>
            {
                if (index > 0) await source.EnsureOrientationAsync(index - 1, cancellation);
                if (index > 1) await source.EnsureOrientationAsync(index - 2, cancellation);
                return ViewerPagePlanner.PreviousIndex(source.Count, index, _mode, source.IsPortrait);
            });
        }
    }

    private Task ShowSpreadAsync(int index) => ShowSpreadAsync(_ => Task.FromResult(index));

    private async Task ShowSpreadAsync(Func<CancellationToken, Task<int>> resolveIndex)
    {
        var source = _source;
        if (source is null || source.Count == 0 || _closed) return;
        var request = _loadRequests.Begin();
        if (request is null) return;
        _loading = true;
        UpdateReaderPanel();
        try
        {
            int index = Math.Clamp(await resolveIndex(request.CancellationToken), 0, source.Count - 1);
            await source.EnsureOrientationAsync(index, request.CancellationToken);
            if (index + 1 < source.Count) await source.EnsureOrientationAsync(index + 1, request.CancellationToken);
            bool two = ViewerPagePlanner.SpreadLength(source.Count, index, _mode, source.IsPortrait) == 2;
            var scale = XamlRoot?.RasterizationScale ?? 1;
            double width = Math.Max(1, _viewport.ActualWidth * scale) / (two ? 2 : 1);
            double height = Math.Max(1, _viewport.ActualHeight * scale);
            var first = await LoadAsync(source, index, width, height, request.CancellationToken);
            var second = two ? await LoadAsync(source, index + 1, width, height, request.CancellationToken) : null;
            request.CancellationToken.ThrowIfCancellationRequested();
            if (!_loadRequests.IsCurrent(request)) return;
            _index = index;
            RightImage.Source = _rightBinding || second is null ? first : second;
            LeftImage.Source = _rightBinding ? second : first;
            if (second is null) LeftImage.Source = null;
            LeftImage.Visibility = second is null ? Visibility.Collapsed : Visibility.Visible;
            RightImage.Visibility = Visibility.Visible;
            PageText.Text = second is null ? $"{index + 1} / {source.Count}" : $"{index + 1}–{index + 2} / {source.Count}";
            LayoutPages();
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Reader page load failed", ex);
            if (_loadRequests.IsCurrent(request))
            {
                // Do not retain the previous page under the failed page's identity.
                LeftImage.Source = RightImage.Source = null;
                PageText.Text = L10n.Format("ViewerWindow_xaml_008", ex.Message);
                if (source.IsPdf && !IsPreview) await ShowIssueAsync(L10n.Get("Pdf_PageError"), ex.Message);
            }
        }
        finally
        {
            if (_loadRequests.Complete(request)) { _loading = false; UpdateReaderPanel(); }
            request.Dispose();
        }
    }

    private async Task<BitmapImage> LoadAsync(ViewerSource source, int index, double width, double height, CancellationToken cancellation)
    {
        using var stream = await source.OpenPageAsync(index, width, height, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var bitmap = new BitmapImage();
        if (IsPreview)
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            cancellation.ThrowIfCancellationRequested();
            double scale = Math.Min(1, Math.Min(width / decoder.OrientedPixelWidth, height / decoder.OrientedPixelHeight));
            bitmap.DecodePixelWidth = (int)Math.Max(1, decoder.OrientedPixelWidth * scale);
            bitmap.DecodePixelType = DecodePixelType.Physical;
            stream.Seek(0);
        }
        await bitmap.SetSourceAsync(stream);
        cancellation.ThrowIfCancellationRequested();
        return bitmap;
    }

    private void LayoutPages()
    {
        if (RightImage.Source is not BitmapImage right || right.PixelHeight == 0) return;
        double rightRatio = (double)right.PixelWidth / right.PixelHeight;
        var left = LeftImage.Visibility == Visibility.Visible ? LeftImage.Source as BitmapImage : null;
        double leftRatio = left is { PixelHeight: > 0 } ? (double)left.PixelWidth / left.PixelHeight : 0;
        double height = Math.Min(_viewport.ActualHeight, _viewport.ActualWidth / (rightRatio + leftRatio));
        if (height <= 0) return;
        RightImage.Width = height * rightRatio;
        RightImage.Height = height;
        LeftImage.Width = height * leftRatio;
        LeftImage.Height = height;
    }

    public void Dispose()
    {
        if (_closed) return;
        Clear();
        _closed = true;
        _loadRequests.Close();
    }
}
