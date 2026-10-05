using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Lilium;

internal static class WindowDpi
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    // XAML sizes are DIPs, while AppWindow bounds are physical pixels.
    internal static void ResizeInDips(Window window, double width, double height)
    {
        uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var appWindow = window.AppWindow;
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int pixelWidth = Math.Clamp((int)Math.Round(width * scale), 1, Math.Max(1, area.Width));
        int pixelHeight = Math.Clamp((int)Math.Round(height * scale), 1, Math.Max(1, area.Height));
        appWindow.MoveAndResize(new RectInt32(
            (int)Math.Clamp((long)appWindow.Position.X, area.X, (long)area.X + area.Width - pixelWidth),
            (int)Math.Clamp((long)appWindow.Position.Y, area.Y, (long)area.Y + area.Height - pixelHeight),
            pixelWidth, pixelHeight));
    }
}
