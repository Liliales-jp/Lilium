using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Lilium;

internal sealed partial class SettingsWindow
{
    private const double KeyInputWidth = 340;
    private const double KeyActionButtonWidth = 44;
    private const double KeyBindingsWidth = KeyInputWidth + KeyActionButtonWidth * 2 + 12;

    private Dictionary<string, List<InputGesture>>? _keyDraft;
    private string? _keyBaseline;
    private readonly StackPanel _keyRows = new() { Spacing = 16, MinWidth = 550 };
    private Button _keyApply = new();
    private Button _keyApplyBottom = new();
    internal bool HasPendingKeys => _keyDraft is not null && BindingRules.Encode(_keyDraft) != _keyBaseline;

    private void BuildKeysSection()
    {
        if (_keyDraft is null)
        {
            _keyDraft = AppServices.Inputs.Snapshot();
            _keyBaseline = BindingRules.Encode(_keyDraft);
        }
        _keyApply = new Button { Content = L10n.Get("SettingsWindow_001") };
        _keyApply.Click += (_, _) => ApplyKeys();
        _body.Children.Add(Text(L10n.Get("Keys_Description")));
        if (AppServices.Inputs.LoadError is not null) _body.Children.Add(Text(L10n.Get("Keys_LoadError")));
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        controls.Children.Add(_keyApply);
        var reset = new Button { Content = L10n.Get("Keys_Reset") };
        reset.Click += (_, _) => { _keyDraft = BindingRules.Defaults(); RenderKeyRows(); };
        controls.Children.Add(reset);
        _body.Children.Add(controls);
        _body.Children.Add(_status);
        _body.Children.Add(_keyRows);
        _keyApplyBottom = new Button { Content = L10n.Get("SettingsWindow_001") };
        _keyApplyBottom.Click += (_, _) => ApplyKeys();
        _body.Children.Add(_keyApplyBottom);
        RenderKeyRows();
    }

    private void RenderKeyRows()
    {
        if (_keyDraft is null) return;
        _keyRows.Children.Clear();
        var errors = BindingRules.Validate(_keyDraft);
        _keyApply.IsEnabled = errors.Count == 0 && (HasPendingKeys || AppServices.Inputs.LoadError is not null);
        _keyApplyBottom.IsEnabled = _keyApply.IsEnabled;
        _status.Text = errors.Count > 0 ? L10n.Get("Keys_FixErrors") : HasPendingKeys ? L10n.Get("Keys_Pending") : "";
        foreach (var action in BindingRules.Actions)
        {
            if (action.Id is "Open" or "CloseReader")
                _keyRows.Children.Add(new TextBlock { Text = L10n.Get(action.Id == "Open" ? "Keys_Library" : "Keys_Reader"), FontSize = 20 });
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 12, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyBindingsWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.Children.Add(Text(L10n.Get("Keys_Action_" + action.Id)));
            var values = new StackPanel { Spacing = 8 };
            Grid.SetColumn(values, 1); row.Children.Add(values);
            var bindings = _keyDraft[action.Id];
            if (bindings.Count == 0) values.Children.Add(Text(L10n.Get("Keys_Unassigned")));
            for (int index = 0; index < bindings.Count; index++)
            {
                int capturedIndex = index;
                var binding = bindings[index];
                var line = new Grid { ColumnSpacing = 6 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyInputWidth) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyActionButtonWidth) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyActionButtonWidth) });
                if (binding.Mouse is not null)
                {
                    var combo = new ComboBox { Width = KeyInputWidth, PlaceholderText = L10n.Get("Keys_ChooseMouse") };
                    foreach (var mouse in BindingRules.MiceFor(action.Id)) combo.Items.Add(new ComboBoxItem { Content = L10n.Get("Keys_Mouse_" + mouse), Tag = mouse });
                    combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == binding.Mouse);
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is not ComboBoxItem { Tag: string mouse }) return;
                        bindings[capturedIndex] = new InputGesture(Mouse: mouse); RenderKeyRows();
                    };
                    line.Children.Add(combo);
                }
                else
                {
                    var label = new TextBox { Width = KeyInputWidth, Text = InputRouting.Label(binding), IsReadOnly = true, IsTabStop = false };
                    line.Children.Add(label);
                    var edit = new Button { Content = SmallActionIcon(Symbol.Edit) };
                    ToolTipService.SetToolTip(edit, L10n.Get("Keys_Edit"));
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edit, L10n.Get("Keys_Edit"));
                    edit.Click += async (_, _) => await CaptureKeyAsync(action.Id, capturedIndex);
                    Grid.SetColumn(edit, 1); line.Children.Add(edit);
                }
                var remove = new Button { Content = SmallActionIcon(Symbol.Delete) };
                ToolTipService.SetToolTip(remove, L10n.Get("Keys_Remove"));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, L10n.Get("Keys_Remove"));
                remove.Click += (_, _) => { bindings.RemoveAt(capturedIndex); RenderKeyRows(); };
                Grid.SetColumn(remove, 2); line.Children.Add(remove);
                values.Children.Add(line);
                foreach (var error in errors.Where(e => e.Action == action.Id && e.Index == capturedIndex))
                    values.Children.Add(Text(error.Reason == "Conflict"
                        ? L10n.Format("Keys_Conflict", L10n.Get("Keys_Action_" + error.Other))
                        : L10n.Get("Keys_" + error.Reason)));
            }
            var buttons = new StackPanel { Spacing = 8 };
            var addKey = new Button { Content = L10n.Get("Keys_AddKey"), HorizontalAlignment = HorizontalAlignment.Stretch };
            addKey.Click += async (_, _) => await CaptureKeyAsync(action.Id, null);
            var addMouse = new Button { Content = L10n.Get("Keys_AddMouse"), HorizontalAlignment = HorizontalAlignment.Stretch };
            addMouse.Click += (_, _) => { bindings.Add(new InputGesture(Mouse: "")); RenderKeyRows(); };
            buttons.Children.Add(addKey); buttons.Children.Add(addMouse);
            Grid.SetColumn(buttons, 2); row.Children.Add(buttons);
            _keyRows.Children.Add(row);
            _keyRows.Children.Add(new Border { Height = 1, Opacity = 0.3, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray) });
        }
    }

    private static Grid SmallActionIcon(Symbol symbol)
    {
        // Keep the original 20 px content footprint so only the glyph shrinks.
        var holder = new Grid { Width = 20, Height = 20 };
        holder.Children.Add(new FontIcon
        {
            Glyph = char.ConvertFromUtf32((int)symbol), FontSize = 16,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        return holder;
    }

    private async Task CaptureKeyAsync(string action, int? index)
    {
        if (_busy || _closeRequested) return;
        SetBusy(true);
        InputGesture? captured = null;
        bool keyboardInput = false;
        var prompt = Text(L10n.Get("Keys_Press"));
        var dialog = new ContentDialog
        {
            Title = L10n.Get("Keys_Action_" + action), Content = prompt, XamlRoot = _root.XamlRoot,
            PrimaryButtonText = L10n.Get("Keys_Register"), CloseButtonText = L10n.Get("SettingsWindow_033"),
            IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.None
        };
        void Capture(object sender, KeyRoutedEventArgs e)
        {
            keyboardInput = true;
            e.Handled = true;
            var gesture = InputRouting.Key(e);
            if (gesture.Key is 16 or 17 or 18 or 91 or 92 or >= 160 and <= 165) return;
            var reason = BindingRules.InvalidReason(action, gesture);
            captured = reason is null ? gesture : null;
            prompt.Text = reason is null ? InputRouting.Label(gesture) : L10n.Get("Keys_" + reason);
            dialog.IsPrimaryButtonEnabled = captured is not null;
        }
        dialog.PreviewKeyDown += Capture;
        // Esc/Enter are data here, never the dialog's cancel/submit shortcuts.
        // Pointer interaction re-enables the explicit on-screen buttons.
        PointerEventHandler pointer = (_, _) => keyboardInput = false;
        dialog.AddHandler(UIElement.PointerPressedEvent, pointer, true);
        void Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
        { if (keyboardInput && !_closeRequested) args.Cancel = true; }
        dialog.Closing += Closing;
        Exception? failure = null;
        try
        {
            _confirmation = dialog;
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && captured is not null && !_closeRequested)
            {
                if (index is { } slot) _keyDraft![action][slot] = captured;
                else _keyDraft![action].Add(captured);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            dialog.PreviewKeyDown -= Capture;
            dialog.RemoveHandler(UIElement.PointerPressedEvent, pointer);
            dialog.Closing -= Closing;
            _confirmation = null;
            SetBusy(false);
            if (!_closeRequested) RenderKeyRows();
            if (failure is not null && !_closeRequested) Report(L10n.Get("Keys_CaptureFailed"), failure);
        }
    }

    private void ApplyKeys()
    {
        if (_busy || _keyDraft is null || BindingRules.Validate(_keyDraft).Count != 0) return;
        try
        {
            AppServices.Inputs.Apply(_keyDraft);
            _keyBaseline = BindingRules.Encode(_keyDraft);
            ShowSection();
            _status.Text = L10n.Get("Keys_Applied");
        }
        catch (Exception ex) { Report(L10n.Get("Keys_SaveFailed"), ex); }
    }

    internal async Task<bool> ConfirmDiscardKeysAsync()
    {
        if (_busy) return false;
        if (!HasPendingKeys) return true;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                Title = L10n.Get("Keys_DiscardTitle"), Content = L10n.Get("Keys_DiscardText"), XamlRoot = _root.XamlRoot,
                PrimaryButtonText = L10n.Get("Keys_Discard"), CloseButtonText = L10n.Get("SettingsWindow_033"),
                DefaultButton = ContentDialogButton.Close
            };
            _confirmation = dialog;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
            _keyDraft = null; _keyBaseline = null;
            return true;
        }
        catch (Exception ex) { Report(L10n.Get("Keys_CaptureFailed"), ex); return false; }
        finally { _confirmation = null; SetBusy(false); }
    }
}
