using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lilium;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _transferCancellation;
    private TextBlock? _operationNote;
    private void ShowBusyCloseNotice()
    {
        string message = L10n.Get("MainWindow_Progress_001");
        OperationCloseNotice.Title = L10n.Get("MainWindow_Progress_002");
        OperationCloseNotice.Message = message;
        OperationCloseNotice.Severity = InfoBarSeverity.Warning;
        OperationCloseNotice.IsOpen = true;
        if (_operationNote is not null) _operationNote.Text = message;
    }
    private async Task<T> WithProgressAsync<T>(string title, Func<CancellationToken, Action<OperationProgress>, T> work)
    {
        ShellRecycle.OwnerWindow.Value = WinRT.Interop.WindowNative.GetWindowHandle(this);
        using var cancellation = new CancellationTokenSource(); _transferCancellation = cancellation;
        var phase = new TextBlock { Text = L10n.Get("MainWindow_Progress_003"), TextWrapping = TextWrapping.Wrap };
        var path = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxHeight = 100 };
        var counts = new TextBlock();
        var progress = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100, Height = 6 };
        var bytes = new TextBlock();
        var note = new TextBlock { Text = L10n.Get("MainWindow_Progress_004"), TextWrapping = TextWrapping.Wrap };
        _operationNote = note;
        var panel = new StackPanel { Spacing = 12, MinWidth = 440, MaxWidth = 620 };
        foreach (var element in new FrameworkElement[] { phase, path, counts, progress, bytes, note }) panel.Children.Add(element);
        var dialog = new ContentDialog { Title = title, Content = panel, CloseButtonText = L10n.Get("MainWindow_Progress_005"), XamlRoot = RootGrid.XamlRoot };
        bool finished = false;
        dialog.CloseButtonClick += (_, args) => { args.Cancel = true; cancellation.Cancel(); dialog.CloseButtonText = L10n.Get("MainWindow_Progress_006"); note.Text = L10n.Get("MainWindow_Progress_007"); };
        dialog.Closing += (_, args) => { if (!finished) { args.Cancel = true; cancellation.Cancel(); } };
        var uiProgress = new Progress<OperationProgress>(p =>
        {
            if (finished) return;
            phase.Text = p.Phase; path.Text = p.Path;
            counts.Text = p.Count > 0 ? L10n.Format("MainWindow_Progress_008", p.Index, p.Count) : "";
            progress.IsIndeterminate = p.Total <= 0;
            if (p.Total > 0) progress.Value = Math.Clamp(100.0 * p.Bytes / p.Total, 0, 100);
            bytes.Text = p.Total <= 0 ? "" : p.WorkUnits ? L10n.Format("MainWindow_Progress_009", Math.Clamp(100.0 * p.Bytes / p.Total, 0, 100)) : L10n.Format("MainWindow_Progress_010", p.Bytes / 1048576.0, p.Total / 1048576.0);
        });
        long last = 0; string previousPhase = ""; string previousPath = "";
        void Report(OperationProgress p)
        {
            long now = Environment.TickCount64;
            if (p.Phase != previousPhase || p.Path != previousPath || now - last >= 100 || p.Total > 0 && p.Bytes >= p.Total)
            { last = now; previousPhase = p.Phase; previousPath = p.Path; ((IProgress<OperationProgress>)uiProgress).Report(p); }
        }
        var task = Task.Run(() => work(cancellation.Token, Report));
        Windows.Foundation.IAsyncOperation<ContentDialogResult>? shown = null;
        try
        {
            if (await Task.WhenAny(task, Task.Delay(500)) != task && !task.IsCompleted) shown = ShowOwnedDialog(dialog);
            return await task;
        }
        catch (Exception ex)
        {
            if (ex is FileOperationJournal.RecordAccessException) ShowRecordFailure(ex);
            cancellation.Cancel();
            // Even if displaying the dialog failed, do not release the busy guard
            // while the native worker is still changing files.
            try { await task; } catch { }
            throw;
        }
        finally { finished = true; if (shown is not null) { dialog.Hide(); await shown; } _transferCancellation = null; _operationNote = null; PublishRecoveryNotices(); }
    }
    private async Task<bool> RunRequestsAsync(List<FileOperationJournal.Request> requests)
    {
        if (requests.Count == 0) return true;
        if (!await CheckOperationNoticesAsync()) return false;
        await WithProgressAsync(L10n.Get("MainWindow_Progress_011"), (token, report) => { AppServices.Operations.Prepare(requests, token, report); return true; });
        if (requests.Any(r => r.Overwrite && !r.Skip))
        {
            var warning = new ContentDialog
            {
                Title = L10n.Get("MainWindow_Progress_012"),
                Content = L10n.Get("MainWindow_Progress_013"),
                PrimaryButtonText = L10n.Get("MainWindow_Progress_014"), CloseButtonText = L10n.Get("MainWindow_Progress_015"), DefaultButton = ContentDialogButton.Close, XamlRoot = RootGrid.XamlRoot
            };
            if (await ShowOwnedDialog(warning) != ContentDialogResult.Primary) return false;
        }
        var result = await WithProgressAsync(L10n.Get("MainWindow_Progress_016"), (token, report) => AppServices.Operations.Execute(requests, token, report));
        await ShowOperationResultAsync(result);
        return result.State == FileOperationJournal.BatchStates.Complete;
    }
    private async Task UndoLastAsync()
    {
        try { if (!await CheckOperationNoticesAsync()) return; var result = await WithProgressAsync(L10n.Get("MainWindow_Progress_017"), (token, report) => AppServices.Operations.Undo(token, report)); await ShowOperationResultAsync(result); }
        catch (FileOperationJournal.EditedCopyException ex)
        {
            var confirm = new ContentDialog { Title = L10n.Get("MainWindow_Progress_018"), Content = ex.Message, PrimaryButtonText = L10n.Get("MainWindow_Progress_019"), CloseButtonText = L10n.Get("MainWindow_Progress_020"), DefaultButton = ContentDialogButton.Close, XamlRoot = RootGrid.XamlRoot };
            if (await ShowOwnedDialog(confirm) == ContentDialogResult.Primary)
            {
                var result = await WithProgressAsync(L10n.Get("MainWindow_Progress_021"), (token, report) => AppServices.Operations.Undo(token, report, confirmedCopyBatchId: ex.BatchId));
                await ShowOperationResultAsync(result);
            }
        }
    }
}
