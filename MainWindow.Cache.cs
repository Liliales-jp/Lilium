using Microsoft.UI.Xaml;

namespace Lilium;

public sealed partial class MainWindow
{
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        try { ((App)Application.Current).ShowSettings(); }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Settings window open failed", ex);
            if (!_closed) await ShowMessageAsync(L10n.Get("MainWindow_Cache_001"), ex.Message);
        }
    }
}
