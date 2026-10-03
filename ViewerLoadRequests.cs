namespace Lilium;

// Keeps image I/O results from reaching a newer or already closed Viewer window.
internal sealed class ViewerLoadRequests
{
    internal sealed class Request : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        internal CancellationToken CancellationToken => _cancellation.Token;
        internal void Cancel() => _cancellation.Cancel();
        public void Dispose() => _cancellation.Dispose();
    }

    private bool _closed;
    private Request? _latest;

    internal Request? Begin()
    {
        if (_closed) return null;
        var previous = _latest;
        var request = new Request();
        _latest = request;
        previous?.Cancel();
        return request;
    }

    internal bool IsCurrent(Request request) => !_closed && ReferenceEquals(_latest, request);

    internal bool Complete(Request request)
    {
        if (!IsCurrent(request)) return false;
        _latest = null;
        return true;
    }

    internal void Close()
    {
        _closed = true;
        Cancel();
    }

    internal void Cancel()
    {
        _latest?.Cancel();
        _latest = null;
    }
}
