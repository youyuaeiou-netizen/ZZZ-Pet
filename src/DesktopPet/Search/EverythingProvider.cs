using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Forms = System.Windows.Forms;

namespace DesktopPet.Search;

// Implements the documented Unicode query v1 IPC; no SDK DLL, service installation or copied launcher code.
internal static class EverythingProvider
{
    [StructLayout(LayoutKind.Sequential)] internal struct CopyData { public nuint Id; public int Size; public nint Data; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint window, uint message, nint sender,
        ref CopyData data, uint flags, uint timeout, out nint result);
    internal static bool Available => FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null) != 0;

    internal static string Pattern(string query) => Pattern(SearchPlan.Literal(query));
    internal static string Pattern(SearchPlan plan, SearchScope? scope = null) => "regex:" + '"' +
        (scope?.Root is { } root ? "(?=^" + System.Text.RegularExpressions.Regex.Escape(root) + ")" : "") + string.Concat(plan.Terms
        .Select(t => "(?=.*(?:" + string.Join('|', t.Alternatives.Select(a => System.Text.RegularExpressions.Regex.Escape(a).Replace("\"", "\\x22"))) + "))")) + ".*\"";

    internal static IReadOnlyList<SearchEntry> Parse(byte[] data)
    {
        if (data.Length < 28) throw new InvalidDataException("Everything 返回的数据不完整。");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
        if (count > 800 || 28L + count * 12L > data.Length) throw new InvalidDataException("Everything 返回的结果数量无效。");
        string Read(uint offset)
        {
            if (offset < 28 + count * 12 || offset >= data.Length || offset % 2 != 0) throw new InvalidDataException("Everything 路径偏移无效。");
            var end = (int)offset;
            while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0)) end += 2;
            if (end + 1 >= data.Length) throw new InvalidDataException("Everything 路径缺少终止符。");
            return Encoding.Unicode.GetString(data, (int)offset, end - (int)offset);
        }
        var result = new List<SearchEntry>();
        for (var i = 0; i < count; i++)
        {
            var item = data.AsSpan(28 + i * 12, 12); var flags = BinaryPrimitives.ReadUInt32LittleEndian(item);
            var name = Read(BinaryPrimitives.ReadUInt32LittleEndian(item[4..]));
            var path = Read(BinaryPrimitives.ReadUInt32LittleEndian(item[8..]));
            var target = Path.Combine(path, name);
            if (Path.IsPathFullyQualified(target)) result.Add(new(name, target, (flags & 1) != 0 ? SearchKind.Folder : SearchKind.File, "Everything"));
        }
        return result;
    }

    internal static Task<IReadOnlyList<SearchEntry>> FilesAsync(string query, CancellationToken token) =>
        FilesAsync(SearchPlan.Literal(query), token);
    internal static Task<IReadOnlyList<SearchEntry>> FilesAsync(SearchPlan plan, CancellationToken token, SearchScope? scope = null) =>
        WindowsSearchProvider.OnSta(() => Query(plan, token, scope), token);

    private static IReadOnlyList<SearchEntry> Query(SearchPlan plan, CancellationToken token, SearchScope? scope)
    {
        var server = FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
        if (server == 0) throw new IOException("Everything 未运行。");
        using var context = new Forms.ApplicationContext();
        using var receiver = new ReplyWindow(server, context);
        var text = Encoding.Unicode.GetBytes(Pattern(plan, scope) + '\0'); var bytes = new byte[20 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, unchecked((uint)receiver.Handle.ToInt64()));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), ReplyWindow.ReplyId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 4); // MATCHPATH
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 800); text.CopyTo(bytes, 20);
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length); var data = new CopyData { Id = 2, Size = bytes.Length, Data = buffer };
            if (SendMessageTimeout(server, 0x4A, receiver.Handle, ref data, 2, 250, out var accepted) == 0 || accepted == 0)
                throw new IOException("Everything 未响应查询。");
        }
        finally { Marshal.FreeHGlobal(buffer); }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var timer = new Forms.Timer { Interval = 25 };
        timer.Tick += (_, _) => { if (token.IsCancellationRequested || watch.ElapsedMilliseconds >= 1500 || receiver.Done) context.ExitThread(); };
        if (!receiver.Done) { timer.Start(); Forms.Application.Run(context); }
        token.ThrowIfCancellationRequested();
        if (receiver.Error is not null) throw receiver.Error;
        return receiver.Results ?? throw new TimeoutException("Everything 查询超时。");
    }

    private sealed class ReplyWindow : Forms.NativeWindow, IDisposable
    {
        internal const uint ReplyId = 0x44505331;
        private readonly nint _server; private readonly Forms.ApplicationContext _context;
        internal IReadOnlyList<SearchEntry>? Results; internal Exception? Error; internal bool Done;
        internal ReplyWindow(nint server, Forms.ApplicationContext context)
        { _server = server; _context = context; CreateHandle(new Forms.CreateParams { Parent = new nint(-3), Caption = "DesktopPet search IPC" }); }
        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == 0x4A && message.WParam == _server)
            {
                var data = Marshal.PtrToStructure<CopyData>(message.LParam);
                if (data.Id == ReplyId)
                {
                    try
                    {
                        if (data.Size is < 28 or > 4_000_000 || data.Data == 0) throw new InvalidDataException("Everything 响应大小无效。");
                        var bytes = new byte[data.Size]; Marshal.Copy(data.Data, bytes, 0, bytes.Length); Results = Parse(bytes);
                    }
                    catch (Exception error) { Error = error; }
                    Done = true; message.Result = 1; _context.ExitThread(); return;
                }
            }
            base.WndProc(ref message);
        }
        public void Dispose() => DestroyHandle();
    }
}
