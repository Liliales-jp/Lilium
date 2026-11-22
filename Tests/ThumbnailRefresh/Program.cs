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

void Commit(FolderRequests owner, FolderRequests.Request? request)
{
    using (request)
        Check(request is not null && owner.TryCommit(request), "The latest request commits successfully");
}

var history = new FolderRequests();
var child = Path.Combine(folder, "cat-series");
var deepChild = Path.Combine(child, "volume", "cat-book");
var anotherChild = Path.Combine(folder, "cat-another");
var catFilter = new FolderFilterState(3, 4, "cat", true);
var dogFilter = new FolderFilterState(5, 1, "dog", false);
Commit(history, history.Navigate(folder));
Commit(history, history.Refresh(filters: catFilter, updateSearchInputs: true));
Check(history.History.Count == 1 && history.CurrentFilters == catFilter,
    "Applying a search replaces the current entry without adding history");
var resultView = new ThumbnailRefreshState(folder, [child, anotherChild], child, true, 0, 925);
history.SaveView(resultView);
using (var openChild = history.Navigate(child)!)
{
    Check(openChild.Filters == FolderFilterState.Empty && openChild.UpdateSearchInputs && openChild.View is null,
        "Opening a folder clears both ratings, keyword, recursion and old viewport");
    Check(history.CurrentFolder == folder && history.CurrentFilters == catFilter,
        "Pending navigation does not change the displayed folder or applied search");
    Check(history.TryCommit(openChild), "Child navigation commits");
}
Commit(history, history.Refresh(filters: dogFilter, updateSearchInputs: true));
var childView = new ThumbnailRefreshState(child, [Path.Combine(child, "dog.jpg")], null, false, 12, 600);
history.SaveView(childView);
using (var back = history.NavigateHistory(-1)!)
{
    Check(back.Folder == folder && back.Filters == catFilter && back.View == resultView,
        "Back restores the original search, multi-selection and viewport");
    Check(history.TryCommit(back), "Back commits");
}
using (var forward = history.NavigateHistory(1)!)
{
    Check(forward.Folder == child && forward.Filters == dogFilter && forward.View == childView,
        "Forward restores the destination entry's own search and view");
    Check(history.TryCommit(forward), "Forward commits");
}
// Up is an ordinary navigation, even when its path already occurs in history.
Commit(history, history.Navigate(folder, child));
Check(history.History.Count == 3 && history.CurrentFilters == FolderFilterState.Empty,
    "Up adds an unfiltered parent entry instead of restoring its old search");
Check(history.History[0].Filters == catFilter && history.History[2].Filters == FolderFilterState.Empty,
    "Separate visits to one path retain independent filter states");
Commit(history, history.NavigateHistory(-1));
Check(history.CurrentFolder == child && history.CurrentFilters == dogFilter,
    "Back immediately after Up returns to the child and its filters");
Commit(history, history.NavigateHistory(-1));
Commit(history, history.Refresh(filters: catFilter with { FileRating = 2 }));
Check(history.CanGoForward && history.History.Count == 3,
    "Editing filters in an existing history entry preserves forward history");
Commit(history, history.Navigate(anotherChild));
Check(history.History.Count == 2 && !history.CanGoForward && history.CurrentFilters == FolderFilterState.Empty,
    "Opening another search result discards the old forward branch and clears filters");
Commit(history, history.NavigateHistory(-1));
using (var same = history.Navigate(folder.ToUpperInvariant())!)
{
    Check(!same.Navigation && same.Filters == (catFilter with { FileRating = 2 }),
        "Reopening the same filesystem path is a case-insensitive refresh");
    Check(history.TryCommit(same), "Same-location refresh commits");
}
Check(history.History.Count == 2 && history.CanGoForward,
    "Same-location refresh adds no history and retains the forward branch");
using (var trailingSlash = history.Navigate(folder + Path.DirectorySeparatorChar)!)
{
    Check(!trailingSlash.Navigation && trailingSlash.Folder == folder && trailingSlash.Filters == history.CurrentFilters,
        "A trailing directory separator still identifies the same displayed location");
    Check(history.TryCommit(trailingSlash), "Trailing-separator refresh commits");
}
using (var plainRefresh = history.Refresh()!)
{
    Check(plainRefresh.Filters.Keyword == "cat" && !plainRefresh.UpdateSearchInputs,
        "Refresh uses applied search and does not overwrite a draft keyword");
    Check(history.TryCommit(plainRefresh), "Plain refresh commits");
}
using (var rating = history.Refresh(filters: history.CurrentFilters with { FolderRating = 1 })!)
{
    Check(rating.Filters.Keyword == "cat" && rating.Filters.IncludeSubfolders && !rating.UpdateSearchInputs,
        "Rating-only updates retain the applied keyword and recursive search");
    Check(history.TryCommit(rating), "Rating update commits");
}
Commit(history, history.Refresh(automatic: true));
Check(history.CurrentFilters.Keyword == "cat", "Automatic updates retain the applied search");
Commit(history, history.Refresh(filters: FolderFilterState.Empty, updateSearchInputs: true));
Check(history.CurrentFilters == FolderFilterState.Empty && history.History.Count == 2,
    "Clear resets all filters within the same history entry");
Commit(history, history.Refresh(filters: catFilter));
Commit(history, history.Navigate(deepChild));
using (var backFromDeep = history.NavigateHistory(-1)!)
{
    Check(backFromDeep.Folder == folder && backFromDeep.View == resultView && backFromDeep.Filters == catFilter,
        "Back from a deep recursive result restores the search root and its exact view");
    Check(history.TryCommit(backFromDeep), "Back from deep result commits");
}

var originalCount = history.History.Count;
var originalIndex = history.HistoryIndex;
using (var missing = history.Navigate(Path.Combine(folder, "missing"))!)
{
    Check(history.TryFail(missing), "Missing destination fails without committing");
    Check(history.CurrentFolder == folder && history.CurrentFilters == catFilter &&
        history.History.Count == originalCount && history.HistoryIndex == originalIndex && history.CanGoForward,
        "A failed navigation preserves folder, filters, history position and forward history");
}
using (var failedSearch = history.Refresh(filters: dogFilter, updateSearchInputs: true)!)
{
    Check(history.TryFail(failedSearch) && history.CurrentFilters == catFilter,
        "A failed search retains the previously applied conditions");
}
history.SaveView(childView);
Check(history.History[history.HistoryIndex].View == resultView,
    "A viewport snapshot from another folder cannot overwrite the current entry");

using (var first = history.Navigate(child)!)
using (var newer = history.Navigate(anotherChild)!)
{
    Check(first.CancellationToken.IsCancellationRequested && !history.TryCommit(first) && !history.TryFail(first),
        "A superseded navigation cannot commit or roll back newer history");
    Check(history.Refresh(automatic: true) is null && history.Pending == newer,
        "An old watcher cannot replace pending navigation");
    using var resized = history.Refresh()!;
    Check(resized.Folder == anotherChild && resized.Navigation && resized.Filters == FolderFilterState.Empty,
        "Sort or size refresh while opening preserves destination and cleared filters");
    Check(history.TryCommit(resized) && history.CurrentFolder == anotherChild,
        "Only the replacement navigation commits");
}
using (var pendingBack = history.NavigateHistory(-1)!)
using (var replacement = history.Refresh()!)
{
    Check(replacement.Filters == catFilter && replacement.View == resultView && replacement.HistoryIndex == 0,
        "Refreshing pending Back retains the historical search, view and index");
    Check(!history.TryCommit(pendingBack) && history.TryCommit(replacement),
        "An old Back completion cannot supersede its replacement");
}
using (var backFail = history.NavigateHistory(1)!)
{
    Check(history.TryFail(backFail) && history.HistoryIndex == 0 && history.CurrentFilters == catFilter,
        "Failed history navigation leaves the original position and filters intact");
}
using (var backPending = history.NavigateHistory(1)!)
using (var toOriginal = history.NavigateHistory(-1)!)
{
    Check(toOriginal.Folder == folder && toOriginal.Filters == catFilter && toOriginal.View == resultView,
        "Rapid Forward/Back resolves relative to the requested history position");
    Check(!history.TryCommit(backPending) && history.TryCommit(toOriginal),
        "The last history click wins");
}
using (var pendingSearch = history.Refresh(filters: dogFilter, updateSearchInputs: true)!)
using (var resizedSearch = history.Refresh()!)
{
    Check(resizedSearch.Filters == dogFilter && resizedSearch.UpdateSearchInputs,
        "Refreshing an in-flight explicit search preserves its applied inputs");
    Check(history.TryCommit(resizedSearch), "Replacement search commits");
}
var root = Path.GetPathRoot(folder)!;
Commit(history, history.Navigate(root));
Commit(history, history.Refresh(filters: catFilter));
using (var rootUp = history.Navigate(root)!)
{
    Check(!rootUp.Navigation && rootUp.Filters == catFilter,
        "Up at a drive root preserves all conditions");
    Check(history.TryCommit(rootUp), "Root refresh commits");
}

// Archive locations are opaque history destinations; their encoded entry path is retained.
var archiveRoot = "lilium-archive:0:fixture:";
var archiveChild = archiveRoot + "chapter";
Commit(history, history.Navigate(archiveRoot, persistFolder: false));
Commit(history, history.Refresh(filters: catFilter));
Commit(history, history.Navigate(archiveChild, persistFolder: false));
Check(history.CurrentFilters == FolderFilterState.Empty, "Opening an archive subfolder clears filters");
Commit(history, history.NavigateHistory(-1));
Check(history.CurrentFolder == archiveRoot && history.CurrentFilters == catFilter,
    "Archive Back restores its own applied search state");
using (var caseDistinctArchive = history.Navigate(archiveRoot.ToUpperInvariant())!)
{
    Check(caseDistinctArchive.Navigation && caseDistinctArchive.Filters == FolderFilterState.Empty,
        "Encoded archive locations are compared exactly rather than as Windows filesystem paths");
    Check(history.TryFail(caseDistinctArchive), "Case-distinct archive test does not alter committed history");
}
Check(!new FolderFilterState(IncludeSubfolders: true).Recursive &&
    !new FolderFilterState(Keyword: "  ", IncludeSubfolders: true).Recursive && catFilter.Recursive,
    "Recursive scans run only with a nonblank applied keyword");
using (var closing = history.Navigate(child)!)
{
    history.Close();
    Check(closing.CancellationToken.IsCancellationRequested && !history.TryCommit(closing) &&
        !history.CanGoBack && !history.CanGoForward && history.Refresh() is null,
        "Closing a window cancels requests and prevents late history changes");
}
Check(new FolderRequests().History.Count == 0 && new FolderRequests().CurrentFilters == FolderFilterState.Empty,
    "A new window starts without persisted navigation or search state");

var cancellable = new FolderRequests();
Commit(cancellable, cancellable.Navigate(folder));
Commit(cancellable, cancellable.Refresh(filters: catFilter));
cancellable.SaveView(resultView);
var display = new FolderLoadState<string>();
var listBeforeSearch = new[] { "cat-first", "cat-second" };
using (var searching = cancellable.Refresh(filters: dogFilter, updateSearchInputs: true)!)
{
    var firstProgress = display.Begin(searching, () => new(listBeforeSearch, cancellable.CurrentFilters, resultView, false));
    firstProgress.FolderCompleted();
    firstProgress.SetPhase(FolderSearchPhase.Descendants);
    Parallel.For(0, 10000, _ => firstProgress.ItemChecked());
    var progressSnapshot = firstProgress.Snapshot();
    Check(progressSnapshot.Folders == 1 && progressSnapshot.Items == 10000 &&
        progressSnapshot.Phase == FolderSearchPhase.Descendants && progressSnapshot.Seconds >= 0,
        "Progress counts are accurate under concurrent reports and expose phase and elapsed time");
    var stableSnapshot = firstProgress.Snapshot();
    firstProgress.ItemChecked();
    Check(stableSnapshot.Items == 10000 && firstProgress.Snapshot().Items == 10001,
        "UI progress snapshots remain stable when workers keep reporting");
    cancellable.CancelPending();
    Check(searching.CancellationToken.IsCancellationRequested && cancellable.Pending == searching,
        "Cancel requests cooperative stopping while retaining the pending request until it finishes");
    Check(!cancellable.TryCommit(searching) && cancellable.CurrentFilters == catFilter,
        "A cancelled search cannot commit even if its worker already returned results");
    Check(cancellable.TryFail(searching), "Cancellation releases the pending request without changing history");
    var rollback = display.Finish(searching)!;
    Check(rollback.Items.SequenceEqual(listBeforeSearch) && rollback.Filters == catFilter && rollback.View == resultView,
        "Cancellation restores the original items, applied conditions, selection and viewport");
    Check(display.Request is null && display.Previous is null && display.Progress is null &&
        cancellable.History.Count == 1 && cancellable.HistoryIndex == 0,
        "Cancellation removes progress state and leaves the original history position intact");
}
using (var slowSearch = cancellable.Refresh(filters: dogFilter, updateSearchInputs: true)!)
{
    var obsoleteProgress = display.Begin(slowSearch,
        () => new(listBeforeSearch, cancellable.CurrentFilters, resultView, false));
    using var replacementSearch = cancellable.Refresh(filters: catFilter with { Keyword = "second" }, updateSearchInputs: true)!;
    bool recaptured = false;
    var replacementProgress = display.Begin(replacementSearch, () =>
    {
        recaptured = true;
        return new([], dogFilter, null, true);
    });
    obsoleteProgress.ItemChecked();
    Check(!recaptured && display.Previous!.Items.SequenceEqual(listBeforeSearch) &&
        display.Previous.Filters == catFilter && display.Previous.View == resultView,
        "Replacing an in-flight search retains the original rollback snapshot instead of an empty display");
    Check(display.Finish(slowSearch) is null && display.Request == replacementSearch &&
        display.Progress == replacementProgress && replacementProgress.Snapshot().Items == 0,
        "Late progress and completion from an older request cannot hide or corrupt the latest progress");
    cancellable.CancelPending();
    Check(!cancellable.TryCommit(slowSearch) && !cancellable.TryFail(slowSearch) &&
        cancellable.TryFail(replacementSearch), "Only the latest cancelled search can roll back");
    Check(display.Finish(replacementSearch)!.Items.SequenceEqual(listBeforeSearch),
        "Cancelling replacement searches restores the list that preceded the entire sequence");
}
using (var fromEmpty = cancellable.Refresh(filters: dogFilter)!)
{
    display.Begin(fromEmpty, () => new([], catFilter, resultView, true));
    Check(cancellable.TryFail(fromEmpty) && display.Finish(fromEmpty) is { NoMatches: true, Items.Length: 0 },
        "Failure restores an earlier no-matches state rather than presenting it as a new result");
}
using (var completed = cancellable.Refresh(filters: dogFilter)!)
{
    display.Begin(completed, () => new(listBeforeSearch, catFilter, resultView, false));
    Check(cancellable.TryCommit(completed), "A completed search commits its conditions");
    Check(display.Finish(completed) is not null && display.Finish(completed) is null,
        "Successful completion discards rollback state exactly once");
}
using (var disposedWindow = cancellable.Navigate(child)!)
{
    display.Begin(disposedWindow, () => new(listBeforeSearch, dogFilter, resultView, false));
    cancellable.Close();
    display.Clear();
    Check(display.Request is null && display.Previous is null && display.Progress is null &&
        display.Finish(disposedWindow) is null,
        "Closing releases retained list and progress state and rejects late UI completion");
}
Console.WriteLine($"Thumbnail refresh checks passed: {checks}");
