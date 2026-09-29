using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Foundation;

namespace Lilium;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _dragHintTimer;
    private string? _dragHintDestination;
    private bool _dragHintSameDrive;
    private bool _dragHintUsesMouse;
    private DataPackageOperation _dragHintAllowed;
    private Point _dragHintPointer;
    private bool _dragHintTickTraced;

    [StructLayout(LayoutKind.Sequential)]
    private struct DragScreenPoint { public int X; public int Y; }
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out DragScreenPoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref DragScreenPoint point);

    // Read physical state, not the last XAML keyboard/drag message. Those messages
    // can stop arriving while the pointer is stationary, especially over TreeView.
    private static bool DragKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private static (bool Control, bool Shift) ReadDragModifiers() => (DragKeyDown(0x11), DragKeyDown(0x10));

    private DataPackageOperation BeginDragHint(DragEventArgs e, IReadOnlyList<string> paths, string destination)
    {
        _dragHintDestination = destination;
        _dragHintSameDrive = FileDropPolicy.ShouldMove(paths, destination, false, false, false);
        _dragHintAllowed = e.AllowedOperations;
        _dragHintPointer = e.GetPosition(RootGrid);
        _dragHintUsesMouse = (e.Modifiers & (DragDropModifiers.LeftButton | DragDropModifiers.RightButton | DragDropModifiers.MiddleButton)) != 0;
        var operation = RefreshDragHint();
        if (_dragHintTimer is null)
        {
            _dragHintTimer = DispatcherQueue.CreateTimer();
            _dragHintTimer.Interval = TimeSpan.FromMilliseconds(30);
            _dragHintTimer.IsRepeating = true;
            _dragHintTimer.Tick += DragHint_Tick;
        }
        if (!_dragHintTimer.IsRunning)
        {
            _dragHintTickTraced = false;
            _dragHintTimer.Start();
        }
        return operation;
    }

    private void DragHint_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!sender.IsRunning || _dragHintDestination is null) return;
        try
        {
            if (_closed || _fileOperationBusy || DragKeyDown(0x1B) ||
                (_dragHintUsesMouse && !DragKeyDown(0x01) && !DragKeyDown(0x02) && !DragKeyDown(0x04)))
            {
                ++_dragFeedbackVersion;
                _dragSourcePathsTask = null;
                HideDragHint();
                return;
            }
            if (!_dragHintTickTraced)
            {
                _dragHintTickTraced = true;
                TraceDrag("Hint timer active");
            }
            if (_dragHintUsesMouse && GetCursorPos(out var screenPoint) &&
                ScreenToClient(WinRT.Interop.WindowNative.GetWindowHandle(this), ref screenPoint))
            {
                double scale = RootGrid.XamlRoot.RasterizationScale;
                _dragHintPointer = new Point(screenPoint.X / scale, screenPoint.Y / scale);
                if (_dragHintPointer.X < 0 || _dragHintPointer.Y < 0 ||
                    _dragHintPointer.X >= RootGrid.ActualWidth || _dragHintPointer.Y >= RootGrid.ActualHeight)
                {
                    ++_dragFeedbackVersion;
                    _dragSourcePathsTask = null;
                    HideDragHint();
                    return;
                }
            }
            RefreshDragHint();
        }
        catch (Exception ex)
        {
            ++_dragFeedbackVersion;
            _dragSourcePathsTask = null;
            HideDragHint();
            App.WriteDiagnosticLog("Drag hint update failed", ex);
        }
    }

    private DataPackageOperation RefreshDragHint()
    {
        if (_dragHintDestination is not { } destination) return DataPackageOperation.None;
        var modifiers = ReadDragModifiers();
        var operation = (FileDropPolicy.ShouldMove(_dragHintSameDrive, modifiers.Control, modifiers.Shift, AppServices.DragAlwaysMove)
            ? DataPackageOperation.Move : DataPackageOperation.Copy) & _dragHintAllowed;
        if (operation == DataPackageOperation.None)
        {
            DragOperationHint.Visibility = Visibility.Collapsed;
            return operation;
        }
        string name = Path.GetFileName(destination.TrimEnd('\\'));
        if (name.Length == 0 || name.EndsWith(':')) name = destination;
        string caption = L10n.Format("MainWindow_Explorer_007", name,
            operation == DataPackageOperation.Copy ? L10n.Get("MainWindow_Explorer_005") : L10n.Get("MainWindow_Explorer_006"));
        if (DragOperationHintText.Text != caption)
        {
            DragOperationHintText.Text = caption;
            TraceDrag($"Hint operation={operation} control={modifiers.Control} shift={modifiers.Shift}");
        }
        PositionDragHint(_dragHintPointer, caption);
        DragOperationHint.Visibility = Visibility.Visible;
        return operation;
    }

    private void PositionDragHint(Point pointer, string caption)
    {
        double width = DragOperationHint.ActualWidth > 0 ? DragOperationHint.ActualWidth : Math.Min(320, 20 + caption.Length * 12);
        double height = DragOperationHint.ActualHeight > 0 ? DragOperationHint.ActualHeight : 34;
        double left = pointer.X + 18;
        double top = pointer.Y - height - 14;
        if (left + width + 8 > RootGrid.ActualWidth) left = pointer.X - width - 18;
        if (top < 8) top = pointer.Y + 22;
        Canvas.SetLeft(DragOperationHint, Math.Clamp(left, 8, Math.Max(8, RootGrid.ActualWidth - width - 8)));
        Canvas.SetTop(DragOperationHint, Math.Clamp(top, 8, Math.Max(8, RootGrid.ActualHeight - height - 8)));
    }

    private void HideDragHint()
    {
        _dragHintTimer?.Stop();
        _dragHintDestination = null;
        if (!_closed) DragOperationHint.Visibility = Visibility.Collapsed;
    }
}
