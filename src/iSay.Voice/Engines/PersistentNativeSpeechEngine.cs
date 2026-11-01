using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace iSay.Voice.Engines;

// 只复用自家原生对象的正常Stop/Start；段间不重放音频、不复制任何官方身份资料。
internal sealed class PersistentNativeSpeechEngine(SpeechEngineOptions options) : IStreamingSpeechEngine, IUpdatableVocabularySpeechEngine
{
    internal const string AdapterSha256 = "E226B55402E1E76D565A161FAD45EC9CCD80518487B09133FDC8373A5F8B3623";
    internal const string UpdatableAdapterSha256 = "8424B0A9626F89F0395C5D941E1F13689005C612B5970ADA1D63F6E78A248E31";
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly object stateLock = new();
    private readonly CancellationTokenSource lifetime = new();
    private Worker? worker;
    private Recording? recording;
    private string lastCompletedText = "";
    private Guid lastCompletedSession;
    private int disposed;
    private string contextBase64 = AsrVocabularyContext.BuildBase64(options.Vocabulary);

    public event EventHandler<SpeechResult>? ResultReceived;

    public async Task<bool> TryUpdateVocabularyAsync(IReadOnlyList<string> words, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(words);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            Worker? current;
            lock (stateLock)
            {
                if (recording is not null) return false;
                current = worker;
            }
            if (current is null || !current.SupportsVocabularyUpdates || current.Faulted || current.Process.HasExited) return false;
            var next = AsrVocabularyContext.BuildBase64(words);
            if (next == contextBase64) return true;
            await WriteContextFileAsync(current.ContextFilePath!, next, linked.Token).ConfigureAwait(false);
            contextBase64 = next;
            current.ContextBase64Length = next.Length;
            return true;
        }
        finally { lifecycle.Release(); }
    }

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        try { ThrowIfDisposed(); await EnsureWorkerAsync(linked.Token).ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    public async Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("识别会话编号不能为空。", nameof(sessionId));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        var startedHere = false;
        try
        {
            ThrowIfDisposed();
            lock (stateLock)
            {
                if (recording is not null) throw new InvalidOperationException("已有录音正在进行。");
                if (sessionId == lastCompletedSession) throw new ArgumentException("下一段录音必须使用新的会话编号。", nameof(sessionId));
            }
            var current = await EnsureWorkerAsync(linked.Token).ConfigureAwait(false);
            var next = new Recording(sessionId, current.Sequence + 1, current.Current) { ExpectedContextLength = current.ContextBase64Length };
            lock (stateLock) { recording = next; lastCompletedText = ""; }
            startedHere = true;
            await writer.WaitAsync(linked.Token).ConfigureAwait(false);
            try { await next.Pipe.WriteAsync(CreateHeader(), linked.Token).ConfigureAwait(false); }
            finally { writer.Release(); }
            if (current.SupportsVocabularyUpdates)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                deadline.CancelAfter(options.PrepareTimeout);
                await next.ContextApplied.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // 重复Start只拒绝新请求，不能把已经进行中的录音一起结束。
            if (startedHere) await ReleaseWorkerAsync().ConfigureAwait(false);
            throw;
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm16KhzMono, CancellationToken cancellationToken = default)
    {
        if ((pcm16KhzMono.Length & 1) != 0) throw new ArgumentException("16位PCM音频字节数必须是偶数。", nameof(pcm16KhzMono));
        if (pcm16KhzMono.IsEmpty) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await writer.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            Recording? current;
            lock (stateLock) current = recording;
            if (current is null || current.Stopping || current.Completed) throw new InvalidOperationException("录音尚未开始或已经结束。");
            await current.Pipe.WriteAsync(pcm16KhzMono, linked.Token).ConfigureAwait(false);
        }
        finally { writer.Release(); }
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        Recording? current = null;
        try
        {
            ThrowIfDisposed();
            Worker? native;
            lock (stateLock) { current = recording; native = worker; }
            if (current is null) return lastCompletedText;
            if (native is null) throw new IOException("识别辅助进程已退出。");
            current.Stopping = true;
            await writer.WaitAsync(linked.Token).ConfigureAwait(false);
            try { await current.Pipe.DisposeAsync().ConfigureAwait(false); }
            finally { writer.Release(); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            deadline.CancelAfter(options.FinalTimeout);
            var code = await current.Done.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (code != 0) throw new IOException("原生识别未能正常收尾，已保留最后转写。");
            // 原生终态在下一段打开前发出；预先排队的第二条管道确保不发生打开空窗。
            await native.NextConnected.WaitAsync(deadline.Token).ConfigureAwait(false);
            var completed = current.FinalText ?? current.LatestText;
            native.Current = native.Next;
            native.Next = CreatePipe(native.PipeName);
            native.NextConnected = native.Next.WaitForConnectionAsync(lifetime.Token);
            native.Sequence = current.Sequence;
            lock (stateLock)
            {
                if (!ReferenceEquals(worker, native) || !ReferenceEquals(recording, current)) throw new IOException("识别会话在收尾时发生变化。");
                recording = null;
                lastCompletedText = completed;
                lastCompletedSession = current.Id;
            }
            if (!current.FinalSeen) Raise(current, SpeechResultKind.Final, completed);
            return completed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            if (current is not null) Raise(current, SpeechResultKind.Error, current.LatestText, "等待识别收尾超时，已保留最后转写。");
            await ReleaseWorkerAsync().ConfigureAwait(false);
            throw new TimeoutException("等待识别收尾超时。");
        }
        catch
        {
            if (current is not null && !linked.IsCancellationRequested) Raise(current, SpeechResultKind.Error, current.LatestText, "识别连接中断，已保留最后转写。");
            await ReleaseWorkerAsync().ConfigureAwait(false);
            throw;
        }
        finally { lifecycle.Release(); }
    }

    private async Task<Worker> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        Worker? existing;
        lock (stateLock) existing = worker;
        if (existing is not null && !existing.Process.HasExited && !existing.Faulted) return existing;
        await ReleaseWorkerAsync().ConfigureAwait(false);
        using var startup = await NativeSdkInitializationGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("原生识别仅支持Windows。");
        var directory = Path.GetFullPath(options.NativeRuntimeDirectory);
        if (options.NativeExecutableName != "ImeService.Persistent.exe") throw new InvalidOperationException("持久识别辅助程序名不正确。");
        var executable = Path.Combine(directory, options.NativeExecutableName);
        string adapterHash;
        await using (var stream = File.OpenRead(executable))
        {
            adapterHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (adapterHash != AdapterSha256 && adapterHash != UpdatableAdapterSha256)
                throw new InvalidOperationException("持久识别运行库校验失败，未启动。");
        }
        var name = "iSayPersistentAsr_" + Guid.NewGuid().ToString("N");
        var first = CreatePipe(name);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--test-sami");
        start.ArgumentList.Add("--wav");
        start.ArgumentList.Add(@"\\.\pipe\" + name);
        start.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        start.Environment.Remove("ISAY_ASR_CONTEXT_B64");
        start.Environment.Remove("ISAY_SYNTHETIC_ASR_CONTEXT_B64");
        start.Environment.Remove("ISAY_ASR_CONTEXT_FILE");
        if (contextBase64.Length > 0) start.Environment["ISAY_ASR_CONTEXT_B64"] = contextBase64;
        var updatable = adapterHash == UpdatableAdapterSha256;
        var contextFile = updatable ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "iSay", "runtime-context", Guid.NewGuid().ToString("N"), "hotwords.b64") : null;
        var native = new Worker(name, first, new Process { StartInfo = start })
        { SupportsVocabularyUpdates = updatable, ContextFilePath = contextFile, ContextBase64Length = contextBase64.Length };
        lock (stateLock) worker = native;
        try
        {
            if (contextFile is not null)
            {
                await WriteContextFileAsync(contextFile, contextBase64, cancellationToken).ConfigureAwait(false);
                start.Environment["ISAY_ASR_CONTEXT_FILE"] = contextFile;
            }
            native.Job = new ProcessJob();
            if (!NativeHelperProcess.Start(native.Process)) throw new IOException("无法启动持久识别辅助进程。");
            native.Job.Attach(native.Process);
            native.OutputTask = ReadOutputAsync(native);
            native.ErrorTask = DrainAsync(native.Process.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.PrepareTimeout);
            var connected = first.WaitForConnectionAsync(deadline.Token);
            var exited = native.Process.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited)
                throw new IOException("识别辅助进程在就绪前退出。");
            await connected.ConfigureAwait(false);
            native.Next = CreatePipe(name);
            native.NextConnected = native.Next.WaitForConnectionAsync(lifetime.Token);
            return native;
        }
        catch
        {
            await ReleaseWorkerAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReadOutputAsync(Worker native)
    {
        try
        {
            while (await native.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Recording? current;
                lock (stateLock) current = ReferenceEquals(worker, native) ? recording : null;
                if (line.StartsWith("ISAY_ASR_CONTEXT_APPLIED|", StringComparison.Ordinal))
                {
                    var lengthText = line.AsSpan("ISAY_ASR_CONTEXT_APPLIED|".Length);
                    if (!native.SupportsVocabularyUpdates || current is null || current.Completed || current.Stopping
                        || current.ContextApplied.Task.IsCompleted || lengthText.Length != 8
                        || !int.TryParse(lengthText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var length)
                        || length != current.ExpectedContextLength)
                        throw new IOException("段间识别词表回执与当前录音不匹配。");
                    current.ContextApplied.TrySetResult();
                    continue;
                }
                if (line.StartsWith("ISAY_ASR_DONE|", StringComparison.Ordinal))
                {
                    var fields = line.Split('|');
                    if (fields.Length != 3 || fields[1].Length != 8 || !uint.TryParse(fields[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var sequence)
                        || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code is < 0 or > 9
                        || current is null || current.Sequence != sequence || current.Completed || !current.Stopping)
                        throw new IOException("原生识别段终态与当前录音不匹配。");
                    current.Completed = true;
                    current.Done.TrySetResult(code);
                    continue;
                }
                if (!line.StartsWith("DBR|", StringComparison.Ordinal) || current is null || current.Completed) continue;
                var separator = line.IndexOf('|', 4);
                if (separator < 0) continue;
                var text = line[(separator+1)..];
                var flag = line.AsSpan(4, separator-4);
                if (!flag.SequenceEqual("0") && !flag.SequenceEqual("1")) continue;
                var final = flag.SequenceEqual("1");
                // 最终结果确认后冻结本段正文，晚到快照或重复终态都不能使其倒退。
                if (current.FinalSeen) continue;
                current.LatestText = text;
                if (final)
                {
                    if (current.FinalSeen) continue;
                    current.FinalSeen = true;
                    current.FinalText = text;
                }
                else
                {
                    if (current.LastPartial == text) continue;
                    current.LastPartial = text;
                }
                Raise(current, final ? SpeechResultKind.Final : SpeechResultKind.Partial, text);
            }
            FailWorker(native, "识别辅助进程已断开。");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { FailWorker(native, "识别辅助进程连接异常。"); }
    }

    private void FailWorker(Worker native, string message)
    {
        Recording? current;
        lock (stateLock)
        {
            native.Faulted = true;
            current = ReferenceEquals(worker, native) ? recording : null;
        }
        if (current is null || current.Completed) return;
        current.Done.TrySetException(new IOException(message));
        if (native.SupportsVocabularyUpdates) current.ContextApplied.TrySetException(new IOException(message));
        Raise(current, SpeechResultKind.Error, current.LatestText, message);
    }

    private static async Task DrainAsync(StreamReader stream)
    {
        try { while (await stream.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void Raise(Recording current, SpeechResultKind kind, string text, string? error = null)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var handlers = ResultReceived;
        if (handlers is null) return;
        foreach (EventHandler<SpeechResult> handler in handlers.GetInvocationList())
        {
            try { handler(this, new(current.Id, kind, text, error)); }
            catch { }
        }
    }

    private async Task ReleaseWorkerAsync()
    {
        Worker? old;
        lock (stateLock) { old = worker; worker = null; recording = null; }
        if (old is null) return;
        try { old.Current.Dispose(); } catch (IOException) { }
        if (!ReferenceEquals(old.Current, old.Next)) try { old.Next.Dispose(); } catch (IOException) { }
        old.Job?.Dispose();
        try { if (!old.Process.HasExited) old.Process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        try { await old.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
        try { await Task.WhenAll(old.OutputTask, old.ErrorTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        old.Process.Dispose();
        if (old.ContextFilePath is not null)
        {
            try { File.Delete(old.ContextFilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(Path.GetDirectoryName(old.ContextFilePath)!); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task WriteContextFileAsync(string path, string context, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 每进程唯一目录，文件只保存本段热词Base64；同目录原子替换，不携带语音正文或身份资料。
            await File.WriteAllTextAsync(temporary, context, Encoding.ASCII, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static NamedPipeServerStream CreatePipe(string name) => new(name, PipeDirection.Out, 2,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);

    private static byte[] CreateHeader()
    {
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), int.MaxValue);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), int.MaxValue-36);
        return header;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        Worker? current;
        lock (stateLock) current = worker;
        // 先关闭进程作业以打断阻塞写入或SDK初始化，再等待生命周期互斥。
        current?.Job?.Dispose();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await ReleaseWorkerAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private sealed class Recording(Guid id, uint sequence, NamedPipeServerStream pipe)
    {
        public Guid Id { get; } = id;
        public uint Sequence { get; } = sequence;
        public NamedPipeServerStream Pipe { get; } = pipe;
        public TaskCompletionSource<int> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContextApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExpectedContextLength;
        public volatile bool Stopping;
        public volatile bool Completed;
        public bool FinalSeen;
        public string LatestText = "";
        public string? FinalText;
        public string? LastPartial;
    }

    private sealed class Worker(string pipeName, NamedPipeServerStream first, Process process)
    {
        public string PipeName { get; } = pipeName;
        public NamedPipeServerStream Current = first;
        public NamedPipeServerStream Next = first;
        public Task NextConnected = Task.CompletedTask;
        public Process Process { get; } = process;
        public ProcessJob? Job;
        public Task OutputTask = Task.CompletedTask;
        public Task ErrorTask = Task.CompletedTask;
        public uint Sequence;
        public volatile bool Faulted;
        public bool SupportsVocabularyUpdates;
        public string? ContextFilePath;
        public int ContextBase64Length;
    }

    private sealed class ProcessJob : IDisposable
    {
        private readonly SafeFileHandle handle;
        public ProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new IOException("无法建立识别进程保护。");
            var info = new ExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            { handle.Dispose(); throw new IOException("无法设置识别进程保护。"); }
        }
        public void Attach(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle)) throw new IOException("无法关联识别进程保护。");
        }
        public void Dispose() => handle.Dispose();
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
