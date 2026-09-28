using Microsoft.UI.Xaml.Controls;

namespace Lilium;

public sealed partial class MainWindow
{
    private string _savedBinding = "right";
    private string _savedThumbnailSize = "M";
    private string _savedFolderSort = "name";
    private string _savedFileSort = "name";

    // The reader has already saved this value. Update only its owning library.
    internal void ApplyBindingFromReader(string binding)
    {
        if (_closed) return;
        bool wasInitializing = _initializing;
        _initializing = true;
        try
        {
            _savedBinding = binding;
            SelectCombo(BindingBox, binding);
        }
        finally { _initializing = wasInitializing; }
    }

    private string ReadSetting(string key, string fallback) => ReadSetting(key, fallback, out _);

    private string ReadSetting(string key, string fallback, out bool succeeded)
    {
        try
        {
            var value = AppServices.Store.Get(key, fallback);
            succeeded = true;
            return value;
        }
        catch (Exception ex)
        {
            succeeded = false;
            App.WriteDiagnosticLog($"Setting read failed: {key}", ex);
            ShowSettingsReadFailure(ex);
            return fallback;
        }
    }

    private string ReadChoiceSetting(string key, string fallback, params string[] choices)
    {
        var value = ReadSetting(key, fallback);
        if (choices.Contains(value, StringComparer.Ordinal)) return value;
        ShowInvalidSetting(key);
        return fallback;
    }

    private int ReadIntSetting(string key, int fallback, int minimum, int maximum)
    {
        var raw = ReadSetting(key, fallback.ToString());
        if (int.TryParse(raw, out var value) && value >= minimum && value <= maximum) return value;
        ShowInvalidSetting(key);
        return fallback;
    }

    private bool TrySaveSetting(string key, string value)
    {
        try { AppServices.Store.Set(key, value); return true; }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog($"Setting save failed: {key}", ex);
            ShowSettingsSaveFailure(ex);
            return false;
        }
    }

    private bool TrySaveSettings(params (string Key, string Value)[] values)
    {
        try { AppServices.Store.SetMany(values); return true; }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog($"Setting save failed: {string.Join(", ", values.Select(value => value.Key))}", ex);
            ShowSettingsSaveFailure(ex);
            return false;
        }
    }

    private bool TryAddQuickAccess(string path)
    {
        try { AppServices.Store.AddQuickAccess(path); return true; }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Quick access add failed", ex);
            ShowSettingsSaveFailure(ex);
            return false;
        }
    }

    private bool TryRemoveQuickAccess(string path)
    {
        try { AppServices.Store.RemoveQuickAccess(path); return true; }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Quick access remove failed", ex);
            ShowSettingsSaveFailure(ex);
            return false;
        }
    }

    private void ShowSettingsReadFailure(Exception ex) => ShowSettingsNotice(
        L10n.Get("MainWindow_Settings_001"),
        L10n.Get("MainWindow_Settings_002") + ex.Message,
        InfoBarSeverity.Error);

    private void ShowSettingsSaveFailure(Exception ex) => ShowSettingsNotice(
        L10n.Get("MainWindow_Settings_003"),
        L10n.Get("MainWindow_Settings_004") + ex.Message,
        InfoBarSeverity.Error);

    private void ShowInvalidSetting(string key) => ShowSettingsNotice(
        L10n.Get("MainWindow_Settings_005"),
        L10n.Format("MainWindow_Settings_006", key),
        InfoBarSeverity.Warning);

    private void ShowSettingsNotice(string title, string message, InfoBarSeverity severity)
    {
        OperationCloseNotice.Title = title;
        OperationCloseNotice.Message = message;
        OperationCloseNotice.Severity = severity;
        OperationCloseNotice.IsOpen = true;
    }
}
