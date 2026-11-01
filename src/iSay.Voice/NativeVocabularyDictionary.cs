using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace iSay.Voice;

public sealed record NativeDictionaryHotwords(bool Available, IReadOnlyList<string> Words, uint NativeStatus);

public sealed record NativeVocabularyDictionaryOptions
{
    public required string RuntimeDirectory { get; init; }
    public required string SystemDictionaryDirectory { get; init; }
    public required string DataDirectory { get; init; }
    public string ExecutableName { get; init; } = "ImeService.Dictionary.exe";
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string> RequiredHashes { get; init; } = new Dictionary<string, string>();
    public TimeSpan PrepareTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

// 离线词典与联网识别分进程。这里只访问调用方显式指定的自家资料根。
public sealed class NativeVocabularyDictionary : IAsyncDisposable
{
    public const int MaximumFieldBytes = 1024 * 1024;
    public const int MaximumDataEntries = 4096;
    public const int MaximumDataDepth = 32;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly NativeVocabularyDictionaryOptions options;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private NamedPipeServerStream? pipe;
    private FileStream? profileLease;
    private WorkerJob? job;
    private Task drains = Task.CompletedTask;
    private Task pendingCleanup = Task.CompletedTask;
    private bool processStarted;
    private bool workerReady;
    private uint nextRequest;
    private int disposed;

    public NativeVocabularyDictionary(NativeVocabularyDictionaryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.RequiredHashes);
        ArgumentNullException.ThrowIfNull(options.Arguments);
        this.options = options with { RequiredHashes = new Dictionary<string, string>(options.RequiredHashes, StringComparer.OrdinalIgnoreCase),
            Arguments = options.Arguments.ToArray() };
        var maximumTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1L);
        if (options.PrepareTimeout <= TimeSpan.Zero || options.RequestTimeout <= TimeSpan.Zero ||
            options.PrepareTimeout > maximumTimeout || options.RequestTimeout > maximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(options), "词典请求期限必须大于零。");
    }

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try { ThrowIfDisposed(); await EnsureWorkerAsync(linked.Token).ConfigureAwait(false); }
        catch { await ReleaseWorkerAsync().ConfigureAwait(false); throw; }
        finally { serial.Release(); }
    }

    public async Task<bool> AddUserWordAsync(string reading, string word, CancellationToken cancellationToken = default)
    {
        // 官方显式添词入口需要输入编码和词两个非空字符串，不伪造未知读音。
        var first = EncodeTerm(reading);
        var second = EncodeTerm(word);
        var response = await ExecuteAsync(1, first, second, cancellationToken).ConfigureAwait(false);
        return response.NativeStatus == 2;
    }

    public async Task<bool> CommitCandidateAsync(string reading, string word, CancellationToken cancellationToken = default)
    {
        // 只接收真实组合输入的读音和提交正文，不把无法匹配的内容转成管理添词。
        var first = EncodeKeyboardTerm(reading, nameof(reading));
        var second = EncodeKeyboardTerm(word, nameof(word));
        var response = await ExecuteAsync(9, first, second, cancellationToken).ConfigureAwait(false);
        if (response.NativeStatus > 1) throw new IOException("原生候选提交返回了未知学习状态。");
        return response.NativeStatus == 1;
    }

    public async Task<bool> AddUserWordAsync(string word, CancellationToken cancellationToken = default)
    {
        // 自动读音交给原版系统词典，无法确认读音时返回不支持，不使用猜测的拼音占位。
        var response = await ExecuteAsync(7, EncodeTerm(word), [], cancellationToken).ConfigureAwait(false);
        return response.NativeStatus == 2;
    }

    public async Task<bool> AddCommonPhraseAsync(string shortcut, string phrase, CancellationToken cancellationToken = default)
    {
        if (shortcut is null || shortcut.Length > 64 || shortcut.Any(char.IsControl))
            throw new ArgumentException("常用语快捷编码无效。");
        if (string.IsNullOrWhiteSpace(phrase) || phrase.Length > 5000 || phrase.Contains('\0'))
            throw new ArgumentException("常用语正文应为1至5000个字符。");
        // 空快捷编码由原版正常生成；保留原版保存成功、但不一定进入识别热词的长度规则。
        var response = await ExecuteAsync(8, StrictUtf8.GetBytes(shortcut), StrictUtf8.GetBytes(phrase), cancellationToken).ConfigureAwait(false);
        return response.NativeStatus == 2;
    }

    public async Task<bool> AddNerResponseAsync(string json, CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync(2, EncodeJson(json), [], cancellationToken).ConfigureAwait(false);
        return response.NativeStatus != 0;
    }

    public async Task<bool> AddModificationResponseAsync(string json, CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync(3, EncodeJson(json), [], cancellationToken).ConfigureAwait(false);
        return response.NativeStatus != 0;
    }

    public async Task<NativeDictionaryHotwords> GetHotwordsAsync(CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync(4, [], [], cancellationToken).ConfigureAwait(false);
        return new(response.NativeStatus == 2, response.Words ?? [], response.NativeStatus);
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
        => (await ExecuteAsync(5, [], [], cancellationToken).ConfigureAwait(false)).NativeStatus == 0;

    private async Task<Reply> ExecuteAsync(uint command, byte[] first, byte[] second, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureWorkerAsync(linked.Token).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            deadline.CancelAfter(options.RequestTimeout);
            if (nextRequest == uint.MaxValue) throw new IOException("本轮词典请求序号已用尽，请重新创建词典会话。");
            var id = ++nextRequest;
            var header = new byte[24];
            "IDQ1"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), id);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), command);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), first.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), second.Length);
            await process!.StandardInput.BaseStream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(first, deadline.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(second, deadline.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
            var reply = await ReadReplyAsync(id, command, deadline.Token).ConfigureAwait(false);
            if (reply.BridgeStatus != 0) throw new IOException("原生词典拒绝了本次请求。");
            if (command != 4 && reply.Body.Length != 0) throw new IOException("词典写入响应包含未知正文。");
            if (command == 4)
            {
                if (reply.NativeStatus != 2 && reply.Body.Length != 0) throw new IOException("词典失败响应不应包含成功词表。");
                if (reply.NativeStatus == 2) reply = reply with { Words = ParseHotwords(reply.Body) };
            }
            return reply;
        }
        catch { await ReleaseWorkerAsync().ConfigureAwait(false); throw; }
        finally { serial.Release(); }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (workerReady && process is not null && !process.HasExited) return;
        await ReleaseWorkerAsync().ConfigureAwait(false);
        if (!pendingCleanup.IsCompleted) throw new IOException("旧词典进程尚未退出，保留资料占用并拒绝并行重开。");
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("原生词典只支持Windows。");
        var runtime = Path.GetFullPath(options.RuntimeDirectory);
        if (runtime.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("词典运行库必须使用本地路径。");
        RejectReparsePoints(Path.GetPathRoot(runtime)!, runtime);
        if (Path.GetFileName(options.ExecutableName) != options.ExecutableName ||
            !options.RequiredHashes.ContainsKey(options.ExecutableName) || options.RequiredHashes.Count == 0)
            throw new IOException("词典运行文件缺少固定哈希。");
        foreach (var pair in options.RequiredHashes)
        {
            var file = ResolveChild(runtime, pair.Key);
            RejectReparsePoints(runtime, file);
            await using var input = File.OpenRead(file);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))
                .Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new IOException("词典运行文件身份校验失败。");
        }
        var system = Path.GetFullPath(options.SystemDictionaryDirectory);
        if (!Directory.Exists(system) || !system.StartsWith(runtime.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new IOException("系统词典必须来自指定的隔离运行库。");
        RejectReparsePoints(runtime, system);
        var data = Path.GetFullPath(options.DataDirectory);
        if (data.StartsWith(@"\\", StringComparison.Ordinal) || data.TrimEnd('\\') == Path.GetPathRoot(data)?.TrimEnd('\\'))
            throw new IOException("必须指定独立的本地词典资料目录。");
        if (Within(data, runtime) || Within(runtime, data)) throw new IOException("词典资料与运行库目录必须独立。");
        // 建目录前先检查已有祖先，不能先沿联接在外部写入，再报告重解析错误。
        RejectReparsePoints(Path.GetPathRoot(data)!, data);
        Directory.CreateDirectory(data);
        RejectReparsePoints(Path.GetPathRoot(data)!, data);
        RejectDataReparseTree(data);
        var leasePath = Path.Combine(data, "iSay词典占用.lock");
        RejectReparsePoints(data, leasePath);
        profileLease = new FileStream(leasePath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var pipeName = "iSayDictionary_" + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        job = new WorkerJob();
        var start = new ProcessStartInfo(Path.Combine(runtime, options.ExecutableName))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = runtime, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in options.Arguments) start.ArgumentList.Add(argument);
        start.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        start.Environment["ISAY_DICTIONARY_RESULT_PIPE"] = @"\\.\pipe\" + pipeName;
        start.Environment["ISAY_DICTIONARY_DATA_ROOT"] = data;
        start.Environment["ISAY_DICTIONARY_SYSTEM_ROOT"] = system;
        process = new Process { StartInfo = start };
        if (!NativeHelperProcess.Start(process)) throw new IOException("词典子进程未启动。");
        processStarted = true;
        drains = Task.WhenAll(DrainAsync(process.StandardOutput.BaseStream), DrainAsync(process.StandardError.BaseStream));
        job.Attach(process);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.PrepareTimeout);
        var connected = pipe.WaitForConnectionAsync(deadline.Token);
        var exited = process.WaitForExitAsync(deadline.Token);
        var first = await Task.WhenAny(connected, exited).ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        if (first == exited)
            throw new IOException("词典子进程在就绪前退出。");
        await connected.ConfigureAwait(false);
        var ready = await ReadReplyAsync(0, 0, deadline.Token).ConfigureAwait(false);
        if (ready.BridgeStatus != 0 || ready.NativeStatus != 0 || ready.Body.Length != 0)
            throw new IOException("原生词典初始化未成功。");
        nextRequest = 0;
        workerReady = true;
    }

    private async Task<Reply> ReadReplyAsync(uint id, uint command, CancellationToken cancellationToken)
    {
        var header = new byte[24];
        await pipe!.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        if (!header.AsSpan(0, 4).SequenceEqual("IDR1"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != id ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != command || length > MaximumFieldBytes)
            throw new IOException("原生词典响应帧无效。");
        var body = new byte[(int)length];
        await pipe.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return new(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)),
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)), body);
    }

    private static IReadOnlyList<string> ParseHotwords(byte[] bytes)
    {
        using var json = JsonDocument.Parse(StrictUtf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (HasDuplicateProperties(root)) throw new IOException("原生词典词表包含重复JSON字段。");
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("hotwords", out var terms) ||
            terms.ValueKind != JsonValueKind.Array || terms.GetArrayLength() > 1000)
            throw new IOException("原生词典没有返回有效词表。");
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms.EnumerateArray())
        {
            if (term.ValueKind != JsonValueKind.Object || !term.TryGetProperty("word", out var word) || word.ValueKind != JsonValueKind.String ||
                !term.TryGetProperty("class", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "other")
                throw new IOException("原生词典词条字段无效。");
            var value = word.GetString();
            if (value is null || RecognitionVocabularyStore.NormalizeWord(value) != value || !seen.Add(value))
                throw new IOException("原生词典词条无效或重复。");
            result.Add(value);
        }
        return result.AsReadOnly();
    }

    private static byte[] EncodeTerm(string value)
    {
        if (value is null || RecognitionVocabularyStore.NormalizeWord(value) != value)
            throw new ArgumentException("词典词条或输入编码必须为1至64个有效字符。");
        return StrictUtf8.GetBytes(value);
    }

    private static byte[] EncodeKeyboardTerm(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(char.IsControl))
            throw new ArgumentException("真实候选读音和正文应为1至64个有效字符。", parameter);
        // 不改空格、分隔符或声调：交给原版转换器，只允许真实候选正文精确匹配。
        return StrictUtf8.GetBytes(value);
    }

    private static byte[] EncodeJson(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > MaximumFieldBytes || value.Contains('\0')) throw new ArgumentException("词典响应正文超过允许容量。");
        using var parsed = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 12 });
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("词典响应必须是JSON对象。");
        if (HasDuplicateProperties(parsed.RootElement)) throw new ArgumentException("词典响应包含重复JSON字段。");
        return bytes;
    }

    private static string ResolveChild(string root, string relative)
    {
        var file = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new IOException("词典运行文件超出指定目录。");
        return file;
    }

    private static void RejectReparsePoints(string root, string target)
    {
        if (!Within(target, root)) throw new IOException("词典路径未处于指定根目录。");
        for (var current = target; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("词典路径不允许重解析点。");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }

    private static bool Within(string target, string root) =>
        string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectDataReparseTree(string root)
    {
        // 只枚举自家资料的元数据，不读取词正文；先拒绝联接再深入，避免原生词典随后沿链接打开外部文件。
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var examined = 0;
        while (pending.TryPop(out var current))
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(current.Path))
            {
                if (++examined > MaximumDataEntries) throw new IOException("词典资料项超过允许数量，未启动原生词典。");
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("词典资料内部不允许重解析点。");
                if ((attributes & FileAttributes.Directory) == 0) continue;
                if (current.Depth >= MaximumDataDepth) throw new IOException("词典资料目录超过允许深度，未启动原生词典。");
                pending.Push((file, current.Depth + 1));
            }
        }
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
                if (!names.Add(item.Name) || HasDuplicateProperties(item.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }

    private static async Task DrainAsync(Stream stream)
    {
        try { await stream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private async Task ReleaseWorkerAsync()
    {
        var oldProcess = process; process = null;
        var oldStarted = processStarted; processStarted = false; workerReady = false;
        var oldPipe = pipe; pipe = null;
        var oldJob = job; job = null;
        var oldLease = profileLease; profileLease = null;
        var exited = oldProcess is null || !oldStarted;
        try
        {
            if (oldProcess is not null && oldStarted)
            {
                try { oldProcess.StandardInput.Close(); } catch (Exception error) when (error is InvalidOperationException or IOException or ObjectDisposedException) { }
                try { await oldProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (TimeoutException) { oldJob?.Dispose(); if (!oldProcess.HasExited) oldProcess.Kill(true); }
                await oldProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                exited = oldProcess.HasExited;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception) { }
        finally
        {
            try { oldJob?.Dispose(); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            if (oldPipe is not null)
                try { await oldPipe.DisposeAsync().ConfigureAwait(false); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            try { await drains.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or IOException or ObjectDisposedException) { }
            if (!exited && oldProcess is not null)
                try { exited = oldProcess.HasExited; } catch (InvalidOperationException) { }
            if (exited) { oldProcess?.Dispose(); oldLease?.Dispose(); }
            else pendingCleanup = RetainLeaseUntilExitAsync(oldProcess!, oldLease);
        }
    }

    private static async Task RetainLeaseUntilExitAsync(Process oldProcess, FileStream? oldLease)
    {
        // 无法确认退出时，宁可继续持有独占租约，避免两个词典进程同时改写同一资料根。
        while (true)
        {
            try
            {
                if (oldProcess.HasExited) break;
                await oldProcess.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            { await Task.Delay(250).ConfigureAwait(false); }
        }
        oldProcess.Dispose(); oldLease?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        await serial.WaitAsync().ConfigureAwait(false);
        try { await ReleaseWorkerAsync().ConfigureAwait(false); }
        finally { serial.Release(); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    private sealed record Reply(uint BridgeStatus, uint NativeStatus, byte[] Body, IReadOnlyList<string>? Words = null);

    private sealed class WorkerJob : IDisposable
    {
        private readonly SafeFileHandle handle;
        public WorkerJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("无法创建词典进程作业。"); }
            var value = new ExtendedLimitInformation(); value.Basic.LimitFlags = 0x2000;
            if (!SetInformationJobObject(handle, 9, ref value, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            { handle.Dispose(); throw new IOException("无法设置词典进程退出清理。"); }
        }
        public void Attach(Process value)
        { if (!AssignProcessToJobObject(handle, value.Handle)) throw new IOException("无法关联词典进程作业。"); }
        public void Dispose() => handle.Dispose();
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation { public BasicLimitInformation Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle handle, int kind, ref ExtendedLimitInformation value, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle handle, IntPtr process);
    }
}
