using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Lilium;

public sealed partial class ViewerWindow : Window
{
    private readonly ReaderView _reader = new(false);
    private readonly ReaderDocument _document;
    private readonly int _initialIndex;
    private readonly string _binding;
    private readonly string _mode;
    private readonly Window? _owner;
    private string? _settingsIssue;
    private bool _started;
    private bool _closed;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _activationTimer;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    public ViewerWindow(List<LibraryItem> images, int index, string binding, Window? libraryOwner = null)
        : this(new ReaderDocument(images.Select(i => i.Path).ToArray()), index, binding, libraryOwner) { }

    public ViewerWindow(string pdfPath, string binding, Window? libraryOwner = null)
        : this(new ReaderDocument([], pdfPath), 0, binding, libraryOwner) { }

    internal ViewerWindow(ReaderDocument document, int index, string binding, Window? libraryOwner)
    {
        var owner = libraryOwner ?? App.MainWindow;
        _owner = owner;
        _document = document;
        _initialIndex = index;
        _binding = binding is "right" or "left" ? binding : "right";
        if (_binding != binding) _settingsIssue = L10n.Get("ViewerWindow_xaml_001");
        _mode = "auto";
        try
        {
            var savedMode = AppServices.Store.Get("viewer_mode", "auto");
            if (savedMode is "auto" or "one" or "two") _mode = savedMode;
            else _settingsIssue = L10n.Get("ViewerWindow_xaml_002");
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Viewer setting read failed", ex);
            _settingsIssue = L10n.Get("ViewerWindow_xaml_003") + ex.Message;
        }
        InitializeComponent();
        RootGrid.Children.Add(_reader);
        _reader.CloseRequested = Close;
        _reader.BindingChanged = value => (owner as MainWindow)?.ApplyBindingFromReader(value);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Lilium.ico"));
        if (owner is not null)
        {
            SetWindowLongPtr(WinRT.Interop.WindowNative.GetWindowHandle(this), -8,
                WinRT.Interop.WindowNative.GetWindowHandle(owner));
            owner.Closed += OwnerClosed;
        }
        Closed += (_, _) =>
        {
            _closed = true;
            _reader.Dispose();
            _activationTimer?.Stop();
            if (_owner is not null) _owner.Closed -= OwnerClosed;
        };
        var display = DisplayArea.GetFromWindowId(owner?.AppWindow.Id ?? AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is not null) AppWindow.MoveAndResize(display.WorkArea);
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    private void OwnerClosed(object sender, WindowEventArgs args)
    {
        if (!_closed) Close();
    }

    public void ShowForeground()
    {
        if (_closed) return;
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        // Reassert activation once after the originating double-click has finished.
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
        _reader.Focus(FocusState.Programmatic);
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started || _closed) return;
        _started = true;
        ShowForeground();
        _reader.Focus(FocusState.Programmatic);
        if (_settingsIssue is not null)
            await _reader.ReportIssueAsync(L10n.Get("ViewerWindow_xaml_004"), _settingsIssue);
        if (!_closed) await _reader.OpenAsync(_document, _initialIndex, _binding, _mode);
    }
}
