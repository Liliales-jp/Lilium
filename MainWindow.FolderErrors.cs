using Microsoft.UI.Xaml.Controls;

namespace Lilium;

public sealed partial class MainWindow
{
    private void ShowFolderReadFailure(string path, FolderReadStatus status, Exception? error, bool tree = false)
    {
        var title = status switch
        {
            FolderReadStatus.NotFound => L10n.Get("MainWindow_FolderErrors_001"),
            FolderReadStatus.AccessDenied => L10n.Get("MainWindow_FolderErrors_002"),
            FolderReadStatus.Partial => L10n.Get("MainWindow_FolderErrors_003"),
            _ => L10n.Get("MainWindow_FolderErrors_004")
        };
        var retained = tree
            ? L10n.Get("MainWindow_FolderErrors_005")
            : _currentFolder is null
                ? L10n.Get("MainWindow_FolderErrors_006")
                : L10n.Get("MainWindow_FolderErrors_007");
        var detail = error?.GetBaseException().Message;
        var message = $"{retained}\n{path}" + (string.IsNullOrWhiteSpace(detail) ? "" : "\n" + detail);
        ShowSettingsNotice(title, message, status == FolderReadStatus.Partial ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
        if (error is not null) App.WriteDiagnosticLog($"Folder read failed ({status}): {path}", error);
    }

    private bool TryExpandTreeNode(TreeViewNode treeNode, FolderNode folder)
    {
        var result = FileCatalog.ExpandTreeNode(treeNode, folder);
        if (result.IsSuccess) return true;
        ShowFolderReadFailure(folder.Path, result.Status, result.Error, tree: true);
        return false;
    }
}
