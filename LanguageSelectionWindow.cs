using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lilium;

internal sealed class LanguageSelectionWindow : Window
{
    internal LanguageSelectionWindow(Action<string> start)
    {
        Title = "Lilium — Language / 言語";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Lilium.ico"));
        WindowDpi.ResizeInDips(this, 600, 420);
        var panel = new StackPanel { Padding = new Thickness(28), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = L10n.Get("Language_Welcome"), FontSize = 24, TextWrapping = TextWrapping.Wrap });
        var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        choice.Items.Add(new ComboBoxItem { Content = "日本語", Tag = "ja" });
        choice.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        choice.SelectedIndex = L10n.DefaultLanguage == "ja" ? 0 : 1;
        panel.Children.Add(choice);
        var next = new Button { Content = L10n.Get("Language_Start") };
        panel.Children.Add(next);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);
        next.Click += (_, _) =>
        {
            if (choice.SelectedItem is not ComboBoxItem { Tag: string language }) return;
            next.IsEnabled = false;
            try
            {
                AppServices.Store.Set(L10n.SettingKey, language);
                start(language); // Keep this window alive until the library is open.
                Close();
            }
            catch (Exception ex)
            {
                App.WriteDiagnosticLog("Language selection startup failed", ex);
                status.Text = L10n.Get("Language_StartFailed") + "\n" + ex.Message;
                next.IsEnabled = true;
            }
        };
        Content = new ScrollViewer { Content = panel };
        WindowTitleBarTheme.Attach(this, (FrameworkElement)Content);
    }
}
