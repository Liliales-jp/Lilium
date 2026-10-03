namespace Lilium;

internal static class FolderCoverSearch
{
    // The folder's own files are depth 0; grandchildren's files are depth 2.
    internal const int MaxDepth = 2;
    internal const int MaxFolders = 256;
    private const FileAttributes Excluded = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;

    internal static string? FindFirstFile(string folder, Func<string, bool> isSupported,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Do not follow a junction even when it is the folder shown in the list.
            if ((File.GetAttributes(folder) & Excluded) != 0) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }

        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((folder, 0));
        int scheduled = 1;
        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = FolderListingReader.Read(current.Path, cancellationToken);
            // A failed child must not stop the search of its siblings. Partial reads
            // still offer the entries that were accessible.
            var entries = result.Entries.Where(entry =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (entry.Attributes & Excluded) == 0;
            }).ToArray();
            var first = entries.Where(entry =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return !entry.IsFolder && isSupported(entry.Path);
            }).OrderBy(entry => entry.Name, NaturalComparer.Instance).FirstOrDefault();
            cancellationToken.ThrowIfCancellationRequested();
            if (first is not null) return first.Path;
            if (current.Depth >= MaxDepth || scheduled >= MaxFolders) continue;

            foreach (var child in entries.Where(entry => entry.IsFolder)
                .OrderBy(entry => entry.Name, NaturalComparer.Instance))
            {
                cancellationToken.ThrowIfCancellationRequested();
                pending.Enqueue((child.Path, current.Depth + 1));
                if (++scheduled >= MaxFolders) break;
            }
        }
        return null;
    }
}
