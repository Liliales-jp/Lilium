namespace Lilium;

internal static class FileDropPolicy
{
    internal static bool ShouldMove(IReadOnlyList<string> sources, string destination,
        bool control, bool shift, bool alwaysMove)
    {
        // A selection normally has one source drive. If it contains several,
        // copy the whole selection rather than unexpectedly moving some files.
        var destinationRoot = Path.GetPathRoot(destination);
        bool sameDrive = !string.IsNullOrEmpty(destinationRoot) && sources.Count > 0 &&
            sources.All(source => string.Equals(Path.GetPathRoot(source), destinationRoot,
                StringComparison.OrdinalIgnoreCase));
        return ShouldMove(sameDrive, control, shift, alwaysMove);
    }

    internal static bool ShouldMove(bool sameDrive, bool control, bool shift, bool alwaysMove) =>
        !control && (alwaysMove || shift || sameDrive);
}
