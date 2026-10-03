namespace Lilium;

// A reader owns its source. Fullscreen handoff copies this description, never a live PDF session.
internal sealed record ReaderDocument(string[] Images, string? PdfPath = null)
{
    internal ViewerSource CreateSource() => PdfPath is null
        ? new ImageViewerSource(Images) : new PdfViewerSource(PdfPath);
}
