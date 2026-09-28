using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Lilium;

// Configure once at startup, before creating any application windows or workers.
// Saving a preference never changes the language of a running session.
internal static class L10n
{
    internal const string SettingKey = "language";
    private static readonly FrozenDictionary<string, string> Japanese = Load("ja");
    private static readonly FrozenDictionary<string, string> English = Load("en");
    internal static string Language { get; private set; } = DefaultLanguage;
    internal static string DefaultLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";
    internal static bool IsSupported(string? language) => language is "ja" or "en";
    internal static void Initialize(string? language) => Language = IsSupported(language) ? language! : DefaultLanguage;
    internal static IEnumerable<KeyValuePair<string, string>> UiResources => Japanese.Keys
        .Where(key => key.StartsWith("Ui_", StringComparison.Ordinal)).Select(key => KeyValuePair.Create(key, Get(key)));
    internal static string HelpFileName => Language == "en" ? "Help.en.txt" : "Help.txt";
    internal static string Get(string key)
    {
        if (Language == "en" && English.TryGetValue(key, out var translated)) return translated;
        return Japanese.TryGetValue(key, out var fallback) ? fallback : key;
    }
    internal static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.GetCultureInfo(Language == "ja" ? "ja-JP" : "en-US"), Get(key), args);
    private static FrozenDictionary<string, string> Load(string language)
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream($"Lilium.Strings.{language}.json")
            ?? throw new InvalidOperationException($"Missing language resource: {language}");
        return (JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException($"Empty language resource: {language}")).ToFrozenDictionary(StringComparer.Ordinal);
    }
}
