using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Lilium;

internal static class WindowTitleBarTheme
{
    internal static void Attach(Window window, FrameworkElement root)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        void Apply()
        {
            var theme = root.ActualTheme;
            bool dark = theme == ElementTheme.Dark ||
                (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
            var background = Gray(dark ? (byte)0x20 : (byte)0xF3);
            var foreground = Gray(dark ? (byte)0xEE : (byte)0x1A);
            var inactiveForeground = Gray(dark ? (byte)0x99 : (byte)0x66);
            var titleBar = window.AppWindow.TitleBar;

            // Set every color so the system accent cannot leave mismatched buttons.
            // Windows still handles activation, dragging, and the close button states.
            titleBar.BackgroundColor = background;
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveBackgroundColor = background;
            titleBar.InactiveForegroundColor = inactiveForeground;
            titleBar.ButtonBackgroundColor = background;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveBackgroundColor = background;
            titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            titleBar.ButtonHoverBackgroundColor = Gray(dark ? (byte)0x2D : (byte)0xE5);
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = Gray(dark ? (byte)0x33 : (byte)0xD9);
            titleBar.ButtonPressedForegroundColor = foreground;
        }

        void OnLoaded(object sender, RoutedEventArgs args) => Apply();
        void OnThemeChanged(FrameworkElement sender, object args) => Apply();
        void OnClosed(object sender, WindowEventArgs args)
        {
            root.Loaded -= OnLoaded;
            root.ActualThemeChanged -= OnThemeChanged;
            window.Closed -= OnClosed;
        }

        root.Loaded += OnLoaded;
        root.ActualThemeChanged += OnThemeChanged;
        window.Closed += OnClosed;
        Apply();
    }

    private static Color Gray(byte value) => Color.FromArgb(255, value, value, value);
}
