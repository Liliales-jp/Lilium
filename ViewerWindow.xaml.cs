using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Lilium;

public sealed partial class ViewerWindow : Window
{
    private readonly ViewerSource _source;
    private readonly MainWindow? _libraryOwner;
    private int _index;
    private string _mode = "auto";
    private bool _rightBinding = true;
    private bool _loading;
    private bool _closed;
    private bool _started;
    private readonly ViewerLoadRequests _loadRequests = new();
    private string? _settingsIssue;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _activationTimer;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    public void ShowForeground()
    {
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        // A double-click's final pointer release can reactivate the library.
        // Reassert activation once after that gesture, never continuously.
        _activationTimer ??= DispatcherQueue.CreateTimer();
        _activationTimer.Stop();
        _activationTimer.Interval = TimeSpan.FromMilliseconds(200);
        _activationTimer.IsRepeating = false;
        _activationTimer.Tick -= FinishActivation;
        _activationTimer.Tick += FinishActivation;
        _activationTimer.Start();
    }

    private void FinishActivation(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_closed) return;
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        RootGrid.Focus(FocusState.Programmatic);
    }

    public ViewerWindow(List<LibraryItem> images, int index, string binding, Window? libraryOwner = null)
        : this(new ImageViewerSource(images.Select(item => item.Path).ToArray()), index, binding, libraryOwner) { }

    public ViewerWindow(string pdfPath, string binding, Window? libraryOwner = null)
        : this(new PdfViewerSource(pdfPath), 0, binding, libraryOwner) { }

    private ViewerWindow(ViewerSource source, int index, string binding, Window? libraryOwner)
    {
        var owner = libraryOwner ?? App.MainWindow;
        _source = source;
        _libraryOwner = owner as MainWindow;
        _index = Math.Max(0, index);
        if (binding is "right" or "left") _rightBinding = binding == "right";
        else _settingsIssue = L10n.Get("ViewerWindow_xaml_001");
        InitializeComponent();
        RootGrid.Language = L10n.Language;
        try
        {
            var mode = AppServices.Store.Get("viewer_mode", "auto");
            if (mode is "auto" or "one" or "two") _mode = mode;
            else _settingsIssue = AppendIssue(_settingsIssue, L10n.Get("ViewerWindow_xaml_002"));
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Viewer setting read failed", ex);
            _settingsIssue = AppendIssue(_settingsIssue,
                L10n.Get("ViewerWindow_xaml_003") + ex.Message);
        }
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Lilium.ico"));
        InitializeReaderPanel();
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(MousePressed), true);
        RootGrid.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(MouseWheel), true);
        // An owned reader stays above its library window without being globally topmost.
        if (owner is not null)
            SetWindowLongPtr(WinRT.Interop.WindowNative.GetWindowHandle(this), -8,
                WinRT.Interop.WindowNative.GetWindowHandle(owner));
        RootGrid.SizeChanged += (_, _) => LayoutPages();
        Closed += (_, _) =>
        {
            _closed = true;
            _loadRequests.Close();
            _source.Dispose();
            _activationTimer?.Stop();
            LeftImage.Source = null;
            RightImage.Source = null;
        };
        // Place the hidden window on its owner's current display before activation
        // and fullscreen presentation. DisplayArea coordinates are physical pixels,
        // including negative desktop coordinates on displays to the left or above.
        var display = DisplayArea.GetFromWindowId(owner?.AppWindow.Id ?? AppWindow.Id,
            DisplayAreaFallback.Nearest);
        if (display is not null) AppWindow.MoveAndResize(display.WorkArea);
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started || _closed) return;
        _started = true;
        ShowForeground();
        RootGrid.Focus(FocusState.Programmatic);
        if (_settingsIssue is not null) await ShowSettingsIssueAsync(L10n.Get("ViewerWindow_xaml_004"), _settingsIssue);
        if (_closed) return;
        var request = _loadRequests.Begin();
        if (request is null) return;
        _loading = true;
        PageText.Text = L10n.Get("Reader_Loading");
        try
        {
            await _source.InitializeAsync(request.CancellationToken);
            request.CancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (_closed || request.CancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Reader document open failed", ex);
            if (!_closed)
            {
                await ShowSettingsIssueAsync(L10n.Get("Pdf_OpenError"), ex.Message);
                if (!_closed) Close();
            }
            return;
        }
        finally
        {
            if (_loadRequests.Complete(request)) _loading = false;
            request.Dispose();
        }
        if (!_closed) await ShowSpreadAsync(_index);
    }

    private int _inputDialogDepth;
    private int InputScope => _rightBinding ? BindingRules.RightReader : BindingRules.LeftReader;
    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || _closed || _inputDialogDepth > 0 || InputRouting.IsControl(FocusManager.GetFocusedElement(RootGrid.XamlRoot))) return;
        var action = AppServices.Inputs.Match(InputRouting.Key(e), InputScope);
        if (action is null) return;
        e.Handled = true;
        await RunReaderInputAsync(action);
    }

    private async Task RunReaderInputAsync(string action)
    {
        if (_closed || _inputDialogDepth > 0) return;
        try
        {
            if (action == "CloseReader") { Close(); return; }
            if (_loading) return;
            switch (action)
            {
                case "NextRight": case "NextLeft": await MovePageAsync(true); break;
                case "PreviousRight": case "PreviousLeft": await MovePageAsync(false); break;
                case "StepNext": await MoveAsync(1); break;
                case "StepPrevious": await MoveAsync(-1); break;
                case "SinglePage": await ChangeModeAsync("one"); break;
                case "TwoPages": await ChangeModeAsync("two"); break;
                case "FirstPage": await ShowSpreadAsync(0); break;
                case "LastPage": await ShowSpreadAsync(_source.Count - 1); break;
                case "RightBinding": await ChangeBindingAsync(true); break;
                case "LeftBinding": await ChangeBindingAsync(false); break;
            }
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Reader input failed", ex);
            if (!_closed) await ShowSettingsIssueAsync(L10n.Get("MainWindow_FileOperations_032"), ex.Message);
        }
        finally { if (!_closed) UpdateReaderPanel(); }
    }
    private async Task ChangeModeAsync(string mode)
    {
        try { AppServices.Store.Set("viewer_mode", mode); }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Viewer mode setting save failed", ex);
            await ShowSettingsIssueAsync(L10n.Get("ViewerWindow_xaml_005"),
                L10n.Get("ViewerWindow_xaml_006") + ex.Message);
            return;
        }
        _mode = mode;
        await ShowSpreadAsync(_index);
    }

    private async Task ShowSettingsIssueAsync(string title, string message)
    {
        _inputDialogDepth++;
        _wheelDelta = 0;
        try
        {
            await new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = L10n.Get("ViewerWindow_xaml_007"),
                XamlRoot = RootGrid.XamlRoot
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Viewer setting message failed", ex);
            if (!_closed) PageText.Text = title + ": " + message;
        }
        finally { _inputDialogDepth--; }
    }

    private static string AppendIssue(string? current, string next) =>
        string.IsNullOrEmpty(current) ? next : current + "\n" + next;

    private async Task MoveAsync(int amount)
    {
        if (_source.Count == 0) return;
        await ShowSpreadAsync(Math.Clamp(_index + amount, 0, _source.Count - 1));
    }

    private int _wheelDelta;
    private int _inputRevision = -1;
    private async void MousePressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled || _closed || _inputDialogDepth > 0 || IsReaderPanelSource(e.OriginalSource) || InputRouting.Modifiers != 0 || InputRouting.IsControl(e.OriginalSource)) return;
        var mouse = InputRouting.Mouse(e, RootGrid);
        if (mouse is null) return;
        var action = AppServices.Inputs.Match(new InputGesture(Mouse: mouse), InputScope);
        if (action is null) return;
        e.Handled = true;
        await RunReaderInputAsync(action);
    }

    private async void MouseWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled || _closed || _inputDialogDepth > 0 || IsReaderPanelSource(e.OriginalSource) || InputRouting.Modifiers != 0 || InputRouting.IsControl(e.OriginalSource)) { _wheelDelta = 0; return; }
        var properties = e.GetCurrentPoint(RootGrid).Properties;
        if (properties.IsHorizontalMouseWheel) return;
        if (_inputRevision != AppServices.Inputs.Revision) { _wheelDelta = 0; _inputRevision = AppServices.Inputs.Revision; }
        var delta = properties.MouseWheelDelta;
        if (delta == 0) return;
        var action = AppServices.Inputs.Match(new InputGesture(Mouse: delta < 0 ? "WheelDown" : "WheelUp"), InputScope);
        if (action is null) { _wheelDelta = 0; return; }
        e.Handled = true;
        if (_loading && action != "CloseReader") { _wheelDelta = 0; return; }
        if (Math.Sign(_wheelDelta) != Math.Sign(delta)) _wheelDelta = 0;
        _wheelDelta += delta;
        if (Math.Abs(_wheelDelta) < 120) return;
        _wheelDelta = 0;
        await RunReaderInputAsync(action);
    }
    private async Task MovePageAsync(bool next)
    {
        if (_source.Count == 0) return;
        if (next)
        {
            int target = ViewerPagePlanner.NextIndex(_source.Count, _index, _mode, _source.IsPortrait);
            if (target != _index) await ShowSpreadAsync(target);
            return;
        }

        if (_index == 0) return;
        await ShowSpreadAsync(async cancellationToken =>
        {
            if (_index > 0) await _source.EnsureOrientationAsync(_index - 1, cancellationToken);
            if (_index > 1) await _source.EnsureOrientationAsync(_index - 2, cancellationToken);
            return ViewerPagePlanner.PreviousIndex(_source.Count, _index, _mode, _source.IsPortrait);
        });
    }

    private Task ShowSpreadAsync(int targetIndex) =>
        ShowSpreadAsync(_ => Task.FromResult(Math.Clamp(targetIndex, 0, _source.Count - 1)));

    private async Task ShowSpreadAsync(Func<CancellationToken, Task<int>> resolveTargetIndex)
    {
        if (_source.Count == 0 || _closed) return;
        var request = _loadRequests.Begin();
        if (request is null) return;
        var cancellationToken = request.CancellationToken;
        _loading = true;
        try
        {
            int targetIndex = await resolveTargetIndex(cancellationToken);
            var spread = await LoadSpreadAsync(targetIndex, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_loadRequests.IsCurrent(request)) return;
            CommitSpread(spread);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Reader page load failed", ex);
            if (_loadRequests.IsCurrent(request))
            {
                PageText.Text = L10n.Format("ViewerWindow_xaml_008", ex.Message);
                if (_source.IsPdf) await ShowSettingsIssueAsync(L10n.Get("Pdf_PageError"), ex.Message);
            }
        }
        finally
        {
            if (_loadRequests.Complete(request)) _loading = false;
            request.Dispose();
        }
    }

    private sealed record LoadedSpread(int Index, BitmapImage First, BitmapImage? Second);

    private async Task<LoadedSpread> LoadSpreadAsync(int index, CancellationToken cancellationToken)
    {
        await _source.EnsureOrientationAsync(index, cancellationToken);
        if (index + 1 < _source.Count) await _source.EnsureOrientationAsync(index + 1, cancellationToken);
        bool showTwo = ViewerPagePlanner.SpreadLength(_source.Count, index, _mode,
            _source.IsPortrait) == 2;

        var rasterScale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        double width = (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth * rasterScale : 1600) / (showTwo ? 2 : 1);
        double height = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight * rasterScale : 2400;
        var firstBitmap = await LoadAsync(index, width, height, cancellationToken);
        BitmapImage? secondBitmap = showTwo
            ? await LoadAsync(index + 1, width, height, cancellationToken)
            : null;
        return new LoadedSpread(index, firstBitmap, secondBitmap);
    }

    private void CommitSpread(LoadedSpread spread)
    {
        _index = spread.Index;
        if (spread.Second is not null)
        {
            if (_rightBinding)
            {
                RightImage.Source = spread.First;
                LeftImage.Source = spread.Second;
            }
            else
            {
                LeftImage.Source = spread.First;
                RightImage.Source = spread.Second;
            }
            LeftImage.Visibility = RightImage.Visibility = Visibility.Visible;
            PageText.Text = $"{_index + 1}–{_index + 2} / {_source.Count}";
        }
        else
        {
            LeftImage.Source = null;
            RightImage.Source = spread.First;
            LeftImage.Visibility = Visibility.Collapsed;
            RightImage.Visibility = Visibility.Visible;
            PageText.Text = $"{_index + 1} / {_source.Count}";
        }
        LayoutPages();
    }

    private void LayoutPages()
    {
        if (RightImage.Source is not BitmapImage right || right.PixelHeight == 0) return;
        double rightRatio = (double)right.PixelWidth / right.PixelHeight;
        var left = LeftImage.Visibility == Visibility.Visible ? LeftImage.Source as BitmapImage : null;
        double leftRatio = left is { PixelHeight: > 0 } ? (double)left.PixelWidth / left.PixelHeight : 0;
        double height = Math.Min(RootGrid.ActualHeight, RootGrid.ActualWidth / (rightRatio + leftRatio));
        if (height <= 0) return;
        RightImage.Width = height * rightRatio;
        RightImage.Height = height;
        LeftImage.Width = height * leftRatio;
        LeftImage.Height = height;
    }

    private async Task<BitmapImage> LoadAsync(int index, double width, double height, CancellationToken cancellationToken)
    {
        using var stream = await _source.OpenPageAsync(index, width, height, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }
}
