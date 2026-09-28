using Microsoft.UI.Windowing;
using System.Text.Json;
using Windows.Graphics;

namespace Lilium;

public sealed partial class MainWindow
{
    private sealed record Placement(int X, int Y, int Width, int Height, bool Maximized);
    private RectInt32 _normalBounds;
    private bool _saveWindowPlacement = true;

    private void InitializeWindowPlacement()
    {
        Placement? saved = null;
        try { saved = JsonSerializer.Deserialize<Placement>(AppServices.Store.Get("window_placement", "null")); }
        catch (JsonException ex)
        {
            _saveWindowPlacement = false;
            App.WriteDiagnosticLog("Invalid window placement setting", ex);
            ShowInvalidSetting("window_placement");
        }
        catch (Exception ex)
        {
            _saveWindowPlacement = false;
            App.WriteDiagnosticLog("Window placement setting read failed", ex);
            ShowSettingsReadFailure(ex);
        }
        if (saved is not null && (saved.Width <= 0 || saved.Height <= 0))
        {
            _saveWindowPlacement = false;
            ShowInvalidSetting("window_placement");
            saved = null;
        }
        if (saved is not null && saved.Width > 0 && saved.Height > 0)
        {
            var requested = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
            var area = DisplayArea.GetFromRect(requested, DisplayAreaFallback.Nearest).WorkArea;
            var bounds = FitToWorkArea(requested, area);
            AppWindow.MoveAndResize(bounds);
            _normalBounds = bounds;
            if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        }
        else _normalBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

        AppWindow.Changed += (_, args) =>
        {
            if ((args.DidPositionChange || args.DidSizeChange) &&
                AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
                _normalBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        };
        AppWindow.Closing += (_, _) =>
        {
            var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            var p = new Placement(_normalBounds.X, _normalBounds.Y, _normalBounds.Width, _normalBounds.Height, maximized);
            if (_saveWindowPlacement) TrySaveSetting("window_placement", JsonSerializer.Serialize(p));
        };
    }

    // WorkArea excludes the taskbar; fully contain the restored window, including its title bar.
    private static RectInt32 FitToWorkArea(RectInt32 bounds, RectInt32 area)
    {
        int width = Math.Clamp(bounds.Width, 1, Math.Max(1, area.Width));
        int height = Math.Clamp(bounds.Height, 1, Math.Max(1, area.Height));
        return new RectInt32(
            (int)Math.Clamp((long)bounds.X, area.X, (long)area.X + area.Width - width),
            (int)Math.Clamp((long)bounds.Y, area.Y, (long)area.Y + area.Height - height),
            width, height);
    }
}
