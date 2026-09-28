using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Lilium;

internal abstract class ViewerSource : IDisposable
{
    private readonly Dictionary<int, bool> _orientations = [];
    internal abstract int Count { get; }
    internal virtual bool IsPdf => false;
    internal virtual Task InitializeAsync(CancellationToken cancellation) => Task.CompletedTask;
    internal bool? IsPortrait(int index) => _orientations.TryGetValue(index, out var value) ? value : null;
    internal async Task EnsureOrientationAsync(int index, CancellationToken cancellation)
    {
        if (_orientations.ContainsKey(index)) return;
        var value = await ReadOrientationAsync(index, cancellation);
        cancellation.ThrowIfCancellationRequested();
        _orientations[index] = value;
    }
    protected abstract Task<bool> ReadOrientationAsync(int index, CancellationToken cancellation);
    internal abstract Task<IRandomAccessStream> OpenPageAsync(int index, double width, double height,
        CancellationToken cancellation);
    public virtual void Dispose() { }
}

internal sealed class ImageViewerSource(string[] paths) : ViewerSource
{
    internal override int Count => paths.Length;
    protected override async Task<bool> ReadOrientationAsync(int index, CancellationToken cancellation)
    {
        using var stream = await OpenPageAsync(index, 0, 0, cancellation);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        cancellation.ThrowIfCancellationRequested();
        return ViewerImageOrientation.IsPortrait(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
    }
    internal override async Task<IRandomAccessStream> OpenPageAsync(int index, double width, double height,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ArchiveLocation.TryParse(paths[index], out var archive);
        var stream = archive is not null ? await ArchiveCatalog.OpenImageAsync(archive, cancellation)
            : await (await StorageFile.GetFileFromPathAsync(paths[index])).OpenAsync(FileAccessMode.Read);
        if (cancellation.IsCancellationRequested) { stream.Dispose(); cancellation.ThrowIfCancellationRequested(); }
        return stream;
    }
}

internal sealed class PdfViewerSource(string path) : ViewerSource
{
    private PdfSession? _session;
    private bool _closed;
    internal override bool IsPdf => true;
    internal override int Count => _session?.Count ?? 0;
    internal override async Task InitializeAsync(CancellationToken cancellation)
    {
        var session = await PdfSession.OpenAsync(path, cancellation);
        if (_closed || cancellation.IsCancellationRequested)
        {
            session.Dispose();
            throw new OperationCanceledException(cancellation);
        }
        _session = session;
    }
    protected override Task<bool> ReadOrientationAsync(int index, CancellationToken cancellation) =>
        _session!.IsPortraitAsync(index, cancellation);
    internal override Task<IRandomAccessStream> OpenPageAsync(int index, double width, double height,
        CancellationToken cancellation) => _session!.RenderAsync(index, width, height, cancellation);
    public override void Dispose()
    {
        _closed = true;
        _session?.Dispose();
        _session = null;
    }
}
