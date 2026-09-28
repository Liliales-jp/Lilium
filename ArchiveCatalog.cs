using System.IO.Compression;
using System.Text;
using Windows.Storage.Streams;

namespace Lilium;

internal enum ArchiveItemKind { Folder, Image, Cover, Other }
internal sealed class ArchivePasswordException() : IOException(L10n.Get("Archive_Password"));
internal sealed record ArchiveLocation(string ArchivePath, string EntryName, ArchiveItemKind Kind)
{
    private const string Prefix = "lilium-archive:";

    internal static bool IsArchiveFile(string path) =>
        Path.GetExtension(path) is { } ext &&
        (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cbz", StringComparison.OrdinalIgnoreCase));

    internal static string Folder(string archivePath, string prefix) => Encode(archivePath, prefix, ArchiveItemKind.Folder);
    internal static string Image(string archivePath, string name) => Encode(archivePath, name, ArchiveItemKind.Image);
    internal static string Cover(string archivePath) => Encode(archivePath, "", ArchiveItemKind.Cover);
    internal static string Other(string archivePath, string name) => Encode(archivePath, name, ArchiveItemKind.Other);
    private static string Encode(string path, string name, ArchiveItemKind kind) =>
        Prefix + (int)kind + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(path)) + ":" +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(name));

    internal static bool IsVirtual(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    internal static bool TryParse(string? value, out ArchiveLocation? location)
    {
        location = null;
        if (!IsVirtual(value)) return false;
        try
        {
            var parts = value![Prefix.Length..].Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[0], out int kind) || kind is < 0 or > 3) return false;
            location = new ArchiveLocation(
                Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])),
                Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])), (ArchiveItemKind)kind);
            return true;
        }
        catch (FormatException) { return false; }
    }

    internal string DisplayPath => ArchivePath + (EntryName.Length == 0 ? "" : " / " + EntryName.TrimEnd('/'));
    internal string Parent
    {
        get
        {
            if (EntryName.Length == 0) return Path.GetDirectoryName(ArchivePath) ?? ArchivePath;
            int slash = EntryName.TrimEnd('/').LastIndexOf('/');
            return Folder(ArchivePath, slash < 0 ? "" : EntryName[..(slash + 1)]);
        }
    }

    internal string? FocusPathWhenReturningTo(string destination)
    {
        if (Kind != ArchiveItemKind.Folder ||
            !string.Equals(Parent, destination, EntryName.Length == 0
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        return EntryName.Length == 0 ? ArchivePath : Folder(ArchivePath, EntryName);
    }
}

internal static class ArchiveCatalog
{
    private const int MaxEntries = 50000;
    private const long MaxImageBytes = 256L * 1024 * 1024;
    internal static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };

    private static string Normalize(string name)
    {
        name = name.Replace('\\', '/');
        if (name.Length == 0 || name[0] == '/' || name.Contains('\0') ||
            name.Split('/').Any(p => p is "." or ".." || p.Contains(':')))
            throw new InvalidDataException(L10n.Get("Archive_InvalidEntry"));
        return name;
    }

    private static List<(string Name, ZipArchiveEntry Entry)> Images(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException(L10n.Get("Archive_TooManyEntries"));
        var images = new List<(string Name, ZipArchiveEntry Entry)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var name = Normalize(entry.FullName);
            if (name.EndsWith('/')) continue;
            if (!ImageExtensions.Contains(Path.GetExtension(name))) continue;
            if (entry.IsEncrypted) throw new ArchivePasswordException();
            if (!names.Add(name)) throw new InvalidDataException(L10n.Get("Archive_InvalidEntry"));
            images.Add((name, entry));
        }
        images.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        return images;
    }

    internal static async Task<LibraryFolderReadResult> ReadFolderAsync(
        ArchiveLocation location, string size, bool recursive, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => ReadFolder(location, size, recursive, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FileNotFoundException ex) { return new([], FolderReadStatus.NotFound, ex); }
        catch (UnauthorizedAccessException ex) { return new([], FolderReadStatus.AccessDenied, ex); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        { return new([], FolderReadStatus.Failed, ex); }
    }

    private static LibraryFolderReadResult ReadFolder(
        ArchiveLocation location, string size, bool recursive, CancellationToken cancellationToken)
    {
        if (location.Kind != ArchiveItemKind.Folder) throw new InvalidDataException(L10n.Get("Archive_InvalidEntry"));
        using var zip = ZipFile.OpenRead(location.ArchivePath);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException(L10n.Get("Archive_TooManyEntries"));
        var entries = zip.Entries.Select(entry => (Name: Normalize(entry.FullName), Entry: entry)).ToList();
        var images = Images(zip);
        var firstImageByFolder = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _) in images)
        {
            for (int slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                firstImageByFolder.TryAdd(name[..(slash + 1)], name);
        }
        var (width, height) = size switch { "S" => (200, 240), "L" => (300, 400), _ => (240, 320) };
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<LibraryItem>();
        foreach (var (name, entry) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!name.StartsWith(location.EntryName, StringComparison.Ordinal)) continue;
            var rest = name[location.EntryName.Length..];
            int slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                int searchFrom = 0;
                do
                {
                    var prefix = location.EntryName + rest[..(slash + 1)];
                    if (folders.Add(prefix))
                        items.Add(new LibraryItem
                        {
                            Path = ArchiveLocation.Folder(location.ArchivePath, prefix),
                            DisplayName = rest[searchFrom..slash], IsFolder = true,
                            Modified = entry.LastWriteTime.LocalDateTime, Rating = 0,
                            ThumbnailSourcePath = firstImageByFolder.TryGetValue(prefix, out var firstImage)
                                ? ArchiveLocation.Image(location.ArchivePath, firstImage) : "",
                            CardWidth = width, CardHeight = height + 32
                        });
                    if (!recursive) break;
                    searchFrom = slash + 1;
                    slash = rest.IndexOf('/', searchFrom);
                } while (slash >= 0);
            }
            if (name.EndsWith('/') || (slash >= 0 && !recursive)) continue;
            bool isImage = ImageExtensions.Contains(Path.GetExtension(name));
            items.Add(new LibraryItem
            {
                Path = isImage ? ArchiveLocation.Image(location.ArchivePath, name)
                    : ArchiveLocation.Other(location.ArchivePath, name),
                DisplayName = name[(name.LastIndexOf('/') + 1)..], IsFolder = false,
                Modified = entry.LastWriteTime.LocalDateTime, Rating = 0,
                ThumbnailSourcePath = isImage ? ArchiveLocation.Image(location.ArchivePath, name) : "",
                CardWidth = width, CardHeight = height + 32
            });
        }
        return new(items, FolderReadStatus.Success);
    }

    internal static async Task<IRandomAccessStream> OpenImageAsync(ArchiveLocation location, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(async () =>
        {
            using var zip = ZipFile.OpenRead(location.ArchivePath);
            var chosen = location.Kind == ArchiveItemKind.Cover
                ? Images(zip).FirstOrDefault().Entry
                : zip.GetEntry(location.EntryName) ?? zip.Entries.FirstOrDefault(
                    entry => Normalize(entry.FullName) == location.EntryName);
            if (chosen is null) throw new FileNotFoundException(L10n.Get("Archive_ImageMissing"));
            if (chosen.IsEncrypted) throw new ArchivePasswordException();
            if (chosen.Length > MaxImageBytes) throw new InvalidDataException(L10n.Get("Archive_ImageTooLarge"));
            await using var input = chosen.Open();
            var output = new InMemoryRandomAccessStream();
            try
            {
                using (var writer = new DataWriter(output))
                {
                    var chunk = new byte[64 * 1024];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(chunk, cancellationToken)) != 0)
                    {
                        total += read;
                        if (total > MaxImageBytes) throw new InvalidDataException(L10n.Get("Archive_ImageTooLarge"));
                        writer.WriteBytes(read == chunk.Length ? chunk : chunk[..read]);
                        await writer.StoreAsync();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    writer.DetachStream();
                }
                output.Seek(0);
                return output;
            }
            catch { output.Dispose(); throw; }
        }, cancellationToken);
    }
}
