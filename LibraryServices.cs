using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Lilium;

public static class FileCatalog
{
    private static readonly SemaphoreSlim ThumbnailGate = new(1, 1);
    private static int _cacheGeneration;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };
    private static readonly Regex RatingPattern = new(@"\{zpi\$r=([1-5])\}", RegexOptions.Compiled);
    public static TreeViewNode CreateFolderNode(string path, string name) { var node = new TreeViewNode { Content = new FolderNode(name, path, true) }; node.Children.Add(new TreeViewNode { Content = new FolderNode(L10n.Get("LibraryServices_001"), "", false, true) }); return node; }
    public static FolderReadResult ExpandTreeNode(TreeViewNode treeNode, FolderNode folder)
    {
        if (!folder.CanExpand || treeNode.Children.Count != 1 || treeNode.Children[0].Content is not FolderNode { IsPlaceholder: true })
            return new FolderReadResult([], FolderReadStatus.Success);
        var result = ReadDirectories(folder.Path);
        if (!result.IsSuccess) return result;
        treeNode.Children.Clear();
        foreach (var child in result.Entries) treeNode.Children.Add(CreateFolderNode(child.Path, child.Name));
        return result;
    }
    public static FolderReadResult ReadDirectories(string path)
    {
        var result = FolderListingReader.ReadDirectories(path);
        if (!result.IsSuccess) return result;
        return result with
        {
            Entries = result.Entries.Where(entry => !IsHiddenOrSystem(entry.Attributes))
                .OrderBy(entry => entry.Name, NaturalComparer.Instance).ToArray()
        };
    }
    public static async Task<LibraryFolderReadResult> ReadFolderAsync(string folder, string size,
        CancellationToken cancellationToken = default) =>
        await Task.Run(() => ReadFolder(folder, size, cancellationToken), cancellationToken);

    public static async Task<LibraryFolderReadResult> ReadDescendantFoldersAsync(string root, string size,
        CancellationToken cancellationToken = default) =>
        await Task.Run(() => ReadDescendantFolders(root, size, cancellationToken), cancellationToken);

    private static LibraryFolderReadResult ReadDescendantFolders(string root, string size, CancellationToken cancellationToken)
    {
        var items = new List<LibraryItem>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directories = FolderListingReader.ReadDirectories(path, cancellationToken);
            if (!directories.IsSuccess)
                return new LibraryFolderReadResult(items, FolderReadStatus.Partial, directories.Error, path);

            foreach (var child in directories.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsHiddenOrSystem(child.Attributes) ||
                    (child.Attributes & System.IO.FileAttributes.ReparsePoint) != 0) continue;
                pending.Push(child.Path);
                var read = ReadFolder(child.Path, size, cancellationToken);
                if (!read.IsSuccess)
                    return new LibraryFolderReadResult(items, FolderReadStatus.Partial, read.Error, child.Path);
                items.AddRange(read.Items);
            }
        }
        return new LibraryFolderReadResult(items, FolderReadStatus.Success);
    }

    private static LibraryFolderReadResult ReadFolder(string folder, string size, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (width, height) = size switch { "S" => (200, 240), "L" => (300, 400), _ => (240, 320) };
        var result = FolderListingReader.Read(folder, cancellationToken);
        var items = new List<LibraryItem>();
        foreach (var entry in result.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsHiddenOrSystem(entry.Attributes)) continue;
            if (entry.IsFolder)
            {
                items.Add(new LibraryItem { Path = entry.Path, DisplayName = entry.Name, IsFolder = true, Modified = entry.Modified, Rating = GetRating(entry.Name), ThumbnailSourcePath = FolderCoverSource(entry.Path, cancellationToken), CardWidth = width, CardHeight = height + 32 });
            }
            else
            {
                items.Add(new LibraryItem { Path = entry.Path, DisplayName = entry.Name, IsFolder = false, Modified = entry.Modified, Rating = GetRating(entry.Name), ThumbnailSourcePath = ThumbnailSource(entry.Path), CardWidth = width, CardHeight = height + 32 });
            }
        }
        return new LibraryFolderReadResult(items, result.Status, result.Error);
    }
    public static List<LibraryItem> FilterAndSort(IEnumerable<LibraryItem> source, int minimumFolderRating, int minimumFileRating,
        string? keyword, string sort, string fileSort, CancellationToken cancellationToken = default)
    {
        var result = source.Where(i =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return i.Rating >= (i.IsFolder ? minimumFolderRating : minimumFileRating) &&
                (string.IsNullOrWhiteSpace(keyword) || i.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        });
        var cancellableNatural = new CancellationComparer<string>(NaturalComparer.Instance, cancellationToken);
        return result.OrderBy(i => i.IsFolder ? 0 : 1, new CancellationComparer<int>(Comparer<int>.Default, cancellationToken))
            .ThenByDescending(i => (i.IsFolder ? sort : fileSort) == "modified" ? i.Modified : DateTime.MinValue,
                new CancellationComparer<DateTime>(Comparer<DateTime>.Default, cancellationToken))
            .ThenBy(i => i.DisplayName, cancellableNatural).ToList();
    }
    internal static int CacheGeneration => Volatile.Read(ref _cacheGeneration);
    internal static async Task<BitmapImage> LoadThumbnailAsync(string sourcePath, int generation, CancellationToken cancellation)
    {
        await ThumbnailGate.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var cacheLease = await AppServices.ThumbnailCache.LockAsync(cancellation);
            if (generation != CacheGeneration) throw new OperationCanceledException();
            var path = await GetThumbnailPathAsync(sourcePath, cancellation);
            // A decode already in progress may finish, but no subsequent image is started.
            cancellation.ThrowIfCancellationRequested();
            if (generation != CacheGeneration) throw new OperationCanceledException();
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            // Stream loading avoids retaining images in the URI-based image cache.
            await bitmap.SetSourceAsync(stream);
            AppServices.ThumbnailCache.Touch(path);
            cancellation.ThrowIfCancellationRequested();
            if (generation != CacheGeneration) throw new OperationCanceledException();
            return bitmap;
        }
        finally { ThumbnailGate.Release(); }
    }
    public static async Task<(int Deleted, int Failed)> ClearThumbnailCacheAsync()
    {
        // Stop pre-existing fill loops, including those in other Lilium windows.
        Interlocked.Increment(ref _cacheGeneration);
        await ThumbnailGate.WaitAsync();
        try
        {
            return await AppServices.ThumbnailCache.ClearAsync();
        }
        finally
        {
            // Also retire fill loops queued while deletion was in progress.
            Interlocked.Increment(ref _cacheGeneration);
            ThumbnailGate.Release();
        }
    }
    internal static async Task ClearFolderThumbnailsAsync(string folder)
    {
        Interlocked.Increment(ref _cacheGeneration);
        await ThumbnailGate.WaitAsync();
        try
        {
            using var lease = await AppServices.ThumbnailCache.LockAsync();
            await Task.Run(() =>
            {
                foreach (var sourcePath in Directory.EnumerateFiles(folder).Where(p => ImageExtensions.Contains(Path.GetExtension(p)) || PdfSession.IsPdfFile(p)))
                {
                    var info = new FileInfo(sourcePath);
                    if ((info.Attributes & System.IO.FileAttributes.ReparsePoint) != 0) continue;
                    var name = ThumbnailCacheStore.CurrentKey(sourcePath, info.LastWriteTimeUtc.Ticks, info.Length) + ".jpg";
                    var cache = Path.Combine(AppServices.CachePath, name);
                    if (File.Exists(cache) && (File.GetAttributes(cache) & System.IO.FileAttributes.ReparsePoint) == 0) File.Delete(cache);
                }
            });
        }
        finally { Interlocked.Increment(ref _cacheGeneration); ThumbnailGate.Release(); }
    }
    private static async Task<string> GetThumbnailPathAsync(string sourcePath, CancellationToken cancellation)
    {
        ArchiveLocation.TryParse(sourcePath, out var archiveLocation);
        var sourceInfo = new FileInfo(archiveLocation?.ArchivePath ?? sourcePath);
        var key = ThumbnailCacheStore.CurrentKey(sourcePath, sourceInfo.LastWriteTimeUtc.Ticks, sourceInfo.Length);
        var outputPath = Path.Combine(AppServices.CachePath, key + ".jpg");
        if (File.Exists(outputPath)) return outputPath;
        using var input = archiveLocation is not null
            ? await ArchiveCatalog.OpenImageAsync(archiveLocation, cancellation)
            : PdfSession.IsPdfFile(sourcePath) ? await PdfSession.OpenCoverAsync(sourcePath, cancellation)
            : await (await StorageFile.GetFileFromPathAsync(sourcePath)).OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(input);
        // Fit within the shared L-size cache, without upscaling originals.
        var scale = Math.Min(1, Math.Min(300.0 / decoder.OrientedPixelWidth, 400.0 / decoder.OrientedPixelHeight));
        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant, ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale), ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale) };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var data = pixels.DetachPixelData();
        // Composite premultiplied RGBA over white; JPEG has no alpha channel.
        for (var i = 0; i < data.Length; i += 4)
        {
            var white = 255 - data[i + 3];
            data[i] = (byte)Math.Min(255, data[i] + white);
            data[i + 1] = (byte)Math.Min(255, data[i + 1] + white);
            data[i + 2] = (byte)Math.Min(255, data[i + 2] + white);
            data[i + 3] = 255;
        }
        var cacheFolder = await StorageFolder.GetFolderFromPathAsync(AppServices.CachePath);
        var temporaryPath = Path.Combine(AppServices.CachePath, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var output = await cacheFolder.CreateFileAsync(Path.GetFileName(temporaryPath), CreationCollisionOption.FailIfExists);
            using (var outputStream = await output.OpenAsync(FileAccessMode.ReadWrite))
            {
                bool rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
                var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.70f, Windows.Foundation.PropertyType.Single) };
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream, options);
                encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, rotated ? transform.ScaledHeight : transform.ScaledWidth, rotated ? transform.ScaledWidth : transform.ScaledHeight, 96, 96, data);
                await encoder.FlushAsync();
            }
            // Only publish complete images. Another process may have generated the same key.
            try { File.Move(temporaryPath, outputPath); }
            catch (IOException) when (File.Exists(outputPath)) { }
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        AppServices.ThumbnailCache.Generated();
        return outputPath;
    }
    public static string RatingTarget(string path, bool isFolder, int rating)
    {
        var parent = Directory.GetParent(path)?.FullName ?? throw new IOException(L10n.Get("LibraryServices_002")); var extension = isFolder ? "" : Path.GetExtension(path); var stem = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        stem = RatingPattern.Replace(stem, "").TrimEnd(' '); var target = Path.Combine(parent, rating == 0 ? stem + extension : $"{stem} {{zpi$r={rating}}}{extension}");
        if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target))) throw new IOException(L10n.Get("LibraryServices_003"));
        return target;
    }
    private static bool IsThumbnailFile(string path) => ImageExtensions.Contains(Path.GetExtension(path)) ||
        ArchiveLocation.IsArchiveFile(path) || PdfSession.IsPdfFile(path);

    private static string ThumbnailSource(string path) => ArchiveLocation.IsArchiveFile(path)
        ? ArchiveLocation.Cover(path) : IsThumbnailFile(path) ? path : "";

    private static string FolderCoverSource(string folder, CancellationToken cancellationToken)
    {
        var file = FolderCoverSearch.FindFirstFile(folder, IsThumbnailFile, cancellationToken);
        return file is null ? "" : ThumbnailSource(file);
    }

    private sealed class CancellationComparer<T>(IComparer<T> inner, CancellationToken cancellationToken) : IComparer<T>
    {
        public int Compare(T? x, T? y)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.Compare(x!, y!);
        }
    }
    private static int GetRating(string name) => RatingPattern.Match(name) is { Success: true } match ? int.Parse(match.Groups[1].Value) : 0;
    private static bool IsHiddenOrSystem(System.IO.FileAttributes attributes) => (attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) != 0;
}
