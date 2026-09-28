using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lilium;

public sealed partial class MainWindow
{
    private bool _operationNoticeOpen;
    private string? _recordFailure;
    private int _recoveryNoticeGeneration;
    private async Task<bool> CheckOperationNoticesAsync()
    {
        if (_closed || _operationNoticeOpen) return false;
        _operationNoticeOpen = true;
        try
        {
            var entries = await Task.Run(() => AppServices.Operations.List());
            _recordFailure = null;
            RecordRecoveryNotice.IsOpen = false;
            foreach (var entry in entries.Where(b => b.NeedsNotice))
            {
                if (_closed) return false;
                await ShowInterruptedOperationAsync(entry);
            }
            return !_closed;
        }
        catch (Exception ex) { ShowRecordFailure(ex); return false; }
        finally { PublishRecoveryNotices(); _operationNoticeOpen = false; }
    }
    private void ShowRecordFailure(Exception ex)
    {
        App.WriteDiagnosticLog("Operation records unavailable", ex);
        if (_closed) return;
        _recordFailure = ex.ToString();
        RecordRecoveryNotice.Title = L10n.Get("MainWindow_OperationNotices_001");
        RecordRecoveryNotice.Message = L10n.Get("MainWindow_OperationNotices_002");
        RecordRecoveryNotice.IsOpen = true;
    }
    private void PublishRecoveryNotices()
    {
        var notices = AppServices.Operations.TakeRecoveryNotices();
        foreach (var notice in notices)
            App.WriteDiagnosticLog($"Operation record quarantined: {notice.OriginalPath} -> {notice.QuarantinedPath}", new InvalidDataException(notice.Reason));
        if (_closed || notices.Length == 0) return;
        _recoveryNoticeGeneration++;
        OperationCloseNotice.Title = L10n.Format("MainWindow_OperationNotices_003", notices.Length);
        OperationCloseNotice.Message = L10n.Get("MainWindow_OperationNotices_004");
        OperationCloseNotice.Severity = InfoBarSeverity.Warning;
        OperationCloseNotice.IsOpen = true;
    }
    private async void RetryOperationRecords_Click(object sender, RoutedEventArgs e)
    {
        if (_fileOperationBusy || _operationNoticeOpen || _dialogDepth > 0) return;
        _fileOperationBusy = true;
        try
        {
            var details = new TextBlock
            {
                Text = L10n.Get("MainWindow_OperationNotices_005") +
                    Path.Combine(AppServices.RootPath, "crash.log") + "\n\n" + _recordFailure,
                IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap
            };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = L10n.Get("MainWindow_OperationNotices_006"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new Expander { Header = L10n.Get("MainWindow_OperationNotices_007"), Content = new ScrollViewer { Content = details, MaxHeight = 300, MaxWidth = 620 } });
            var dialog = new ContentDialog { Title = L10n.Get("MainWindow_OperationNotices_008"), Content = panel, PrimaryButtonText = L10n.Get("MainWindow_OperationNotices_009"), CloseButtonText = L10n.Get("MainWindow_OperationNotices_010") };
            if (await ShowOwnedDialog(dialog) != ContentDialogResult.Primary) return;
            var noticeGeneration = _recoveryNoticeGeneration;
            if (await CheckOperationNoticesAsync() && noticeGeneration == _recoveryNoticeGeneration)
            {
                OperationCloseNotice.Title = L10n.Get("MainWindow_OperationNotices_011");
                OperationCloseNotice.Message = L10n.Get("MainWindow_OperationNotices_012");
                OperationCloseNotice.Severity = InfoBarSeverity.Success;
                OperationCloseNotice.IsOpen = true;
            }
        }
        catch (Exception ex) { ShowRecordFailure(ex); }
        finally { _fileOperationBusy = false; }
    }
    private async Task ShowInterruptedOperationAsync(FileOperationJournal.Batch batch)
    {
        var text = new TextBlock { Text = FileOperationJournal.NoticeText(batch), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var dialog = new ContentDialog
        {
            Title = L10n.Get("MainWindow_OperationNotices_013"),
            Content = new ScrollViewer { Content = text, MaxHeight = 420, MaxWidth = 650 },
            CloseButtonText = L10n.Get("MainWindow_OperationNotices_014"), XamlRoot = RootGrid.XamlRoot
        };
        await ShowOwnedDialog(dialog);
        await Task.Run(() => AppServices.Operations.MarkNotified(batch.Id));
    }
    private async Task ShowOperationResultAsync(FileOperationJournal.Batch batch)
    {
        if (batch.State == FileOperationJournal.BatchStates.Cancelled) await ShowMessageAsync(L10n.Get("MainWindow_OperationNotices_015"), L10n.Get("MainWindow_OperationNotices_016"));
        else if (batch.NeedsNotice) await ShowInterruptedOperationAsync(batch);
    }
}
