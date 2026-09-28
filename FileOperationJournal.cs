using System.Text.Json;

namespace Lilium;

internal sealed record OperationProgress(string Phase, string Path = "", int Index = 0, int Count = 0, long Bytes = 0, long Total = 0, bool WorkUnits = false);
internal sealed partial class FileOperationJournal
{
    internal static class BatchStates
    {
        internal const string Running = "Running";
        internal const string Complete = "Complete";
        internal const string Undoing = "Undoing";
        internal const string Undone = "Undone";
        internal const string Failed = "Failed";
        internal const string Cancelled = "Cancelled";
    }

    internal static class ItemStates
    {
        internal const string Planned = "Planned";
        internal const string Skipped = "Skipped";
        internal const string Working = "Working";
        internal const string Done = "Done";
        internal const string Undoing = "Undoing";
        internal const string Undone = "Undone";
    }

    internal sealed class Request
    {
        public string Kind { get; set; } = "Copy";
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public bool Overwrite { get; set; }
        public bool Skip { get; set; }
        public string SourceId { get; set; } = "";
        public string? TargetId { get; set; }
        public string SourceVolume { get; set; } = "";
        public string TargetVolume { get; set; } = "";
        public long Size { get; set; }
    }
    internal sealed class Item
    {
        public Request Request { get; set; } = new();
        public string State { get; set; } = ItemStates.Planned;
        public string Error { get; set; } = "";
        public string ResultId { get; set; } = "";
        public string ActualTarget { get; set; } = "";
        public RecycleTicket? Replaced { get; set; }
        public RecycleTicket? Recycled { get; set; }
        public bool Restored { get; set; }
        public Item? Inverse { get; set; }
    }
    internal sealed class Batch
    {
        public int FormatVersion { get; set; } = 1;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public string State { get; set; } = BatchStates.Running;
        public bool NoUndo { get; set; }
        public List<Item> Items { get; set; } = [];
        public bool Notified { get; set; }
        public bool NeedsNotice => !Notified && State is BatchStates.Running or BatchStates.Undoing or BatchStates.Failed;
        public int Success => Items.Count(i => i.State is ItemStates.Done or ItemStates.Undone);
        public int Skipped => Items.Count(i => i.State == ItemStates.Skipped);
        public string Summary => L10n.Format("FileOperationJournal_001", Success, Items.Count(i => i.State is ItemStates.Working or ItemStates.Undoing), Items.Count(i => i.State == ItemStates.Planned), Skipped);
        public override string ToString() => $"{Created.ToLocalTime():MM/dd HH:mm}  {Summary}";
    }
    private readonly string _root;
    private readonly string[] _protected;
    private readonly IShellFileService _recycle;

    internal FileOperationJournal(string root, IShellFileService recycle, params string[] protectedPaths)
    {
        _root = SafeFileSystem.Full(root); _recycle = recycle;
        _protected = protectedPaths.Append(_root).Where(p => !string.IsNullOrWhiteSpace(p)).Select(SafeFileSystem.Full).ToArray();
        Directory.CreateDirectory(_root);
    }
    private IDisposable Lease()
    {
        var pins = new SafeFileSystem.Pins(_root);
        try { return new Guard(new FileStream(Path.Combine(Path.GetDirectoryName(_root)!, "file-operations.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), pins); }
        catch { pins.Dispose(); throw new IOException(L10n.Get("FileOperationJournal_002")); }
    }
    private sealed class Guard(FileStream file, IDisposable pins) : IDisposable { public void Dispose() { file.Dispose(); pins.Dispose(); } }
    private string PathFor(Batch b) => Path.Combine(_root, b.Id + ".json");
    private void Save(Batch b)
    {
        var path = PathFor(b); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, b); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    private List<Batch> Read()
    {
        try { return ReadRecovering(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new RecordAccessException(_root, ex); }
    }
    internal List<Batch> List() { using var lease = Lease(); return Read(); }
    private void Validate(string path)
    {
        path = SafeFileSystem.Full(path); SafeFileSystem.RequireNtfs(path);
        if (path.Length <= 3) throw new IOException(L10n.Get("FileOperationJournal_003"));
        var physical = SafeFileSystem.Physical(path);
        foreach (var p in _protected)
        {
            var pp = SafeFileSystem.Physical(p);
            if (SafeFileSystem.Within(physical, pp) || SafeFileSystem.Within(pp, physical)) throw new IOException(L10n.Get("FileOperationJournal_004"));
        }
        if (SafeFileSystem.Exists(path))
        {
            SafeFileSystem.NotLink(path);
            if ((File.GetAttributes(path) & FileAttributes.System) != 0) throw new IOException(L10n.Get("FileOperationJournal_005"));
        }
    }
    internal void Prepare(IReadOnlyList<Request> requests, CancellationToken token, Action<OperationProgress> report)
    {
        using var lease = Lease();
        _ = Read();
        var required = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (var r in requests)
        {
            token.ThrowIfCancellationRequested(); report(new(L10n.Get("FileOperationJournal_006"), r.Source, ++index, requests.Count));
            if (r.Skip) continue;
            if (r.Kind is not ("Copy" or "Move" or "Delete" or "New")) throw new IOException(L10n.Get("FileOperationJournal_007"));
            if (r.Kind != "New")
            {
                r.Source = SafeFileSystem.Full(r.Source); Validate(r.Source);
                r.SourceId = SafeFileSystem.Identity(r.Source); r.SourceVolume = SafeFileSystem.Volume(r.Source);
                r.Size = SafeFileSystem.Size(r.Source, token); // metadata only, no content read
            }
            if (r.Kind == "Delete") continue;
            r.Target = SafeFileSystem.Full(r.Target); Validate(r.Target);
            if (r.Kind != "New") SafeFileSystem.Separate(r.Source, r.Target);
            r.TargetVolume = SafeFileSystem.Volume(Path.GetDirectoryName(r.Target)!);
            if (!targets.Add(SafeFileSystem.Physical(r.Target))) throw new IOException(L10n.Get("FileOperationJournal_008") + r.Target);
            r.TargetId = SafeFileSystem.Exists(r.Target) ? SafeFileSystem.Identity(r.Target) : null;
            if (r.TargetId is not null) _ = SafeFileSystem.Size(r.Target, token);
            if (r.TargetId is not null && !r.Overwrite) throw new IOException(L10n.Get("FileOperationJournal_009") + r.Target);
            if (r.Kind == "Copy" || r.Kind == "Move" && !SameVolume(r))
            {
                var drive = Path.GetPathRoot(r.Target)!;
                required[drive] = checked(required.GetValueOrDefault(drive) + r.Size);
            }
        }
        foreach (var (drive, bytes) in required)
            if (new DriveInfo(drive).AvailableFreeSpace < bytes + 64 * 1024 * 1024) throw new IOException(L10n.Get("FileOperationJournal_010") + drive);
        if (new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace < 16 * 1024 * 1024) throw new IOException(L10n.Get("FileOperationJournal_011"));
        // Reject batches whose destination overwrites another selected source.
        foreach (var r in requests.Where(r => !r.Skip && r.Kind != "Delete"))
            foreach (var other in requests.Where(o => !o.Skip && o.Kind != "New" && !ReferenceEquals(o, r)))
                SafeFileSystem.Separate(other.Source, r.Target);
    }
    private static bool SameVolume(Request r) => r.SourceVolume == r.TargetVolume;
    private static void CheckId(string path, string expected)
    {
        if (!SafeFileSystem.Exists(path) || SafeFileSystem.Identity(path) != expected) throw new IOException(L10n.Get("FileOperationJournal_012") + path);
    }
    internal Batch Execute(IReadOnlyList<Request> requests, CancellationToken token, Action<OperationProgress> report)
    {
        using var lease = Lease();
        var previous = Read(); // Recover before creating a new Running record or touching user files.
        // Revalidate the preflight identities before the first mutation.
        foreach (var r in requests.Where(r => !r.Skip)) VerifyRequest(r);
        var batch = new Batch { NoUndo = requests.Any(r => !r.Skip && r.Overwrite), Items = requests.Select(r => new Item { Request = r, State = r.Skip ? ItemStates.Skipped : ItemStates.Planned }).ToList() };
        if (batch.Items.All(i => i.State == ItemStates.Skipped)) { batch.State = BatchStates.Complete; return batch; }
        Save(batch);
        foreach (var old in previous.Where(b => !b.NeedsNotice)) File.Delete(PathFor(old)); // metadata only
        File.Delete(UndoBarrierPath); // The new persisted batch now supersedes the unknown operation.
        int index = 0;
        foreach (var item in batch.Items)
        {
            index++;
            if (item.State == ItemStates.Skipped) continue;
            try
            {
                token.ThrowIfCancellationRequested();
                Apply(batch, item, token, p => report(p with { Index = index, Count = batch.Items.Count }));
            }
            catch (Exception ex)
            {
                item.Error = ex is OperationCanceledException ? L10n.Get("FileOperationJournal_013") : ex.Message;
                batch.State = ex is OperationCanceledException ? BatchStates.Cancelled : BatchStates.Failed; batch.NoUndo = true; Save(batch); return batch;
            }
        }
        batch.State = BatchStates.Complete; Save(batch); return batch;
    }
    private void VerifyRequest(Request r)
    {
        if (r.Kind != "New") { Validate(r.Source); CheckId(r.Source, r.SourceId); if (SafeFileSystem.Volume(r.Source) != r.SourceVolume) throw new IOException(L10n.Get("FileOperationJournal_014")); }
        if (r.Kind == "Delete") return;
        Validate(r.Target);
        if (SafeFileSystem.Volume(Path.GetDirectoryName(r.Target)!) != r.TargetVolume) throw new IOException(L10n.Get("FileOperationJournal_015"));
        if (r.TargetId is null) { if (SafeFileSystem.Exists(r.Target)) throw new IOException(L10n.Get("FileOperationJournal_016") + r.Target); }
        else CheckId(r.Target, r.TargetId);
    }
    private void Apply(Batch b, Item item, CancellationToken token, Action<OperationProgress> report)
    {
        var r = item.Request;
        token.ThrowIfCancellationRequested();
        VerifyRequest(r);
        item.State = ItemStates.Working; Save(b);
        if (r.Kind == "Delete")
        {
            report(new(L10n.Get("FileOperationJournal_017"), r.Source));
            _recycle.Recycle(r.Source, r.SourceId, ticket => { item.Recycled = ticket; Save(b); }, token);
        }
        else
        {
            if (r.TargetId is not null)
            {
                report(new(L10n.Get("FileOperationJournal_018"), r.Target));
                _recycle.Recycle(r.Target, r.TargetId, ticket => { item.Replaced = ticket; Save(b); }, token);
                token.ThrowIfCancellationRequested();
            }
            var phase = r.Kind switch { "Copy" => L10n.Get("FileOperationJournal_019"), "Move" => L10n.Get("FileOperationJournal_020"), _ => L10n.Get("FileOperationJournal_021") };
            report(new(phase, r.Source.Length > 0 ? r.Source : r.Target));
            _recycle.Transfer(r.Kind, r.Source, r.Target, r.SourceId, actual =>
            {
                item.ActualTarget = actual; item.ResultId = SafeFileSystem.Identity(actual); Save(b);
            }, token, (total, done) => report(new(phase, r.Source, Bytes: done, Total: total, WorkUnits: true)));
            if (!string.Equals(item.ActualTarget, r.Target, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L10n.Get("FileOperationJournal_022") + item.ActualTarget);
            CheckId(r.Target, item.ResultId);
        }
        item.State = ItemStates.Done; item.Error = ""; Save(b);
    }
    internal Batch Undo(CancellationToken token, Action<OperationProgress> report, string? confirmedCopyBatchId = null)
    {
        using var lease = Lease(); var b = Read().FirstOrDefault() ?? throw new IOException(L10n.Get("FileOperationJournal_023"));
        if (SafeFileSystem.Exists(UndoBarrierPath))
            throw new IOException(L10n.Get("FileOperationJournal_024"));
        // A confirmation authorizes this batch only. Check under the same lease
        // that protects Undo, before changing either files or journal state.
        if (confirmedCopyBatchId is not null && b.Id != confirmedCopyBatchId)
            throw new IOException(L10n.Get("FileOperationJournal_025"));
        if (b.State is not BatchStates.Complete) throw new IOException(L10n.Get("FileOperationJournal_026"));
        if (b.NoUndo) throw new IOException(L10n.Get("FileOperationJournal_027"));
        var targets = b.Items.Where(i => i.State == ItemStates.Done).Reverse().ToArray();
        // Preflight every item before making the first inverse change.
        foreach (var i in targets)
        {
            var r = i.Request;
            if (r.Kind is "Move" or "Delete" && SafeFileSystem.Volume(Path.GetDirectoryName(r.Source)!) != r.SourceVolume)
                throw new IOException(L10n.Get("FileOperationJournal_028"));
            if (r.Kind == "Delete") { if (i.Recycled is null) throw new IOException(L10n.Get("FileOperationJournal_029")); if (SafeFileSystem.Exists(r.Source)) throw new IOException(L10n.Get("FileOperationJournal_030") + r.Source); CheckId(i.Recycled.FilePath, i.Recycled.Identity); }
            else
            {
                CheckId(r.Target, i.ResultId);
                if (r.Kind == "Move" && SafeFileSystem.Exists(r.Source)) throw new IOException(L10n.Get("FileOperationJournal_031") + r.Source);
                if (r.Kind == "New" && Directory.EnumerateFileSystemEntries(r.Target).Any()) throw new IOException(L10n.Get("FileOperationJournal_032"));
                if (r.Kind == "Copy" && confirmedCopyBatchId is null) throw new EditedCopyException(b.Id);
            }
        }
        b.State = BatchStates.Undoing; Save(b);
        try
        {
        foreach (var i in targets)
        {
            token.ThrowIfCancellationRequested();
            var r = i.Request; i.State = ItemStates.Undoing; Save(b);
            try
            {
                if (r.Kind == "Delete")
                {
                    report(new(L10n.Get("FileOperationJournal_033"), r.Source));
                    _recycle.Restore(i.Recycled!, r.Source, () => { i.Restored = true; Save(b); }, token);
                }
                else
                {
                    var inverse = new Request { Kind = r.Kind == "Move" ? "Move" : "Delete", Source = r.Target, Target = r.Kind == "Move" ? r.Source : "", SourceId = i.ResultId, SourceVolume = SafeFileSystem.Volume(r.Target), TargetVolume = r.Kind == "Move" ? SafeFileSystem.Volume(Path.GetDirectoryName(r.Source)!) : "" };
                    if (inverse.Kind == "Move")
                    {
                        inverse.Size = SafeFileSystem.Size(inverse.Source, token);
                        if (!SameVolume(inverse) && new DriveInfo(Path.GetPathRoot(inverse.Target)!).AvailableFreeSpace < inverse.Size + 64L * 1024 * 1024)
                            throw new IOException(L10n.Get("FileOperationJournal_034"));
                    }
                    i.Inverse = new Item { Request = inverse }; Save(b); Apply(b, i.Inverse, token, report);
                }
                i.State = ItemStates.Undone; Save(b);
            }
            catch (Exception ex) { i.Error = ex.Message; Save(b); throw; }
        }
        b.State = BatchStates.Undone; Save(b); return b;
        }
        catch (Exception ex)
        {
            b.State = ex is OperationCanceledException ? BatchStates.Cancelled : BatchStates.Failed; b.NoUndo = true;
            var current = b.Items.FirstOrDefault(i => i.State == ItemStates.Undoing);
            if (current is not null) current.Error = ex.Message;
            Save(b); return b;
        }
    }
    internal sealed class EditedCopyException : IOException
    {
        internal string BatchId { get; }
        public EditedCopyException(string batchId) : base(L10n.Get("FileOperationJournal_035"))
        { BatchId = batchId; }
    }
    internal void MarkNotified(string id)
    {
        using var lease = Lease();
        var b = Read().SingleOrDefault(b => b.Id == id);
        if (b is null) return;
        b.Notified = true;
        if (b.State != BatchStates.Complete) b.NoUndo = true;
        Save(b);
    }
    internal static string NoticeText(Batch batch)
    {
        var lines = new List<string> { L10n.Get("FileOperationJournal_036"), batch.Summary };
        foreach (var i in batch.Items)
        {
            lines.Add(L10n.Format("FileOperationJournal_037", i.Request.Source, i.Request.Target));
            if (i.ActualTarget.Length > 0) lines.Add(L10n.Get("FileOperationJournal_038") + i.ActualTarget);
            if (i.Inverse is not null) lines.Add(L10n.Format("FileOperationJournal_039", i.Inverse.Request.Source, i.Inverse.Request.Target, i.Inverse.ActualTarget));
            if (i.Error.Length > 0) lines.Add(i.Error);
            if (i.Replaced is not null) lines.Add(L10n.Get("FileOperationJournal_040"));
            else if (i.Request.Overwrite && i.State != ItemStates.Planned && i.State != ItemStates.Skipped) lines.Add(L10n.Get("FileOperationJournal_041"));
            if (i.Request.Kind == "Delete") lines.Add(L10n.Get("FileOperationJournal_042"));
        }
        return string.Join("\n\n", lines);
    }
}
