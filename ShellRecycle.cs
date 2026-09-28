using System.Runtime.InteropServices;

namespace Lilium;

internal sealed record RecycleTicket(string ParsingName, string FilePath, string Identity);
internal interface IRecycleService
{
    void Recycle(string path, string identity, Action<RecycleTicket> saved, CancellationToken token);
    void Restore(RecycleTicket ticket, string destination, Action restored, CancellationToken token);
}

internal interface IShellFileService : IRecycleService
{
    void Transfer(string kind, string source, string target, string sourceId,
        Action<string> completed, CancellationToken token, Action<uint, uint> progress);
}

// Uses the shell namespace to retain recycle metadata; never manipulates $I files.
internal sealed class ShellRecycle : IShellFileService
{
    internal static readonly AsyncLocal<IntPtr> OwnerWindow = new();
    public void Transfer(string kind, string source, string target, string sourceId,
        Action<string> completed, CancellationToken token, Action<uint, uint> progress) => OnSta(() =>
    {
        if (kind is not ("Copy" or "Move" or "New")) throw new ArgumentException("Unknown operation");
        using var destinationPins = new SafeFileSystem.Pins(Path.GetDirectoryName(target)!);
        using var sourcePins = kind == "New" ? null : new SafeFileSystem.Pins(Path.GetDirectoryName(source)!);
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (SafeFileSystem.Exists(target)) throw new IOException(L10n.Get("ShellRecycle_001") + target);
            if (kind != "New" && SafeFileSystem.Identity(source) != sourceId) throw new IOException(L10n.Get("ShellRecycle_002"));
        }
        Check();
        bool movingDirectory = kind == "Move" && Directory.Exists(source);
        bool crossVolumeDirectory = movingDirectory && SafeFileSystem.Volume(source) != SafeFileSystem.Volume(Path.GetDirectoryName(target)!);
        var inventory = crossVolumeDirectory ? ReadDirectoryInventory(source, token) : null;
        var parent = Create(Path.GetDirectoryName(target)!);
        IShellItem? item = null;
        try
        {
            if (kind != "New") item = Create(source);
            var sink = new Sink(token) { BeforeMove = Check, Progress = progress, RootPath = kind == "New" ? null : source };
            void Saved(IShellItem created) => completed(Name(created, 0x80058000));
            sink.Moved = Saved; sink.Copied = Saved; sink.Created = Saved;
            Execute(item, parent, Path.GetFileName(target), sink, kind,
                movingDirectory ? () => ConfirmDirectoryMove(source, target, completed) : null,
                inventory is not null ? () => ConfirmDirectoryInventory(source, target, inventory, completed, token) : null);
        }
        finally { if (item is not null) Marshal.ReleaseComObject(item); Marshal.ReleaseComObject(parent); }
    });

    public void Recycle(string path, string identity, Action<RecycleTicket> saved, CancellationToken token) => OnSta(() =>
    {
        using var pins = new SafeFileSystem.Pins(Path.GetDirectoryName(path)!);
        var item = Create(path);
        try
        {
            var sink = new Sink(token)
            {
                RootPath = path,
                BeforeDelete = (flags, source) =>
                {
                    if ((flags & 0x80) == 0) throw new IOException(L10n.Get("ShellRecycle_003"));
                    if (SafeFileSystem.Identity(path) != identity) throw new IOException(L10n.Get("ShellRecycle_004"));
                },
                Deleted = newItem =>
                {
                    var filePath = Name(newItem, 0x80058000); // SIGDN_FILESYSPATH
                    var ticket = new RecycleTicket(Name(newItem, 0x80028000), filePath, SafeFileSystem.Identity(filePath));
                    if (ticket.Identity != identity) throw new IOException(L10n.Get("ShellRecycle_005"));
                    saved(ticket);
                }
            };
            Execute(item, null, null, sink);
        }
        finally { Marshal.ReleaseComObject(item); }
    });
    public void Restore(RecycleTicket ticket, string destination, Action restored, CancellationToken token) => OnSta(() =>
    {
        using var pins = new SafeFileSystem.Pins(Path.GetDirectoryName(destination)!);
        if (SafeFileSystem.Exists(destination)) throw new IOException(L10n.Get("ShellRecycle_006"));
        if (!SafeFileSystem.Exists(ticket.FilePath) || SafeFileSystem.Identity(ticket.FilePath) != ticket.Identity)
            throw new IOException(L10n.Get("ShellRecycle_007"));
        var item = Create(ticket.ParsingName); var parent = Create(Path.GetDirectoryName(destination)!);
        try
        {
            var sink = new Sink(token)
            {
                RootPath = ticket.FilePath,
                BeforeMove = () =>
                {
                    if (SafeFileSystem.Exists(destination) || SafeFileSystem.Identity(ticket.FilePath) != ticket.Identity)
                        throw new IOException(L10n.Get("ShellRecycle_008"));
                },
                Moved = newItem =>
                {
                    if (!string.Equals(Name(newItem, 0x80058000), destination, StringComparison.OrdinalIgnoreCase))
                        throw new IOException(L10n.Get("ShellRecycle_009"));
                    restored();
                }
            };
            Execute(item, parent, Path.GetFileName(destination), sink);
        }
        finally { Marshal.ReleaseComObject(parent); Marshal.ReleaseComObject(item); }
    });
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Execute(IShellItem? item, IShellItem? parent, string? name, Sink sink, string kind = "Move", Action? confirmDirectoryMove = null, Action? confirmAbortedDirectoryMove = null)
    {
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), true)!)!;
        try
        {
            if (OwnerWindow.Value != IntPtr.Zero) operation.SetOwnerWindow(OwnerWindow.Value);
            // No automatic Yes-to-all. Refuse non-recycle deletes in PreDelete.
            // Rename-on-collision on restore is a final safety net, never overwrite.
            operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x00000400 | 0x00000004 | 0x00000200 | 0x00002000 | 0x00000008 | 0x02000000 | 0x00004000);
            // The global sink receives progress and nested errors, never commits root results.
            var observer = new Sink(sink.Token) { Progress = sink.Progress };
            operation.Advise(observer, out uint cookie);
            if (parent is null) operation.DeleteItem(item!, sink);
            else if (kind == "Copy") operation.CopyItem(item!, parent, name!, sink);
            else if (kind == "New") operation.NewItem(parent, 0x10, name!, null!, sink);
            else operation.MoveItem(item!, parent, name!, sink);
            int result = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            operation.Unadvise(cookie);
            sink.Token.ThrowIfCancellationRequested();
            if (IsUserCancellation(result) || IsUserCancellation(sink.Failure?.HResult) || IsUserCancellation(observer.Failure?.HResult))
                throw new OperationCanceledException(L10n.Get("ShellRecycle_010"));
            if (sink.Failure is not null) throw sink.Failure;
            if (observer.Failure is not null) throw observer.Failure;
            sink.Token.ThrowIfCancellationRequested();
            CompleteOperation(result, aborted, sink.Completed, confirmDirectoryMove, confirmAbortedDirectoryMove);
        }
        finally { Marshal.ReleaseComObject(operation); }
    }
    // Cross-volume folders may finish through child operations without a final
    // root PostMoveItem. This fallback runs ONLY after all callbacks were checked
    // for errors/cancellation and Windows reports overall success with no abort.
    internal static void CompleteOperation(int result, bool aborted, bool rootCompleted, Action? confirmDirectoryMove, Action? confirmAbortedDirectoryMove = null)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        if (aborted)
        {
            // A shell confirmation/fallback can leave the batch abort flag set.
            // Only reconcile a successful cross-volume directory move with a
            // pre-operation inventory, no surviving source, and no callback errors.
            if (result == 0 && confirmAbortedDirectoryMove is not null) { confirmAbortedDirectoryMove(); return; }
            throw new IOException(L10n.Format("ShellRecycle_011", result));
        }
        if (rootCompleted) return;
        if (result == 0 && confirmDirectoryMove is not null) { confirmDirectoryMove(); return; }
        throw new IOException(L10n.Format("ShellRecycle_012", result));
    }
    internal static void ConfirmDirectoryMove(string source, string target, Action<string> completed)
    {
        if (SafeFileSystem.Exists(source) || !Directory.Exists(target))
            throw new IOException(L10n.Get("ShellRecycle_013") + source + "\n" + target);
        SafeFileSystem.NotLink(target);
        completed(target); // Persist the actual result, including its identity for Undo.
    }
    internal sealed record DirectoryEntry(bool Directory, long WriteTicks, string Streams);
    internal static Dictionary<string, DirectoryEntry> ReadDirectoryInventory(string root, CancellationToken token)
    {
        var entries = new Dictionary<string, DirectoryEntry>(StringComparer.OrdinalIgnoreCase);
        Visit(root, "");
        return entries;
        void Visit(string path, string relative)
        {
            token.ThrowIfCancellationRequested(); SafeFileSystem.NotLink(path);
            bool directory = System.IO.Directory.Exists(path);
            // Metadata only: do not read/hash the file contents. Include hidden and
            // system entries, empty folders and ADS; don't rely on viewer filtering.
            string streams = System.Text.Json.JsonSerializer.Serialize(SafeFileSystem.Streams(path)
                .OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => new { s.Name, s.Size }));
            entries.Add(relative, new(directory, directory ? 0 : File.GetLastWriteTimeUtc(path).Ticks, streams));
            if (!directory) return;
            using var pins = new SafeFileSystem.Pins(path);
            foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(path))
                Visit(child, Path.Combine(relative, Path.GetFileName(child)));
        }
    }
    internal static void ConfirmDirectoryInventory(string source, string target, Dictionary<string, DirectoryEntry> expected, Action<string> completed, CancellationToken token)
    {
        ConfirmDirectoryMove(source, target, _ => { });
        var actual = ReadDirectoryInventory(target, token);
        if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var entry) || entry != pair.Value))
            throw new IOException(L10n.Get("ShellRecycle_014") + source + "\n" + target);
        token.ThrowIfCancellationRequested();
        ConfirmDirectoryMove(source, target, completed);
    }
    private static IShellItem Create(string path)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item)); return item;
    }
    internal static bool IsUserCancellation(int? code) => code == unchecked((int)0x800704C7) || code == unchecked((int)0x80270000);
#if LILIUM_SAFETY_TEST
    internal static (int ReturnCode, bool Completed, bool Failed, bool Saved) TestNotification(int result)
    {
        var sink = new Sink(default);
        bool saved = false;
        int code = sink.TestFinish(result, () => saved = true);
        return (code, sink.Completed, sink.Failure is not null, saved);
    }
#endif
    private static string Name(IShellItem item, uint kind)
    {
        item.GetDisplayName(kind, out var pointer);
        try { return Marshal.PtrToStringUni(pointer) ?? throw new IOException(L10n.Get("ShellRecycle_015")); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Sink(CancellationToken token) : IFileOperationProgressSink
    {
        internal CancellationToken Token => token;
        internal Action<uint, IShellItem>? BeforeDelete;
        internal Action<IShellItem>? Deleted, Moved, Copied, Created;
        internal Action<uint, uint>? Progress;
        internal string? RootPath;
        private bool IsRoot(IShellItem item) => RootPath is null || string.Equals(Name(item, 0x80058000), RootPath, StringComparison.OrdinalIgnoreCase);
        internal Action? BeforeMove;
        internal Exception? Failure;
        internal bool Completed;
        private int Call(Action action) { try { token.ThrowIfCancellationRequested(); action(); return 0; } catch (Exception ex) { Failure ??= ex; return unchecked((int)0x80004004); } }
        public int StartOperations() => Call(() => { });
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint f, IShellItem i, string n) => 0;
        public int PostRenameItem(uint f, IShellItem i, string n, int hr, IShellItem? created) => 0;
        public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n) => Call(() => { if ((f & 2) != 0) throw new IOException(L10n.Get("ShellRecycle_016")); SafeFileSystem.NotLink(Name(i, 0x80058000)); if (IsRoot(i)) BeforeMove?.Invoke(); });
        public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int hr, IShellItem? created) => Post(i, hr, created, Moved);
        public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) => PreMoveItem(f, i, d, n);
        public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int hr, IShellItem? created) => Post(i, hr, created, Copied);
        public int PreDeleteItem(uint f, IShellItem i) => Call(() => { if (IsRoot(i)) BeforeDelete?.Invoke(f, i); });
        public int PostDeleteItem(uint f, IShellItem i, int hr, IShellItem? created) => Post(i, hr, created, Deleted);
        private int Post(IShellItem source, int hr, IShellItem? created, Action<IShellItem>? action)
        {
            try { return Finish(hr, created, action is not null && IsRoot(source) ? action : null); }
            catch (Exception ex) { Failure ??= ex; return unchecked((int)0x80004004); }
        }
        private int Finish(int hr, IShellItem? item, Action<IShellItem>? action)
        {
            // Always persist a completed shell side effect, even after cancellation.
            try
            {
                Marshal.ThrowExceptionForHR(hr);
                // COPYENGINE_S_NOT_HANDLED is a nonterminal shell notification,
                // e.g. before the cross-volume folder move falls back to copying.
                // Let the shell continue, but do not record this as completion.
                // Execute still requires a later completed root notification AND
                // successful PerformOperations/GetAnyOperationsAborted results.
                if (hr == 0x00270003) return 0;
                // sherrors.h: successful rename/move may return DONT_PROCESS_CHILDREN
                // or ALREADY_DONE rather than S_OK. Ignore/pending are not completion.
                if (hr is not (0 or 0x00270008 or 0x0027000A))
                    throw new IOException(L10n.Format("ShellRecycle_017", hr));
                if (action is null) return 0;
                if (item is null) throw new IOException(L10n.Get("ShellRecycle_018"));
                action(item); Completed = true; return 0;
            }
            catch (Exception ex) { Failure ??= ex; return unchecked((int)0x80004004); }
        }
#if LILIUM_SAFETY_TEST
        internal int TestFinish(int result, Action saved) => Finish(result, null, _ => saved());
#endif
        public int PreNewItem(uint f, IShellItem d, string n) => Call(() => BeforeMove?.Invoke());
        public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int hr, IShellItem? item) => Finish(hr, item, Created);
        public int UpdateProgress(uint total, uint done) => Call(() => Progress?.Invoke(total, done));
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attrs);
        void Compare(IShellItem other, uint hint, out int result);
    }
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr window);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void MoveItems(IntPtr items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void CopyItems(IntPtr items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [ComVisible(true), Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attrs, int hr, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}
