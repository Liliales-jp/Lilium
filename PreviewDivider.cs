using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lilium;

public sealed class PreviewDivider : UserControl
{
    public PreviewDivider()
    {
        IsTabStop = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Content = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children = { new Border
            {
                Width = 1, HorizontalAlignment = HorizontalAlignment.Center,
                Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]
            } }
        };
    }
}
