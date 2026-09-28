using System.Collections.Concurrent;
using System.Text.Json;

namespace Lilium;

internal sealed partial class FileOperationJournal
{
    internal sealed record RecoveryNotice(string OriginalPath, string QuarantinedPath, string Reason);
    private readonly ConcurrentQueue<RecoveryNotice> _recoveryNotices = new();
    private string UndoBarrierPath => Path.Combine(_root, "undo-unavailable");

    internal RecoveryNotice[] TakeRecoveryNotices()
    {
        var notices = new List<RecoveryNotice>();
        while (_recoveryNotices.TryDequeue(out var notice)) notices.Add(notice);
        return notices.ToArray();
    }

    internal sealed class RecordAccessException(string root, Exception inner)
        : IOException(L10n.Get("FileOperationJournal_Recovery_001") + root + "\n" + inner.Message, inner);

    private List<Batch> ReadRecovering()
    {
        var result = new List<Batch>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json").ToArray())
        {
            SafeFileSystem.NotLink(path);
            // I/O errors are not proof of corruption. Leave unreadable/locked files in place.
            var text = File.ReadAllText(path);
            try
            {
                using var document = JsonDocument.Parse(text);
                var record = document.RootElement;
                if (record.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_002"));
                if (!record.TryGetProperty("FormatVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
                    throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_003"));
                if (number > 1) throw new IOException(L10n.Get("FileOperationJournal_Recovery_004") + path);
                if (number != 1) throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_005"));
                foreach (var property in new[] { "Id", "Created", "State", "Items", "NoUndo", "Notified" })
                    if (!record.TryGetProperty(property, out _)) throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_006") + property);
                if (record.GetProperty("Items").ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_007"));
                foreach (var item in record.GetProperty("Items").EnumerateArray()) ValidateItemShape(item, 0);
                var batch = JsonSerializer.Deserialize<Batch>(text) ?? throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_008"));
                ValidateRecord(batch, path);
                result.Add(batch);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                Quarantine(path, ex.Message);
            }
        }
        return result.OrderByDescending(b => b.Created).ToList();
    }

    private static void ValidateItemShape(JsonElement item, int depth)
    {
        if (depth > 1 || item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("State", out _) ||
            !item.TryGetProperty("Request", out var request) || request.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_009"));
        foreach (var property in new[] { "Kind", "Source", "Target", "Overwrite", "Skip", "SourceId", "SourceVolume", "TargetVolume" })
            if (!request.TryGetProperty(property, out _)) throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_010") + property);
        if (item.TryGetProperty("Inverse", out var inverse) && inverse.ValueKind != JsonValueKind.Null)
            ValidateItemShape(inverse, depth + 1);
    }

    private void Quarantine(string path, string reason)
    {
        // Persist the Undo barrier BEFORE moving anything. It survives restart and another process.
        // Otherwise removing an unreadable latest record could expose an older operation as "latest".
        if (SafeFileSystem.Exists(UndoBarrierPath)) SafeFileSystem.NotLink(UndoBarrierPath);
        using (var barrier = new FileStream(UndoBarrierPath, FileMode.OpenOrCreate, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        { barrier.WriteByte(1); barrier.Flush(true); }
        var destination = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(destination);
        using var pins = new SafeFileSystem.Pins(destination);
        SafeFileSystem.NotLink(path);
        var target = Path.Combine(destination, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".json");
        File.Move(path, target); // No overwrite, no deletion, no replay of user file operations.
        _recoveryNotices.Enqueue(new(path, target, reason));
    }

    private static void ValidateRecord(Batch batch, string path)
    {
        if (!Guid.TryParseExact(batch.Id, "N", out _) || Path.GetFileNameWithoutExtension(path) != batch.Id ||
            batch.Created == default || batch.Items is null || batch.Items.Count == 0 ||
            batch.State is not (BatchStates.Running or BatchStates.Complete or BatchStates.Undoing or BatchStates.Undone or BatchStates.Failed or BatchStates.Cancelled))
            throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_011"));
        foreach (var item in batch.Items) ValidateItem(item, 0);
        if (batch.State == BatchStates.Complete && batch.Items.Any(i => i.State is not (ItemStates.Done or ItemStates.Skipped)))
            throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_012"));
    }

    private static void ValidateItem(Item? item, int depth)
    {
        if (item is null || depth > 1 || item.Request is not { } r ||
            item.State is not (ItemStates.Planned or ItemStates.Skipped or ItemStates.Working or ItemStates.Done or ItemStates.Undoing or ItemStates.Undone) ||
            r.Kind is not ("Copy" or "Move" or "Delete" or "New") || r.Size < 0 ||
            r.Source is null || r.Target is null || r.SourceId is null || r.SourceVolume is null || r.TargetVolume is null ||
            item.Error is null || item.ActualTarget is null || item.ResultId is null)
            throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_013"));
        if (!r.Skip)
        {
            if (r.Kind != "New" && (!Path.IsPathFullyQualified(r.Source) || r.SourceId.Length == 0 || r.SourceVolume.Length == 0))
                throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_014"));
            if (r.Kind != "Delete" && (!Path.IsPathFullyQualified(r.Target) || r.TargetVolume.Length == 0))
                throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_015"));
            if (item.State == ItemStates.Done && r.Kind != "Delete" &&
                (item.ResultId.Length == 0 || !Path.IsPathFullyQualified(item.ActualTarget)))
                throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_016"));
        }
        foreach (var ticket in new[] { item.Recycled, item.Replaced })
            if (ticket is not null && (string.IsNullOrWhiteSpace(ticket.ParsingName) ||
                string.IsNullOrWhiteSpace(ticket.Identity) || string.IsNullOrWhiteSpace(ticket.FilePath)))
                throw new InvalidDataException(L10n.Get("FileOperationJournal_Recovery_017"));
        if (item.Inverse is not null) ValidateItem(item.Inverse, depth + 1);
    }
}
