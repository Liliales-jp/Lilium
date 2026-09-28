using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lilium;

public sealed partial class MainWindow
{
    private sealed class ThumbnailSession(LibraryItem[] items)
    {
        internal readonly LibraryItem[] Items = items;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly HashSet<int> Failed = [];
        internal readonly HashSet<int> Loaded = [];
        internal readonly int Generation = FileCatalog.CacheGeneration;
        internal readonly bool Limited = ThumbnailRange.ShouldLimit(items.Count(i => !string.IsNullOrEmpty(i.ThumbnailSourcePath)));
        internal bool Running;
    }

    private ThumbnailSession? _thumbnailSession;
    private readonly DispatcherTimer _thumbnailTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void InitializeThumbnails()
    {
        _thumbnailTimer.Tick += (_, _) =>
        {
            if (_thumbnailSession is { } session) _ = PumpThumbnailsAsync(session);
        };
        Closed += (_, _) => { StopThumbnails(); _items.Clear(); };
    }

    private void StopThumbnails()
    {
        _thumbnailTimer.Stop();
        if (_thumbnailSession is not { } session) return;
        _thumbnailSession = null;
        session.Cancellation.Cancel();
        foreach (var item in session.Items) item.Thumbnail = null;
        session.Loaded.Clear();
        // Drop old item references even when the last in-flight decoder has not returned.
        Array.Clear(session.Items);
        if (!session.Running) session.Cancellation.Dispose();
    }

    private void StartThumbnails()
    {
        if (_closed || _items.Count == 0) return;
        _thumbnailSession = new ThumbnailSession(_items.ToArray());
        // Wait for layout before determining the viewport; no full-list eager loading.
        _thumbnailTimer.Start();
    }

    private ThumbnailRange? CurrentThumbnailRange(ThumbnailSession session)
    {
        if (ThumbnailGrid.ItemsPanelRoot is not ItemsWrapGrid panel || panel.FirstVisibleIndex < 0 ||
            panel.LastVisibleIndex < panel.FirstVisibleIndex || ThumbnailGrid.ActualHeight <= 0) return null;
        return ThumbnailRange.Create(session.Items.Length, panel.FirstVisibleIndex, panel.LastVisibleIndex);
    }

    private async Task PumpThumbnailsAsync(ThumbnailSession session)
    {
        if (session.Running || session.Cancellation.IsCancellationRequested) return;
        session.Running = true;
        try
        {
            while (ReferenceEquals(session, _thumbnailSession) && !session.Cancellation.IsCancellationRequested)
            {
                if (session.Generation != FileCatalog.CacheGeneration)
                {
                    // Manual cache deletion stops generation without clearing displayed thumbnails.
                    session.Cancellation.Cancel();
                    _thumbnailTimer.Stop();
                    return;
                }
                if (CurrentThumbnailRange(session) is not { } range) return;
                if (session.Limited)
                {
                    foreach (var index in session.Loaded.Where(i => !range.Keeps(i)).ToArray())
                    {
                        session.Items[index].Thumbnail = null;
                        session.Loaded.Remove(index);
                    }
                }
                int next = -1, nearest = int.MaxValue;
                var start = session.Limited ? range.KeepFirst : 0;
                var end = session.Limited ? range.KeepLast : session.Items.Length - 1;
                for (int i = start; i <= end; i++)
                {
                    if (session.Loaded.Contains(i) || session.Failed.Contains(i) || string.IsNullOrEmpty(session.Items[i].ThumbnailSourcePath)) continue;
                    var distance = range.Distance(i);
                    if (distance < nearest) { next = i; nearest = distance; }
                }
                if (next < 0) return;
                try
                {
                    var bitmap = await FileCatalog.LoadThumbnailAsync(session.Items[next].ThumbnailSourcePath,
                        session.Generation, session.Cancellation.Token);
                    if (!ReferenceEquals(session, _thumbnailSession) || session.Cancellation.IsCancellationRequested) return;
                    // Scrolling can change the desired range while decoding this image.
                    if (!session.Limited || CurrentThumbnailRange(session) is { } latest && latest.Keeps(next))
                    {
                        session.Items[next].Thumbnail = bitmap;
                        session.Loaded.Add(next);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { session.Failed.Add(next); }
            }
        }
        finally
        {
            session.Running = false;
            if (!ReferenceEquals(session, _thumbnailSession)) session.Cancellation.Dispose();
        }
    }
}
