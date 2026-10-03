using Lilium;

// Files need no valid image data: these checks exercise discovery, not decoding.
var root = Path.Combine(Path.GetTempPath(), "Lilium-cover-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".zip", ".cbz", ".pdf" };
bool Supported(string path) => extensions.Contains(Path.GetExtension(path));
string Folder(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
string Put(string folder, string relative)
{
    var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "check");
    return path;
}
void Expect(string label, string folder, string? expected)
{
    var actual = FolderCoverSearch.FindFirstFile(folder, Supported);
    if (actual != expected) throw new Exception($"{label}: expected {expected ?? "none"}, got {actual ?? "none"}");
    Console.WriteLine("PASS " + label);
    passed++;
}
try
{
    var direct = Folder("direct");
    Put(direct, "A/01.jpg");
    Expect("direct files beat descendants", direct, Put(direct, "10.pdf"));
    Expect("natural order mixes file types", direct, Put(direct, "2.CBZ"));

    var breadth = Folder("breadth");
    Put(breadth, "A/A1/01.jpg");
    Expect("child beats earlier folder's grandchild", breadth, Put(breadth, "B/10.zip"));

    var names = Folder("names");
    Put(names, "10/1.pdf");
    Expect("natural folder order", names, Put(names, "2/10.png"));
    Expect("natural file order within chosen folder", names, Put(names, "2/2.pdf"));

    var depth = Folder("depth");
    Put(depth, "A/B/C/1.jpg");
    Expect("great-grandchildren excluded", depth, null);
    Expect("grandchildren included", depth, Put(depth, "A/B/1.zip"));

    var hidden = Folder("hidden");
    var hiddenFile = Put(hidden, "1.jpg");
    File.SetAttributes(hiddenFile, FileAttributes.Hidden);
    var hiddenFolderImage = Put(hidden, "A/1.jpg");
    File.SetAttributes(Path.GetDirectoryName(hiddenFolderImage)!, FileAttributes.Hidden);
    var systemFile = Put(hidden, "2.pdf");
    File.SetAttributes(systemFile, FileAttributes.System);
    Expect("hidden/system files and hidden folders excluded", hidden, Put(hidden, "B/1.cbz"));

    Expect("empty folder", Folder("empty"), null);
    Expect("missing folder is skipped", Path.Combine(root, "missing"), null);

    var vanished = Folder("vanished");
    Put(vanished, "A/ignore.txt");
    var accessible = Put(vanished, "B/1.jpg");
    Put(vanished, "remove.txt");
    var found = FolderCoverSearch.FindFirstFile(vanished, path =>
    {
        if (Path.GetFileName(path) == "remove.txt") Directory.Delete(Path.Combine(vanished, "A"), true);
        return Supported(path);
    });
    if (found != accessible) throw new Exception("Missing child prevented sibling search.");
    Console.WriteLine("PASS missing child does not block siblings");
    passed++;

    var wide = Folder("wide");
    for (int i = 1; i <= FolderCoverSearch.MaxFolders; i++) Directory.CreateDirectory(Path.Combine(wide, i.ToString()));
    var lastAllowed = Put(wide, $"{FolderCoverSearch.MaxFolders - 1}/1.jpg");
    Put(wide, $"{FolderCoverSearch.MaxFolders}/1.jpg");
    Expect("last allowed folder included", wide, lastAllowed);
    File.Delete(lastAllowed);
    Expect("folder count limit includes root", wide, null);

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        FolderCoverSearch.FindFirstFile(direct, Supported, cancellation.Token);
        throw new Exception("Cancellation was ignored.");
    }
    catch (OperationCanceledException) { Console.WriteLine("PASS cancellation propagates"); passed++; }

    using var during = new CancellationTokenSource();
    try
    {
        FolderCoverSearch.FindFirstFile(breadth, path => { during.Cancel(); return false; }, during.Token);
        throw new Exception("Cancellation during search was ignored.");
    }
    catch (OperationCanceledException) { Console.WriteLine("PASS cancellation during traversal"); passed++; }
    Console.WriteLine($"{passed} checks passed.");
}
finally
{
    // Delete only the uniquely named fixture root this run created.
    var resolved = Path.GetFullPath(root);
    var temp = Path.GetFullPath(Path.GetTempPath());
    if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(resolved).StartsWith("Lilium-cover-checks-", StringComparison.Ordinal))
        throw new Exception("Unexpected fixture path.");
    foreach (var path in Directory.EnumerateFileSystemEntries(resolved, "*", SearchOption.AllDirectories))
        File.SetAttributes(path, FileAttributes.Normal);
    Directory.Delete(resolved, true);
}
