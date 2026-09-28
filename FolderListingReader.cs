using System.Security;

namespace Lilium;

public enum FolderReadStatus
{
    Success,
    NotFound,
    AccessDenied,
    Partial,
    Failed
}

public sealed record FileSystemEntryData(
    string Path,
    string Name,
    DateTime Modified,
    bool IsFolder,
    FileAttributes Attributes);

public sealed record FolderReadResult(
    IReadOnlyList<FileSystemEntryData> Entries,
    FolderReadStatus Status,
    Exception? Error = null)
{
    public bool IsSuccess => Status == FolderReadStatus.Success;
}

public sealed record FolderTraversalResult(
    IReadOnlyList<string> Folders,
    FolderReadStatus Status,
    Exception? Error = null)
{
    public bool IsSuccess => Status == FolderReadStatus.Success;
}

public static class FolderListingReader
{
    public static FolderReadResult Read(string folder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<FileSystemEntryData>();
        var errors = new List<Exception>();
        int completedParts = 0;

        if (ReadPart(() => Directory.EnumerateDirectories(folder), path =>
            {
                var info = new DirectoryInfo(path);
                return new FileSystemEntryData(info.FullName, info.Name, info.LastWriteTime, true, info.Attributes);
            }, entries, errors, cancellationToken)) completedParts++;

        if (ReadPart(() => Directory.EnumerateFiles(folder), path =>
            {
                var info = new FileInfo(path);
                return new FileSystemEntryData(info.FullName, info.Name, info.LastWriteTime, false, info.Attributes);
            }, entries, errors, cancellationToken)) completedParts++;

        return CreateResult(entries, completedParts, 2, errors);
    }

    public static FolderReadResult ReadDirectories(string folder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<FileSystemEntryData>();
        var errors = new List<Exception>();
        int completedParts = ReadPart(() => Directory.EnumerateDirectories(folder), path =>
        {
            var info = new DirectoryInfo(path);
            return new FileSystemEntryData(info.FullName, info.Name, info.LastWriteTime, true, info.Attributes);
        }, entries, errors, cancellationToken) ? 1 : 0;
        return CreateResult(entries, completedParts, 1, errors);
    }

    public static FolderTraversalResult ReadDescendantDirectories(string root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folders = new List<string>();
        var errors = new List<Exception>();
        var pending = new Stack<string>();
        pending.Push(root);
        int successfulFolders = 0;

        while (pending.TryPop(out var path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ReadDirectories(path, cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.Error is not null) errors.Add(result.Error);
                continue;
            }

            successfulFolders++;
            foreach (var child in result.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(child.Path);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                    folders.Add(child.Path);
                    pending.Push(child.Path);
                }
                catch (Exception ex) when (IsExpected(ex)) { errors.Add(ex); }
            }
        }

        if (errors.Count == 0) return new FolderTraversalResult(folders, FolderReadStatus.Success);
        var error = Combine(errors);
        var status = successfulFolders > 0 || folders.Count > 0
            ? FolderReadStatus.Partial
            : ClassifyFailure(errors);
        return new FolderTraversalResult(folders, status, error);
    }

    internal static FolderReadResult CreateResult(
        IReadOnlyList<FileSystemEntryData> entries,
        int completedParts,
        int totalParts,
        IReadOnlyList<Exception> errors)
    {
        if (errors.Count == 0 && completedParts == totalParts)
            return new FolderReadResult(entries, FolderReadStatus.Success);

        var status = completedParts > 0 || entries.Count > 0
            ? FolderReadStatus.Partial
            : ClassifyFailure(errors);
        return new FolderReadResult(entries, status, Combine(errors));
    }

    private static bool ReadPart(
        Func<IEnumerable<string>> source,
        Func<string, FileSystemEntryData> createEntry,
        List<FileSystemEntryData> entries,
        List<Exception> errors,
        CancellationToken cancellationToken)
    {
        bool complete = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in source())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { entries.Add(createEntry(path)); }
                catch (Exception ex) when (IsExpected(ex)) { errors.Add(ex); complete = false; }
            }
        }
        catch (Exception ex) when (IsExpected(ex)) { errors.Add(ex); complete = false; }
        return complete;
    }

    private static FolderReadStatus ClassifyFailure(IReadOnlyList<Exception> errors)
    {
        if (errors.Any(Contains<DirectoryNotFoundException>)) return FolderReadStatus.NotFound;
        if (errors.Any(error => Contains<UnauthorizedAccessException>(error) || Contains<SecurityException>(error)))
            return FolderReadStatus.AccessDenied;
        return FolderReadStatus.Failed;
    }

    private static bool Contains<T>(Exception error) where T : Exception =>
        error is T || error is AggregateException aggregate && aggregate.InnerExceptions.Any(Contains<T>);

    private static Exception? Combine(IReadOnlyList<Exception> errors) => errors.Count switch
    {
        0 => null,
        1 => errors[0],
        _ => new AggregateException(errors)
    };

    private static bool IsExpected(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException;
}
