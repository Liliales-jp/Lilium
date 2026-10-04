using Lilium;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
var folder = Path.GetFullPath("fixture-library");
var source = Path.Combine(folder, "book.pdf");
var target = Path.Combine(folder, "book {zpi$r=5}.pdf");
var other = Path.Combine(folder, "other.cbz");
var state = new ThumbnailRefreshState(folder, [source, other], source, true, 17, 1250.5);
var renamed = state.Renamed(source, target, false);
Check(renamed.SelectedPaths.SequenceEqual([target, other]), "Rating rename retains selection at the new path");
Check(renamed.FocusPath == target, "Keyboard focus follows the renamed file");
Check(renamed.VerticalOffset == 1250.5 && renamed.HorizontalOffset == 17, "Rename retains both exact viewport offsets");
Check(renamed.Folder == folder && renamed.RestoreFocus, "Rename retains the displayed folder and focus policy");
Check(state.FocusPath == source && state.SelectedPaths[0] == source, "Failed or cancelled renames can reuse the original snapshot");
var differentFocus = (state with { FocusPath = other, RestoreFocus = false }).Renamed(source, target, false);
Check(differentFocus.FocusPath == other && !differentFocus.RestoreFocus, "Focus distinct from selection remains unchanged without stealing focus");
var dir = Path.Combine(folder, "series");
var renamedDir = Path.Combine(folder, "series {zpi$r=4}");
var descendant = Path.Combine(dir, "volume", "page.jpg");
var sibling = Path.Combine(folder, "series-extra", "page.jpg");
var subtree = new ThumbnailRefreshState(folder, [dir, descendant, sibling], descendant, true, 0, 300)
    .Renamed(dir.ToUpperInvariant(), renamedDir, true);
Check(subtree.SelectedPaths.SequenceEqual([renamedDir, Path.Combine(renamedDir, "volume", "page.jpg"), sibling]),
    "Folder rating remaps descendants case-insensitively and leaves similar sibling names alone");
Check(subtree.FocusPath == Path.Combine(renamedDir, "volume", "page.jpg"), "Descendant focus follows a renamed folder");
Check((state with { FocusPath = null }).Renamed(source, target, false).FocusPath is null, "A missing focus target is retained");
var requests = new FolderRequests();
using var initial = requests.Navigate(folder)!;
Check(requests.TryCommit(initial), "Initial folder commits");
using var refresh = requests.Refresh()!;
Check(!refresh.Navigation && requests.TryCommit(refresh) && requests.IsCurrent(refresh), "Refresh restoration belongs to its current request");
using var next = requests.Navigate(Path.Combine(folder, "next"))!;
Check(!requests.IsCurrent(refresh), "A new navigation invalidates a queued viewport restoration");
Console.WriteLine($"Thumbnail refresh checks passed: {checks}");
