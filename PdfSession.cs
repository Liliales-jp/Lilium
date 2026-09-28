using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Lilium;

// One document per reader (or one temporary document for a cover). Native PDF
// operations finish before their page/input streams are released, even on Close.
internal sealed class PdfSession : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PdfDocument? _document;
    private IRandomAccessStream? _input;
    private FileStream? _file;
    private int _disposed;
    internal int Count { get; private set; }

    internal static bool IsPdfFile(string path) =>
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    internal static Task<PdfSession> OpenAsync(string path, CancellationToken cancellation) =>
        Task.Run(() => OpenCoreAsync(path, cancellation), cancellation);

    private static async Task<PdfSession> OpenCoreAsync(string path, CancellationToken cancellation)
    {
        var session = new PdfSession();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            session._file = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete, 4096,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            cancellation.ThrowIfCancellationRequested();
            session._input = session._file.AsRandomAccessStream();
            session._document = await PdfDocument.LoadFromStreamAsync(session._input);
            cancellation.ThrowIfCancellationRequested();
            session.Count = checked((int)session._document.PageCount);
            if (session.Count == 0) throw new InvalidDataException(L10n.Get("Pdf_Empty"));
            return session;
        }
        catch (Exception ex)
        {
            session.Dispose();
            if (ex.HResult == unchecked((int)0x8007052B))
                throw new IOException(L10n.Get("Pdf_Password"), ex);
            throw;
        }
    }

    internal Task<bool> IsPortraitAsync(int index, CancellationToken cancellation) =>
        Task.Run(() => IsPortraitCoreAsync(index, cancellation), cancellation);

    private async Task<bool> IsPortraitCoreAsync(int index, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            ThrowIfUnavailable(cancellation);
            using var page = _document!.GetPage(checked((uint)index));
            return page.Size.Height >= page.Size.Width;
        }
        finally { FinishOperation(); }
    }

    internal Task<IRandomAccessStream> RenderAsync(int index, double maxWidth,
        double maxHeight, CancellationToken cancellation) =>
        Task.Run(() => RenderCoreAsync(index, maxWidth, maxHeight, cancellation), cancellation);

    private async Task<IRandomAccessStream> RenderCoreAsync(int index, double maxWidth,
        double maxHeight, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            ThrowIfUnavailable(cancellation);
            using var page = _document!.GetPage(checked((uint)index));
            var size = page.Size; // Includes CropBox and PDF rotation; do not rotate again.
            var (width, height) = RenderSize(size.Width, size.Height, maxWidth, maxHeight);
            var output = new InMemoryRandomAccessStream();
            try
            {
                await page.RenderToStreamAsync(output, new PdfPageRenderOptions
                {
                    DestinationWidth = width, DestinationHeight = height,
                    BitmapEncoderId = BitmapEncoder.PngEncoderId,
                    BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
                });
                ThrowIfUnavailable(cancellation);
                output.Seek(0);
                return output;
            }
            catch { output.Dispose(); throw; }
        }
        finally { FinishOperation(); }
    }

    internal static (uint Width, uint Height) RenderSize(double pageWidth, double pageHeight,
        double maxWidth, double maxHeight)
    {
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0)
            throw new InvalidDataException(L10n.Get("Pdf_InvalidPage"));
        maxWidth = double.IsFinite(maxWidth) && maxWidth > 0 ? Math.Clamp(maxWidth, 1, 4096) : 1600;
        maxHeight = double.IsFinite(maxHeight) && maxHeight > 0 ? Math.Clamp(maxHeight, 1, 4096) : 2400;
        double scale = Math.Min(maxWidth / pageWidth, maxHeight / pageHeight);
        return ((uint)Math.Max(1, Math.Floor(pageWidth * scale)),
            (uint)Math.Max(1, Math.Floor(pageHeight * scale)));
    }

    internal static async Task<IRandomAccessStream> OpenCoverAsync(string path, CancellationToken cancellation)
    {
        using var session = await OpenAsync(path, cancellation);
        return await session.RenderAsync(0, 300, 400, cancellation);
    }

    private void ThrowIfUnavailable(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void FinishOperation()
    {
        _gate.Release();
        if (Volatile.Read(ref _disposed) != 0) TryReleaseDocument();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        TryReleaseDocument();
    }

    private void TryReleaseDocument()
    {
        if (_gate.Wait(0))
        {
            try { ReleaseDocument(); }
            finally { _gate.Release(); }
        }
    }

    private void ReleaseDocument()
    {
        // PdfDocument has no IDisposable API. Release our reference and close the
        // owned input after outstanding native operations, without forcing a GC.
        _document = null;
        _input?.Dispose();
        _input = null;
        _file?.Dispose();
        _file = null;
    }
}
