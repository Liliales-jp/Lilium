using System.Diagnostics;

namespace Lilium;

internal enum FolderSearchPhase { Reading, Descendants, Sorting }

internal sealed record FolderSearchProgressSnapshot(FolderSearchPhase Phase, long Folders, long Items, long Seconds);

// Workers only update counters. The UI samples them at a bounded rate, including
// while a filesystem read is waiting and has no new counts to report.
internal sealed class FolderSearchProgress
{
    private readonly object _gate = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private FolderSearchPhase _phase;
    private long _folders;
    private long _items;

    internal void SetPhase(FolderSearchPhase phase) { lock (_gate) _phase = phase; }
    internal void ItemChecked() { lock (_gate) _items++; }
    internal void FolderCompleted() { lock (_gate) _folders++; }
    internal FolderSearchProgressSnapshot Snapshot()
    {
        lock (_gate) return new(_phase, _folders, _items, (long)_elapsed.Elapsed.TotalSeconds);
    }
}

// One rollback snapshot spans replacement requests. Clearing the visible list
// again must never replace the original snapshot with an empty list.
internal sealed class FolderLoadState<T>
{
    internal sealed record Snapshot(T[] Items, FolderFilterState Filters, ThumbnailRefreshState? View, bool NoMatches);
    internal FolderRequests.Request? Request { get; private set; }
    internal Snapshot? Previous { get; private set; }
    internal FolderSearchProgress? Progress { get; private set; }

    internal FolderSearchProgress Begin(FolderRequests.Request request, Func<Snapshot> capture)
    {
        Previous ??= capture();
        Request = request;
        return Progress = new FolderSearchProgress();
    }

    internal Snapshot? Finish(FolderRequests.Request request)
    {
        if (!ReferenceEquals(Request, request)) return null;
        var previous = Previous;
        Clear();
        return previous;
    }

    internal void Clear() { Request = null; Previous = null; Progress = null; }
}
