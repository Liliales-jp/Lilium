using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Lilium;

internal static class InputRouting
{
    internal static KeyModifiers Modifiers
    {
        get
        {
            bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            return (Down(VirtualKey.Control) ? KeyModifiers.Control : 0) |
                (Down(VirtualKey.Shift) ? KeyModifiers.Shift : 0) |
                (Down(VirtualKey.Menu) ? KeyModifiers.Alt : 0) |
                (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? KeyModifiers.Windows : 0);
        }
    }
    internal static bool IsControl(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is TextBox or PasswordBox or AutoSuggestBox or ComboBox or Slider or ButtonBase or ScrollBar or MenuFlyoutPresenter) return true;
        return false;
    }
    internal static InputGesture Key(KeyRoutedEventArgs e)
    {
        int key = (int)e.Key;
        // NumLock-off numpad keys are navigation keys; numpad Enter shares Enter.
        return new(key, Modifiers);
    }
    internal static string? Mouse(PointerRoutedEventArgs e, UIElement root) => e.GetCurrentPoint(root).Properties.PointerUpdateKind switch
    {
        PointerUpdateKind.LeftButtonPressed => "Left", PointerUpdateKind.RightButtonPressed => "Right",
        PointerUpdateKind.MiddleButtonPressed => "Middle", PointerUpdateKind.XButton1Pressed => "Back",
        PointerUpdateKind.XButton2Pressed => "Forward", _ => null
    };
    internal static string Label(InputGesture g)
    {
        if (g.Mouse is { } mouse) return L10n.Get("Keys_Mouse_" + mouse);
        string key = g.Key switch
        {
            >= 65 and <= 90 or >= 48 and <= 57 => ((char)g.Key).ToString(),
            >= 96 and <= 105 => "Num " + (g.Key - 96), >= 112 and <= 123 => "F" + (g.Key - 111),
            8 => "Backspace", 13 => "Enter", 27 => "Esc", 32 => "Space", 33 => "PageUp", 34 => "PageDown",
            35 => "End", 36 => "Home", 37 => "←", 38 => "↑", 39 => "→", 40 => "↓", 45 => "Insert", 46 => "Delete",
            106 => "Num *", 107 => "Num +", 109 => "Num -", 110 => "Num .", 111 => "Num /", _ => "?"
        };
        return (g.Modifiers.HasFlag(KeyModifiers.Control) ? "Ctrl + " : "") +
            (g.Modifiers.HasFlag(KeyModifiers.Shift) ? "Shift + " : "") +
            (g.Modifiers.HasFlag(KeyModifiers.Alt) ? "Alt + " : "") + key;
    }
}
