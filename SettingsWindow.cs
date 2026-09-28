using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Lilium;

internal sealed partial class SettingsWindow : Window
{
    private const double DescriptionMaxWidth = 720;
    private readonly Grid _root = new();
    private readonly StackPanel _body = new() { Spacing = 20 };
    private readonly ListView _menu = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = DescriptionMaxWidth, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Slider _limit = new()
    {
        Minimum = 500, Maximum = 5000, StepFrequency = 100,
        SmallChange = 100, LargeChange = 500,
        Width = 400, HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly TextBlock _limitLabel = new();
    private readonly Button _apply = new() { Content = L10n.Get("SettingsWindow_001"), MinWidth = 92, HorizontalAlignment = HorizontalAlignment.Left };
    private bool _busy;
    private bool _closeRequested;
    private ContentDialog? _confirmation;
    private int _savedLimit;
    private readonly List<(Control Control, bool Enabled)> _busyControls = [];

    internal void CloseWithApplication()
    {
        _closeRequested = true;
        if (!_busy) { Close(); return; }
        // Let an active cache operation finish; do not leave a settings-only UI.
        AppWindow.Hide();
        _confirmation?.Hide();
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    internal void ShowForeground()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    internal SettingsWindow()
    {
        Title = L10n.Get("SettingsWindow_002");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Lilium.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 750));
        _root.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        _root.Language = L10n.Language;
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _menu.Margin = new Thickness(12);
        AddHeader(L10n.Get("SettingsWindow_003"));
        AddItem(L10n.Get("SettingsWindow_004"), "language");
        AddHeader(L10n.Get("Keys_Operations"));
        AddItem(L10n.Get("SettingsWindow_006"), "keys");
        AddHeader(L10n.Get("SettingsWindow_007"));
        AddItem(L10n.Get("SettingsWindow_008"), "size");
        AddItem(L10n.Get("SettingsWindow_009"), "clear");
        AddHeader(L10n.Get("SettingsWindow_010"));
        AddItem(L10n.Get("SettingsWindow_011"), "version");
        AddItem(L10n.Get("SettingsWindow_012"), "help");
        _root.Children.Add(_menu);
        var scroll = new ScrollViewer
        {
            Content = _body, Padding = new Thickness(28),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled
        };
        Grid.SetColumn(scroll, 1);
        _root.Children.Add(scroll);
        Content = _root;
        _menu.SelectionChanged += (_, _) => ShowSection();
        _limit.ValueChanged += (_, _) => UpdateLimitLabel();
        _apply.Click += ApplyLimit;
        AppWindow.Closing += async (_, args) =>
        {
            if (_busy)
            {
                args.Cancel = true;
                _status.Text = L10n.Get("SettingsWindow_013");
                return;
            }
            if (!HasPendingKeys) return;
            args.Cancel = true;
            if (await ConfirmDiscardKeysAsync()) Close();
        };
        _menu.SelectedIndex = 1;
    }

    private void AddHeader(string text) => _menu.Items.Add(new ListViewItem
    {
        Content = new TextBlock { Text = text, Opacity = 0.65, Margin = new Thickness(0, 14, 0, 0) },
        IsEnabled = false
    });

    private void AddItem(string text, string tag, bool indent = true) => _menu.Items.Add(new ListViewItem
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Tag = tag,
        Margin = new Thickness(indent ? 16 : 0, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch
    });
    private void UpdateLimitLabel() => _limitLabel.Text = L10n.Format("SettingsWindow_014", _limit.Value);
    private static TextBlock Text(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        MaxWidth = DescriptionMaxWidth, HorizontalAlignment = HorizontalAlignment.Left
    };

    private void ShowSection()
    {
        if (_menu.SelectedItem is not ListViewItem item) return;
        _body.Children.Clear();
        _status.Text = "";
        _body.Children.Add(new TextBlock
        {
            Text = (item.Content as TextBlock)?.Text, FontSize = 24,
            TextWrapping = TextWrapping.Wrap, MaxWidth = DescriptionMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Left
        });
        switch (item.Tag)
        {
            case "keys":
                BuildKeysSection();
                break;
            case "language":
                BuildLanguageSection();
                break;
            case "size":
                try
                {
                    _savedLimit = AppServices.CacheLimitMb;
                    _limit.Value = _savedLimit;
                    _limit.IsEnabled = true;
                    _apply.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    _limit.IsEnabled = false;
                    _apply.IsEnabled = false;
                    Report(L10n.Get("SettingsWindow_016"), ex);
                }
                UpdateLimitLabel();
                _body.Children.Add(_limitLabel);
                _body.Children.Add(_limit);
                _body.Children.Add(_apply);
                _body.Children.Add(Text(L10n.Get("SettingsWindow_017")));
                if (AppServices.ThumbnailCache.LastError is { } issue && _status.Text.Length == 0)
                    _status.Text = L10n.Get("SettingsWindow_018") + issue;
                break;
            case "clear":
                _body.Children.Add(Text(L10n.Get("SettingsWindow_019")));
                var clear = new Button { Content = L10n.Get("SettingsWindow_020") };
                clear.Click += ClearClicked;
                _body.Children.Add(clear);
                break;
            case "version":
                _body.Children.Add(Text(L10n.Get("SettingsWindow_021") +
                    (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? L10n.Get("SettingsWindow_022"))));
                break;
            case "help":
                _body.Children.Add(Text(L10n.Get("SettingsWindow_023")));
                var help = new Button { Content = L10n.Get("SettingsWindow_024") };
                help.Click += (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe")
                        {
                            ArgumentList = { Path.Combine(AppContext.BaseDirectory, L10n.HelpFileName) }, UseShellExecute = true
                        });
                    }
                    catch (Exception ex) { Report(L10n.Get("SettingsWindow_025"), ex); }
                };
                _body.Children.Add(help);
                break;
        }
        if (item.Tag is not "keys") _body.Children.Add(_status);
    }

    private void BuildLanguageSection()
    {
        var choice = new ComboBox { Width = 400, HorizontalAlignment = HorizontalAlignment.Left };
        // Language names always use their native spelling, even after switching languages.
        choice.Items.Add(new ComboBoxItem { Content = "日本語", Tag = "ja" });
        choice.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        string saved;
        try
        {
            saved = AppServices.Store.Get(L10n.SettingKey, L10n.Language);
            choice.SelectedIndex = saved == "en" ? 1 : saved == "ja" ? 0 : -1;
            if (!L10n.IsSupported(saved)) _status.Text = L10n.Get("Language_Invalid");
            else if (saved != L10n.Language) _status.Text = L10n.Get("Language_Restart");
        }
        catch (Exception ex)
        {
            choice.IsEnabled = false;
            saved = L10n.Language;
            Report(L10n.Get("SettingsWindow_016"), ex);
        }
        bool restoring = false;
        choice.SelectionChanged += (_, _) =>
        {
            if (restoring || choice.SelectedItem is not ComboBoxItem { Tag: string selected }) return;
            try
            {
                AppServices.Store.Set(L10n.SettingKey, selected);
                saved = selected;
                _status.Text = selected == L10n.Language ? L10n.Get("Language_Current") : L10n.Get("Language_Restart");
            }
            catch (Exception ex)
            {
                restoring = true;
                choice.SelectedIndex = saved == "en" ? 1 : saved == "ja" ? 0 : -1;
                restoring = false;
                Report(L10n.Get("SettingsWindow_026"), ex);
            }
        };
        _body.Children.Add(choice);
        _body.Children.Add(Text(L10n.Get("Language_Description")));
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _menu.IsEnabled = !busy;
        if (busy)
        {
            _busyControls.Clear();
            foreach (var control in _body.Children.OfType<Control>())
            { _busyControls.Add((control, control.IsEnabled)); control.IsEnabled = false; }
        }
        else
        {
            foreach (var (control, enabled) in _busyControls) control.IsEnabled = enabled;
            _busyControls.Clear();
        }
        if (!busy && _closeRequested) Close();
    }

    private void Report(string message, Exception ex)
    {
        App.WriteDiagnosticLog(message, ex);
        _status.Text = message + "\n" + ex.Message;
    }

    private async void ApplyLimit(object sender, RoutedEventArgs args)
    {
        if (_busy || _closeRequested) return;
        int requested = (int)_limit.Value;
        SetBusy(true);
        try
        {
            try { AppServices.Store.Set("cache_limit_mb", requested.ToString()); }
            catch (Exception ex)
            {
                _limit.Value = _savedLimit;
                Report(L10n.Get("SettingsWindow_026"), ex);
                return;
            }
            _savedLimit = requested;
            await AppServices.ThumbnailCache.MaintainAsync(force: true);
            _status.Text = AppServices.ThumbnailCache.LastError is { } issue
                ? L10n.Get("SettingsWindow_027") + issue : L10n.Get("SettingsWindow_028");
        }
        catch (Exception ex) { Report(L10n.Get("SettingsWindow_029"), ex); }
        finally { SetBusy(false); }
    }

    private async void ClearClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _closeRequested) return;
        SetBusy(true);
        try
        {
            var confirm = new ContentDialog
            {
                Title = L10n.Get("SettingsWindow_030"),
                Content = L10n.Get("SettingsWindow_031"),
                PrimaryButtonText = L10n.Get("SettingsWindow_032"), CloseButtonText = L10n.Get("SettingsWindow_033"),
                DefaultButton = ContentDialogButton.Close, XamlRoot = _root.XamlRoot
            };
            _confirmation = confirm;
            var decision = await confirm.ShowAsync();
            _confirmation = null;
            if (_closeRequested || decision != ContentDialogResult.Primary) return;
            _status.Text = L10n.Get("SettingsWindow_034");
            var result = await FileCatalog.ClearThumbnailCacheAsync();
            _status.Text = L10n.Format("SettingsWindow_035", result.Deleted) +
                (result.Failed > 0 ? L10n.Format("SettingsWindow_036", result.Failed) : "");
        }
        catch (Exception ex) { Report(L10n.Get("SettingsWindow_037"), ex); }
        finally { _confirmation = null; SetBusy(false); }
    }
}
