using System.Text.Json;

namespace Lilium;

[Flags]
internal enum KeyModifiers { None = 0, Control = 1, Shift = 2, Alt = 4, Windows = 8 }
internal sealed record InputGesture(int Key = 0, KeyModifiers Modifiers = KeyModifiers.None, string? Mouse = null);
internal sealed record InputAction(string Id, int Scopes);
internal sealed record BindingIssue(string Action, int Index, string Reason, string? Other = null);

// No UI types: validation, conflict scopes and persistence use the same rules as dispatch.
internal static class BindingRules
{
    internal const int Library = 1, RightReader = 2, LeftReader = 4;
    internal static readonly InputAction[] Actions = [
        new("Open", 1), new("Back", 1), new("Forward", 1), new("Up", 1), new("Rating0", 1), new("Rating1", 1), new("Rating2", 1),
        new("Rating3", 1), new("Rating4", 1), new("Rating5", 1), new("CloseReader", 6),
        new("NextRight", 2), new("PreviousRight", 2), new("NextLeft", 4), new("PreviousLeft", 4),
        new("StepNext", 6), new("StepPrevious", 6), new("SinglePage", 6), new("TwoPages", 6)
    ];
    internal static readonly string[] MouseOptions = ["Left", "Right", "Middle", "Back", "Forward", "WheelUp", "WheelDown"];
    internal static string[] MiceFor(string action) => Scope(action) == Library ? ["Middle", "Back", "Forward"] : MouseOptions;
    internal static int Scope(string action) => Actions.First(a => a.Id == action).Scopes;
    internal static Dictionary<string, List<InputGesture>> Defaults()
    {
        var result = Actions.ToDictionary(a => a.Id, _ => new List<InputGesture>());
        void Keys(string id, params int[] keys) => result[id].AddRange(keys.Select(k => new InputGesture(k)));
        void Mice(string id, params string[] mice) => result[id].AddRange(mice.Select(m => new InputGesture(Mouse: m)));
        Keys("Open", 13, 32); Keys("Back", 8); Mice("Back", "Back"); Mice("Forward", "Forward");
        for (int i = 0; i <= 5; i++) Keys("Rating" + i, 48 + i);
        Keys("CloseReader", 8); Mice("CloseReader", "Back");
        Keys("NextRight", 37, 65, 32); Mice("NextRight", "Left", "WheelDown");
        Keys("PreviousRight", 39, 68); Mice("PreviousRight", "Right", "WheelUp");
        Keys("NextLeft", 39, 68, 32); Mice("NextLeft", "Right", "WheelDown");
        Keys("PreviousLeft", 37, 65); Mice("PreviousLeft", "Left", "WheelUp");
        Keys("StepNext", 40); Keys("StepPrevious", 38); Keys("SinglePage", 49); Keys("TwoPages", 50);
        return result;
    }
    internal static bool SupportedKey(int key) => key is >= 65 and <= 90 or >= 48 and <= 57
        or >= 112 and <= 123 or >= 96 and <= 105 or 106 or 107 or 109 or 110 or 111
        or 8 or 13 or 27 or 32 or >= 33 and <= 40 or 45 or 46;
    internal static string? InvalidReason(string action, InputGesture g)
    {
        if (g.Mouse is not null)
            return g.Key != 0 || g.Modifiers != 0 || !MiceFor(action).Contains(g.Mouse) ? "Unsupported" : null;
        if (!SupportedKey(g.Key) || (g.Modifiers & ~(KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)) != 0) return "Unsupported";
        var m = g.Modifiers;
        if ((m.HasFlag(KeyModifiers.Alt) && g.Key is 27 or 32 or 115) ||
            (m.HasFlag(KeyModifiers.Control) && g.Key == 27) ||
            (m.HasFlag(KeyModifiers.Control) && m.HasFlag(KeyModifiers.Alt) && g.Key == 46)) return "Reserved";
        if (Scope(action) != Library) return null;
        // Preserve focus/selection movement, including Ctrl/Shift selection gestures.
        if (g.Key is >= 33 and <= 40) return "Reserved";
        if (m == 0 && g.Key is 27 or 46 or 113 or 116) return "Reserved";
        if (m == KeyModifiers.Control && g.Key is 65 or 67 or 86 or 88 or 90) return "Reserved";
        if (m == (KeyModifiers.Control | KeyModifiers.Shift) && g.Key == 78) return "Reserved";
        return null;
    }
    internal static List<BindingIssue> Validate(IReadOnlyDictionary<string, List<InputGesture>> bindings)
    {
        var errors = new List<BindingIssue>();
        foreach (var action in Actions)
        {
            if (!bindings.TryGetValue(action.Id, out var values) || values is null)
            { errors.Add(new(action.Id, -1, "Invalid")); continue; }
            for (int i = 0; i < values.Count; i++)
            {
                var gesture = values[i];
                if (gesture is null) { errors.Add(new(action.Id, i, "Invalid")); continue; }
                if (InvalidReason(action.Id, gesture) is { } reason) errors.Add(new(action.Id, i, reason));
                foreach (var other in Actions.Where(a => (a.Scopes & action.Scopes) != 0))
                    if (bindings.TryGetValue(other.Id, out var others) && others is not null &&
                        others.Where((_, index) => other.Id != action.Id || index != i).Contains(gesture))
                    { errors.Add(new(action.Id, i, "Conflict", other.Id)); break; }
            }
        }
        if (bindings.Keys.Any(key => !Actions.Any(a => a.Id == key))) errors.Add(new("Open", -1, "Invalid"));
        return errors;
    }
    internal static Dictionary<string, List<InputGesture>> Clone(IReadOnlyDictionary<string, List<InputGesture>> source) =>
        source.ToDictionary(p => p.Key, p => p.Value.ToList());
    internal static string Encode(Dictionary<string, List<InputGesture>> bindings) => JsonSerializer.Serialize(new BindingDocument(2, bindings));
    internal static Dictionary<string, List<InputGesture>> Decode(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        void NoDuplicates(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>();
                foreach (var field in element.EnumerateObject())
                { if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate binding field"); NoDuplicates(field.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) NoDuplicates(child);
        }
        NoDuplicates(parsed.RootElement);
        var doc = JsonSerializer.Deserialize<BindingDocument>(json);
        if (doc is null || doc.Version is < 1 or > 2 || doc.Bindings is null)
            throw new InvalidDataException("Invalid or unsupported key bindings");
        if (doc.Version == 1)
        {
            if (doc.Bindings.Count != Actions.Length - 2 ||
                doc.Bindings.ContainsKey("Back") || doc.Bindings.ContainsKey("Forward") ||
                !doc.Bindings.TryGetValue("Up", out var oldUp) || oldUp is null)
                throw new InvalidDataException("Invalid legacy key bindings");
            // In v1 these were the default Go up gestures. Transfer them to
            // history Back; retain any other custom Go up gestures.
            oldUp.RemoveAll(g => g is not null && (g == new InputGesture(8) || g == new InputGesture(Mouse: "Back")));
            bool TakenInLibrary(InputGesture gesture) => doc.Bindings
                .Where(entry => Actions.Any(a => a.Id == entry.Key && a.Scopes == Library))
                .SelectMany(entry => entry.Value ?? []).Contains(gesture);
            var back = new List<InputGesture>();
            foreach (var gesture in new[] { new InputGesture(8), new InputGesture(Mouse: "Back") })
                if (!TakenInLibrary(gesture)) back.Add(gesture);
            doc.Bindings["Back"] = back;
            var forward = new InputGesture(Mouse: "Forward");
            doc.Bindings["Forward"] = TakenInLibrary(forward) ? [] : [forward];
        }
        if (Validate(doc.Bindings).Count != 0)
            throw new InvalidDataException("Invalid or unsupported key bindings");
        return doc.Bindings;
    }
    private sealed record BindingDocument(int Version, Dictionary<string, List<InputGesture>> Bindings);
}

internal sealed class InputBindingService
{
    internal const string SettingKey = "input_bindings";
    private Dictionary<string, List<InputGesture>> _current = BindingRules.Defaults();
    internal string? LoadError { get; private set; }
    internal int Revision { get; private set; }
    private bool _loadNoticeShown;
    internal bool TakeLoadNotice()
    {
        if (LoadError is null || _loadNoticeShown) return false;
        _loadNoticeShown = true;
        return true;
    }
    private readonly Action<string> _save;
    internal InputBindingService(Func<string> read, Action<string> save)
    {
        _save = save;
        try { var value = read(); if (value.Length != 0) _current = BindingRules.Decode(value); }
        catch (Exception ex) { LoadError = ex.Message; }
    }
    internal Dictionary<string, List<InputGesture>> Snapshot() => BindingRules.Clone(_current);
    internal string? Match(InputGesture gesture, int scope) => BindingRules.Actions
        .FirstOrDefault(a => (a.Scopes & scope) != 0 && _current[a.Id].Contains(gesture))?.Id;
    internal void Apply(Dictionary<string, List<InputGesture>> draft)
    {
        if (BindingRules.Validate(draft).Count != 0) throw new InvalidDataException("Invalid key bindings");
        var next = BindingRules.Clone(draft);
        _save(BindingRules.Encode(next)); // Publish only after the SQLite commit succeeds.
        _current = next;
        LoadError = null;
        Revision++;
    }
}
