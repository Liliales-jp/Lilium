namespace Lilium;

internal static class PreviewRules
{
    internal const double DefaultWidth = 480, MinimumWidth = 320, ListMinimumWidth = 320, DividerWidth = 8;

    // On very narrow windows share the available space instead of overflowing either pane.
    internal static double FitWidth(double desired, double available)
    {
        double usable = Math.Max(0, available - DividerWidth);
        double maximum = Math.Max(0, usable - Math.Min(ListMinimumWidth, usable / 2));
        return Math.Clamp(double.IsFinite(desired) ? desired : DefaultWidth,
            Math.Min(MinimumWidth, maximum), maximum);
    }

    internal static string? DisabledInputReason(string action, InputGesture gesture,
        IReadOnlyDictionary<string, List<InputGesture>> bindings)
    {
        if (action == "CloseReader" || gesture.Mouse == "Back") return "Preview_CloseDisabled";
        // Pointer gestures have their own surface; list bindings cannot intercept them.
        if (gesture.Mouse is not null) return null;
        if (BindingRules.InvalidReason("Open", gesture) is not null || gesture.Key == 9)
            return "Preview_ReservedKey";
        return BindingRules.Actions.Where(a => a.Scopes == BindingRules.Library)
            .Any(a => bindings.TryGetValue(a.Id, out var values) && values.Contains(gesture))
                ? "Preview_ConflictingKey" : null;
    }
}
