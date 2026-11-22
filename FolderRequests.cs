namespace Lilium;

// Owned by one window and used only on its UI thread. Workers return data;
// they never change the committed folder or navigation history.
internal sealed class FolderRequests
{
    internal sealed record Request(string Folder, bool Navigation, string? FocusPath,
        bool PersistFolder, int? HistoryIndex, FolderFilterState Filters,
        ThumbnailRefreshState? View = null, bool UpdateSearchInputs = false) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        internal CancellationToken CancellationToken => _cancellation.Token;

        internal void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => _cancellation.Dispose();
    }

    private readonly List<FolderHistoryEntry> _history = [];
    private int _historyIndex = -1;
    private bool _closed;
    internal string? CurrentFolder { get; private set; }
    internal Request? Latest { get; private set; }
    internal Request? Pending { get; private set; }
    internal IReadOnlyList<FolderHistoryEntry> History => _history.AsReadOnly();
    internal FolderFilterState CurrentFilters => _historyIndex < 0
        ? FolderFilterState.Empty : _history[_historyIndex].Filters;
    internal FolderFilterState RequestedFilters => Pending?.Filters ?? CurrentFilters;
    internal int HistoryIndex => _historyIndex;
    private int RequestedHistoryIndex => Pending?.HistoryIndex ?? _historyIndex;
    internal bool CanGoBack => !_closed && RequestedHistoryIndex > 0;
    internal bool CanGoForward => !_closed && RequestedHistoryIndex < _history.Count - 1;

    private static bool SameFolder(string? first, string second) =>
        first is not null && Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
            ? string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second),
                StringComparison.OrdinalIgnoreCase)
            : string.Equals(first, second, StringComparison.Ordinal);

    internal Request? Navigate(string folder, string? focusPath = null, bool persistFolder = true)
    {
        // Reopening the displayed location (including Up at a drive root) is a refresh.
        if (SameFolder(CurrentFolder, folder))
            return Begin(new Request(CurrentFolder!, false, null, false, null, CurrentFilters));
        return Begin(new Request(folder, true, focusPath, persistFolder, null,
            FolderFilterState.Empty, UpdateSearchInputs: true));
    }

    internal void SaveView(ThumbnailRefreshState? view)
    {
        if (!_closed && _historyIndex >= 0 && view is not null &&
            SameFolder(CurrentFolder, view.Folder))
            _history[_historyIndex] = _history[_historyIndex] with { View = view };
    }

    internal Request? NavigateHistory(int delta, Func<string, string, string?>? resolveFocus = null)
    {
        int current = RequestedHistoryIndex;
        int next = current + delta;
        if (next < 0 || next >= _history.Count) return null;
        var source = _history[current].Folder;
        var entry = _history[next];
        var destination = entry.Folder;
        string? focusPath = delta < 0 && Path.IsPathFullyQualified(source) &&
            Path.IsPathFullyQualified(destination) &&
            string.Equals(Directory.GetParent(source)?.FullName, destination, StringComparison.OrdinalIgnoreCase)
                ? source : null;
        if (delta < 0 && focusPath is null) focusPath = resolveFocus?.Invoke(source, destination);
        return Begin(new Request(destination, true, focusPath, true, next,
            entry.Filters, entry.View, UpdateSearchInputs: true));
    }

    internal Request? Refresh(bool automatic = false, FolderFilterState? filters = null,
        bool updateSearchInputs = false)
    {
        // An old folder's watcher must not replace an explicit navigation.
        if (automatic && Pending is not null) return null;
        // Search/sort/refresh while navigating still targets the requested folder.
        if (Pending is { } pending)
            return Begin(new Request(pending.Folder, pending.Navigation, pending.FocusPath,
                pending.PersistFolder, pending.HistoryIndex, filters ?? pending.Filters,
                pending.View, pending.UpdateSearchInputs || updateSearchInputs));
        return CurrentFolder is null ? null
            : Begin(new Request(CurrentFolder, false, null, false, null,
                filters ?? CurrentFilters, UpdateSearchInputs: updateSearchInputs));
    }

    private Request? Begin(Request request)
    {
        if (_closed) { request.Dispose(); return null; }
        var previous = Latest;
        Latest = Pending = request;
        previous?.Cancel();
        return request;
    }

    internal bool IsCurrent(Request? request) =>
        !_closed && request is not null && ReferenceEquals(Latest, request);

    internal bool TryCommit(Request request)
    {
        if (!IsCurrent(request) || !ReferenceEquals(Pending, request)) return false;
        if (request.CancellationToken.IsCancellationRequested) return false;
        if (request.Navigation)
        {
            if (request.HistoryIndex is { } index) _historyIndex = index;
            else
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                _history.Add(new FolderHistoryEntry(request.Folder, request.Filters));
                _historyIndex = _history.Count - 1;
            }
            CurrentFolder = request.Folder;
        }
        if (_historyIndex >= 0)
            _history[_historyIndex] = _history[_historyIndex] with { Filters = request.Filters };
        Pending = null;
        return true;
    }

    internal bool TryFail(Request request)
    {
        if (!IsCurrent(request) || !ReferenceEquals(Pending, request)) return false;
        Pending = null;
        return true;
    }

    internal void CancelPending() => Pending?.Cancel();

    internal void Close()
    {
        _closed = true;
        Latest?.Cancel();
        Pending = Latest = null;
    }
}
