using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lilium;

// Cache files only. No recursion and no writes to original image files.
internal sealed class ThumbnailCacheStore : IDisposable
{
    internal const string CurrentPrefix = "q70-300x400-";
    private static readonly Regex Owned = new(@"\Aq70-300x400-[0-9A-F]{64}(?:\.jpg|\.[0-9a-f]{32}\.tmp)\z", RegexOptions.IgnoreCase);
    private readonly string _directory;
    private readonly string _lockPath;
    private readonly Func<long> _limit;
    private readonly ConcurrentDictionary<string, DateTime> _touches = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _maintenance = new(1, 1);
    private int _dirty = 1;
    private readonly Timer _timer;
    internal string? LastError { get; private set; }

    internal ThumbnailCacheStore(string directory, Func<long> limit, bool automatic = true)
    {
        _directory = Path.GetFullPath(directory);
        _lockPath = Path.Combine(Path.GetDirectoryName(_directory)!, "thumbnail-cache.lock");
        _limit = limit;
        _timer = new Timer(_ => { _ = MaintainAsync(); }, null,
            automatic ? TimeSpan.FromSeconds(5) : Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(30));
    }
    public void Dispose() => _timer.Dispose();

    private void CheckDirectory()
    {
        Directory.CreateDirectory(_directory);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(L10n.Get("ThumbnailCacheStore_001"));
    }

    internal async Task<FileStream> LockAsync(CancellationToken cancellation = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            CheckDirectory();
            if (File.Exists(_lockPath) && (File.GetAttributes(_lockPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L10n.Get("ThumbnailCacheStore_002"));
            try { return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 1200) { await Task.Delay(25, cancellation); }
        }
    }

    internal void Touch(string path) => _touches[path] = DateTime.UtcNow;
    internal void Generated() => Interlocked.Exchange(ref _dirty, 1);
    internal static string CurrentKey(string sourcePath, long lastWriteTimeUtcTicks, long length) =>
        CurrentPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"fant-jpeg70-fit300x400-v1|{sourcePath}|{lastWriteTimeUtcTicks}|{length}")));
    internal static bool Owns(string name) => Owned.IsMatch(name);
    internal static long CleanupTarget(long limit) => limit * 9 / 10;

    // File LastWriteTime is explicitly maintained by Lilium as its approximate last-use time.
    // Do not rely on NTFS LastAccessTime. Batch updates instead of writing on each display.
    private void FlushTouches()
    {
        foreach (var key in _touches.Keys)
            if (_touches.TryRemove(key, out var used))
            {
                try
                {
                    if (File.Exists(key) && (File.GetAttributes(key) & FileAttributes.ReparsePoint) == 0 && File.GetLastWriteTimeUtc(key) < used)
                        File.SetLastWriteTimeUtc(key, used);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
    }

    private FileInfo[] Files() => new DirectoryInfo(_directory).EnumerateFiles()
        .Where(f => Owns(f.Name) && (f.Attributes & FileAttributes.ReparsePoint) == 0).ToArray();

    internal async Task MaintainAsync(bool force = false)
    {
        await _maintenance.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                using var lease = await LockAsync();
                FlushTouches();
                if (!force && Interlocked.Exchange(ref _dirty, 0) == 0) return;
                var files = Files();
                long total = files.Sum(f => f.Length), limit = _limit();
                if (total <= limit) { LastError = null; return; }
                var target = CleanupTarget(limit);
                foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= target) break;
                    try
                    {
                        if ((File.GetAttributes(file.FullName) & FileAttributes.ReparsePoint) != 0) continue;
                        var length = file.Length;
                        File.Delete(file.FullName);
                        total -= length;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                LastError = total > target ? L10n.Get("ThumbnailCacheStore_003") : null;
                if (total > target) Generated();
            });
        }
        catch (Exception ex) { LastError = ex.Message; Generated(); }
        finally { _maintenance.Release(); }
    }

    internal async Task<(int Deleted, int Failed)> ClearAsync()
    {
        return await Task.Run(async () =>
        {
            using var lease = await LockAsync();
            _touches.Clear();
            int deleted = 0, failed = 0;
            foreach (var file in Files())
            {
                try { File.Delete(file.FullName); deleted++; }
                catch (IOException) { failed++; }
                catch (UnauthorizedAccessException) { failed++; }
            }
            Generated();
            return (deleted, failed);
        });
    }
}
