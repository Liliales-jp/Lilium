namespace Lilium;

public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string? x, string? y);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x, y);
}
