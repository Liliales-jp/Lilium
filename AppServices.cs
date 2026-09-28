namespace Lilium;

public static class AppServices
{
    public static string RootPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lilium");
    public static string CachePath { get; } = Path.Combine(RootPath, "cache");
    public static SettingsStore Store { get; private set; } = null!;
    internal static FileOperationJournal Operations { get; private set; } = null!;
    internal static ThumbnailCacheStore ThumbnailCache { get; private set; } = null!;
    internal static InputBindingService Inputs { get; private set; } = null!;
    internal static int CacheLimitMb => int.TryParse(Store.Get("cache_limit_mb", "5000"), out var value) ? Math.Clamp(value, 500, 5000) : 5000;

    public static void Initialize()
    {
        Directory.CreateDirectory(CachePath);
        var store = new SettingsStore(Path.Combine(RootPath, "lilium.db"));
        L10n.Initialize(store.Get(L10n.SettingKey, ""));
        ThumbnailCacheStore? thumbnailCache = null;
        try
        {
            thumbnailCache = new ThumbnailCacheStore(CachePath, () =>
                (int.TryParse(store.Get("cache_limit_mb", "5000"), out var value) ? Math.Clamp(value, 500, 5000) : 5000) * 1_000_000L);
            var operations = new FileOperationJournal(Path.Combine(RootPath, "operations"), new ShellRecycle(), RootPath,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), AppContext.BaseDirectory);
            Store = store;
            Inputs = new InputBindingService(() => store.Get(InputBindingService.SettingKey, ""),
                value => store.Set(InputBindingService.SettingKey, value));
            if (Inputs.LoadError is { } error) App.WriteDiagnosticLog("Key binding load failed", new InvalidDataException(error));
            ThumbnailCache = thumbnailCache;
            Operations = operations;
        }
        catch
        {
            thumbnailCache?.Dispose();
            throw;
        }
    }
}
