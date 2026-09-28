using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace Lilium;

public sealed partial class MainWindow
{
    private async Task<bool> TransferAsync(IReadOnlyList<IStorageItem> sources, string destination, bool move, bool allowDuplicate = false)
    {
        sources = sources.Where(item => !sources.Any(parent => parent.IsOfType(StorageItemTypes.Folder) &&
            !string.Equals(parent.Path, item.Path, StringComparison.OrdinalIgnoreCase) &&
            item.Path.StartsWith(parent.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var requests = new List<FileOperationJournal.Request>();
        int? allChoice = null;
            foreach (var source in sources)
            {
                string sourcePath = SafeFileSystem.Full(source.Path);
                string target = SafeFileSystem.Full(Path.Combine(destination, source.Name));
                if (string.Equals(sourcePath, target, StringComparison.OrdinalIgnoreCase))
                { if (!move && allowDuplicate) target = UniqueName(target); else continue; }
                SafeFileSystem.Separate(sourcePath, target);
                bool overwrite = false;
                if (SafeFileSystem.Exists(target))
                {
                    int choice;
                    if (allChoice is { } remembered) choice = remembered;
                    else
                    {
                    var choices = new ComboBox { SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
                    choices.Items.Add(L10n.Get("MainWindow_Transfers_001")); choices.Items.Add(L10n.Get("MainWindow_Transfers_002")); choices.Items.Add(L10n.Get("MainWindow_Transfers_003"));
                    var panel = new StackPanel { Spacing = 12 };
                    panel.Children.Add(new TextBlock { Text = target, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(choices);
                    var applyAll = new CheckBox { Content = L10n.Get("MainWindow_Transfers_004") };
                    panel.Children.Add(applyAll);
                    var dialog = new ContentDialog { Title = L10n.Get("MainWindow_Transfers_005"), Content = panel, PrimaryButtonText = L10n.Get("MainWindow_Transfers_006"), CloseButtonText = L10n.Get("MainWindow_Transfers_007"), XamlRoot = RootGrid.XamlRoot };
                    if (await ShowOwnedDialog(dialog) != ContentDialogResult.Primary) return false;
                    choice = choices.SelectedIndex;
                    if (applyAll.IsChecked == true) allChoice = choice;
                    }
                    if (choice == 0) { requests.Add(new() { Kind = move ? "Move" : "Copy", Source = sourcePath, Target = target, Skip = true }); continue; }
                    if (choice == 1) target = UniqueName(target);
                    else overwrite = true;
                }
                requests.Add(new() { Kind = move ? "Move" : "Copy", Source = sourcePath, Target = target, Overwrite = overwrite });
            }
        return await RunRequestsAsync(requests);
    }
    private static string UniqueName(string path)
    {
        string parent = Path.GetDirectoryName(path)!;
        string extension = Directory.Exists(path) ? "" : Path.GetExtension(path);
        string name = Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(parent, $"{name} ({n}){extension}");
            if (!SafeFileSystem.Exists(candidate)) return candidate;
        }
    }
}
