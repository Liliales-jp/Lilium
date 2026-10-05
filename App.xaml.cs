using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lilium;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    private Window? _startupFailureWindow;
    private SettingsWindow? _settingsWindow;
    private LanguageSelectionWindow? _languageSelectionWindow;
    private readonly HashSet<MainWindow> _libraryWindows = [];

    internal void RegisterLibraryWindow(MainWindow window)
    {
        _libraryWindows.Add(window);
        bool confirmingSettingsClose = false;
        window.AppWindow.Closing += async (_, args) =>
        {
            if (args.Cancel) return;
            if (confirmingSettingsClose) { args.Cancel = true; return; }
            if (_libraryWindows.Count != 1 || _settingsWindow is not { HasPendingKeys: true } settings) return;
            args.Cancel = true;
            confirmingSettingsClose = true;
            try
            {
                settings.ShowForeground();
                if (await settings.ConfirmDiscardKeysAsync())
                {
                    confirmingSettingsClose = false;
                    window.Close();
                }
            }
            finally { confirmingSettingsClose = false; }
        };
        window.Closed += (_, _) =>
        {
            _libraryWindows.Remove(window);
            if (ReferenceEquals(MainWindow, window)) MainWindow = _libraryWindows.FirstOrDefault();
            if (_libraryWindows.Count == 0) _settingsWindow?.CloseWithApplication();
        };
    }

    internal void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            var window = new SettingsWindow();
            _settingsWindow = window;
            window.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.ShowForeground();
    }

    public App()
    {
        UnhandledException += (_, e) =>
        {
            WriteDiagnosticLog("Unhandled exception", e.Exception ?? new Exception(e.Message));
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AppServices.Initialize();
            var language = AppServices.Store.Get(L10n.SettingKey, "");
            if (string.IsNullOrEmpty(language))
            {
                _languageSelectionWindow = new LanguageSelectionWindow(OpenLibrary);
                _languageSelectionWindow.Closed += (_, _) => _languageSelectionWindow = null;
                _languageSelectionWindow.Activate();
            }
            else OpenLibrary(language);
        }
        catch (Exception ex)
        {
            WriteDiagnosticLog("Startup failed", ex);
            _startupFailureWindow = CreateStartupFailureWindow(ex);
            _startupFailureWindow.Closed += (_, _) => _startupFailureWindow = null;
            _startupFailureWindow.Activate();
        }
    }

    private void OpenLibrary(string language)
    {
        L10n.Initialize(language);
        // Also request the selected language for Windows App SDK control resources.
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = L10n.Language;
        // App.xaml keeps framework resources in MergedDictionaries. The root
        // dictionary must have no Source so it can accept these local values.
        foreach (var (key, text) in L10n.UiResources) Resources[key] = text;
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    internal static void WriteDiagnosticLog(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppServices.RootPath);
            File.AppendAllText(Path.Combine(AppServices.RootPath, "crash.log"),
                $"{DateTimeOffset.Now:O}\n{context}\n{exception.Message}\n{exception}\n");
        }
        catch { /* Do not replace the original failure when logging is unavailable. */ }
    }

    private static Window CreateStartupFailureWindow(Exception exception)
    {
        var window = new Window { Title = L10n.Get("App_xaml_001") };
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = L10n.Get("App_xaml_002"),
            FontSize = 26
        });
        panel.Children.Add(new TextBlock
        {
            Text = L10n.Get("App_xaml_003") +
                   L10n.Format("App_xaml_004", AppServices.RootPath, exception.Message),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        window.Content = new ScrollViewer { Content = panel };
        WindowTitleBarTheme.Attach(window, (FrameworkElement)window.Content);
        WindowDpi.ResizeInDips(window, 680, 360);
        return window;
    }
}
