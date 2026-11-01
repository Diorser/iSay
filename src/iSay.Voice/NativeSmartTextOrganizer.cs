using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace iSay.Voice;

public interface INativeTextOrganizer : IAsyncDisposable
{
    bool IsAvailable { get; }
    Task PrepareAsync(CancellationToken cancellationToken = default);
    Task<FormattingResult> OrganizeAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class NativeSmartTextOrganizer : INativeTextOrganizer
{
    private readonly object stateLock = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly NativeSmartOrganizerOptions options;
    private readonly Lazy<bool> runtimeValid;
    private WorkerSlot? warm;
    private WorkerSlot? active;
    private volatile bool disposed;

    public NativeSmartTextOrganizer(string? runtimeDirectory = null, string? dataDirectoryRoot = null)
        : this(NativeSmartOrganizerOptions.Production(runtimeDirectory, dataDirectoryRoot)) { }

    internal NativeSmartTextOrganizer(NativeSmartOrganizerOptions options)
    {
        this.options = options;
        runtimeValid = new Lazy<bool>(() => ValidateRuntime(options), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => !disposed && runtimeValid.Value;

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkerSlot? slot;
        lock (stateLock)
        {
            if (disposed || !runtimeValid.Value) return;
            slot = warm ?? active;
            if (slot is null) slot = warm = StartSlot();
        }
        try { await slot.Started.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // 预热失败不阻塞主界面；真正整理时重建一次并保留原文作为降级结果。
            lock (stateLock) { if (ReferenceEquals(warm, slot)) warm = null; }
            await slot.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<FormattingResult> OrganizeAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) return new(text, "内置智能整理无需修改");
        if (text.Contains('\0')) return Fallback(text, "原文包含无效字符，已保留原文。");
        byte[] body;
        try { body = NativeSmartOrganizerOptions.StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException) { return Fallback(text, "原文包含无效字符，已保留原文。"); }
        if (body.Length > NativeSmartOrganizerOptions.MaximumTextBytes)
            return Fallback(text, "文字超过内置整理容量，已保留原文。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(options.OperationTimeout);
        var entered = false;
        WorkerSlot? slot = null;
        Worker? claimedWorker = null;
        try
        {
            await operationGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            lock (stateLock)
            {
                if (disposed) throw new OperationCanceledException(lifetime.Token);
                if (!runtimeValid.Value) return Fallback(text, "内置智能整理组件不可用，已保留原文。");
                if (warm?.Started.IsFaulted == true || warm?.Started.IsCanceled == true)
                {
                    _ = warm.DisposeAsync();
                    warm = null;
                }
                slot = warm ?? StartSlot();
                options.TestLifecycleObserver?.Invoke("CLAIM " + slot.TraceId);
                active = slot;
                warm = null;
            }
            var worker = claimedWorker = await slot.Started.WaitAsync(options.ReadyWaitTimeout, deadline.Token).ConfigureAwait(false);
            lock (stateLock)
            {
                // 当前进程先取得稳定数据槽并READY，下一段预热才不会抢占当前段仍在等待的槽。
                if (!options.Persistent && !disposed && ReferenceEquals(active, slot)) warm ??= StartSlot();
            }
            var result = await worker.ExecuteAsync(body, deadline.Token).ConfigureAwait(false);
            return result.NoRewrite ? new(text, "内置智能整理无需修改") : new(result.Text!, "内置智能整理");
        }
        catch (TimeoutException) when (claimedWorker is null) { return Fallback(text, "内置整理仍在后台准备，本段已保留原文，下次自动使用就绪组件。"); }
        catch (NativeDataLeaseException ex) { return Fallback(text, ex.Warning); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { claimedWorker?.Invalidate(); return Fallback(text, disposed ? "内置整理已停止，已保留原文。" : "内置智能整理超时，已保留原文。"); }
        catch { claimedWorker?.Invalidate(); return Fallback(text, "内置智能整理失败，已保留原文。"); }
        finally
        {
            if (slot is not null)
            {
                var retain = false;
                lock (stateLock)
                {
                    // 取消只取消调用方等待；旧结果在独立机器通道排空，下一段不得读到旧正文。
                    retain = options.Persistent && !disposed && warm is null &&
                        (claimedWorker?.CanReuse == true || claimedWorker is null && !slot.Started.IsFaulted && !slot.Started.IsCanceled);
                    if (retain) warm = slot;
                    if (ReferenceEquals(active, slot)) active = null;
                }
                if (!retain) await slot.DisposeAsync().ConfigureAwait(false);
            }
            if (entered) operationGate.Release();
        }
    }

    private WorkerSlot StartSlot() => new(options, lifetime.Token);
    private static FormattingResult Fallback(string text, string warning) => new(text, "基础整理", warning);

    internal static ProcessStartInfo CreateWorkerStartInfo(NativeSmartOrganizerOptions options, string dataDirectory, string pipeName)
    {
        var info = new ProcessStartInfo(Path.Combine(options.RuntimeDirectory, options.ExecutableName))
        {
            WorkingDirectory = options.RuntimeDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.Environment["ISAY_SMART_RESULT_PIPE"] = @"\\.\pipe\" + pipeName;
        info.Environment["ISAY_SMART_DATA_ROOT"] = dataDirectory;
        // 此独立CLI不需要管理员权限，不能因原版清单弹出提权或启动主服务。
        info.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        foreach (var argument in options.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static bool ValidateRuntime(NativeSmartOrganizerOptions options)
    {
        try
        {
            if (options.RequiredHashes.Count == 0) return false;
            foreach (var file in options.RequiredHashes)
            {
                var path = Path.GetFullPath(Path.Combine(options.RuntimeDirectory, file.Key));
                var root = Path.GetFullPath(options.RuntimeDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
                using var stream = File.OpenRead(path);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Value, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        WorkerSlot? current;
        WorkerSlot? prepared;
        lock (stateLock)
        {
            if (disposed) return;
            disposed = true;
            current = active;
            prepared = warm;
            warm = null;
        }
        lifetime.Cancel();
        if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
        if (prepared is not null && !ReferenceEquals(prepared, current)) await prepared.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class WorkerSlot
    {
        private readonly CancellationTokenSource cancellation;
        private readonly object disposeLock = new();
        private Task? disposal;
        private DirectoryLease? profile;
        public Task<Worker> Started { get; }
        public string TraceId { get; } = Guid.NewGuid().ToString("N")[..6];

        public WorkerSlot(NativeSmartOrganizerOptions options, CancellationToken lifetime)
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            // 哈希核验、进程创建和原生初始化全部留在后台线程。
            Started = Task.Run(async () =>
            {
                Worker? worker = null;
                DirectoryLease? bootstrap = null;
                IDisposable? startupLease = null;
                try
                {
                    options.TestLifecycleObserver?.Invoke(TraceId + " PROFILE_WAIT");
                    using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                    startup.CancelAfter(options.StartupTimeout);
                    try { profile = await DirectoryLease.AcquireProfileAsync(options.DataDirectoryRoot, startup.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    { throw new NativeDataLeaseException("智能整理数据槽正在使用，已保留原文。请稍后重试，或完全退出其他iSay实例。"); }
                    options.TestLifecycleObserver?.Invoke(TraceId + " PROFILE " + Path.GetFileName(profile.DirectoryPath));
                    // 排队不消耗本进程的初始化期限；等待期间退出取消仍立即生效。
                    startup.CancelAfter(Timeout.InfiniteTimeSpan);
                    startupLease = await NativeSdkInitializationGate.AcquireAsync(cancellation.Token).ConfigureAwait(false);
                    startup.CancelAfter(options.StartupTimeout);
                    options.TestLifecycleObserver?.Invoke(TraceId + " BOOTSTRAP_WAIT");
                    bootstrap = await DirectoryLease.AcquireBootstrapAsync(options.BootstrapDirectoryRoot, startup.Token).ConfigureAwait(false);
                    if (!ValidateRuntime(options)) throw new IOException("内置整理运行时校验失败。");
                    startup.Token.ThrowIfCancellationRequested();
                    worker = new Worker(options, profile.DirectoryPath);
                    worker.Start(identity => { profile.RecordWorker(identity); bootstrap.RecordWorker(identity); options.TestLifecycleObserver?.Invoke(TraceId + " START " + identity.Pid); });
                    await worker.Ready.WaitAsync(startup.Token).ConfigureAwait(false);
                    // READY证明确已离开bootstrap阶段；此时才允许另一个进程开始初始化。
                    bootstrap.ReleaseKnownSafe();
                    options.TestLifecycleObserver?.Invoke(TraceId + " READY");
                    return worker;
                }
                catch (Exception ex)
                {
                    options.TestLifecycleObserver?.Invoke(TraceId + " FAIL " + ex.GetType().Name);
                    if (worker is not null) await worker.DisposeAsync().ConfigureAwait(false);
                    if (worker is null || worker.ExitConfirmed)
                    {
                        bootstrap?.ReleaseKnownSafe();
                        profile?.ReleaseKnownSafe();
                    }
                    profile?.Dispose();
                    options.TestLifecycleObserver?.Invoke(TraceId + " FAILED_CLEANED");
                    throw;
                }
                finally
                {
                    bootstrap?.Dispose();
                    startupLease?.Dispose();
                }
            });
        }

        public ValueTask DisposeAsync()
        {
            lock (disposeLock) return new(disposal ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            cancellation.Cancel();
            try
            {
                var worker = await Started.ConfigureAwait(false);
                await worker.DisposeAsync().ConfigureAwait(false);
                if (worker.ExitConfirmed) profile?.ReleaseKnownSafe();
            }
            catch { }
            profile?.Dispose();
            cancellation.Dispose();
        }
    }

    private sealed record ProcessIdentity(int Pid, long StartTicks)
    {
        public static ProcessIdentity Capture(Process process) => new(process.Id, process.StartTime.ToUniversalTime().Ticks);
        public bool ConfirmExited()
        {
            try
            {
                using var process = Process.GetProcessById(Pid);
                return process.HasExited || process.StartTime.ToUniversalTime().Ticks != StartTicks;
            }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
            catch { return false; }
        }
    }

    private sealed record LeaseRecord(ProcessIdentity Parent, ProcessIdentity? Worker);
    private sealed class NativeDataLeaseException(string warning) : IOException
    {
        public string Warning { get; } = warning;
    }

    private sealed class DirectoryLease : IDisposable
    {
        private readonly FileStream stream;
        private readonly ProcessIdentity parent;
        private bool closed;
        public string DirectoryPath { get; }

        private DirectoryLease(FileStream stream, string directory)
        {
            this.stream = stream;
            DirectoryPath = directory;
            using var process = Process.GetCurrentProcess();
            parent = ProcessIdentity.Capture(process);
            Write(new(parent, null));
        }

        public static async Task<DirectoryLease> AcquireProfileAsync(string root, CancellationToken cancellationToken)
        {
            var fullRoot = ValidateRoot(root);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uncertain = 0;
                for (var index = 1; index <= 2; index++)
                {
                    var directory = Path.Combine(fullRoot, "slot" + index);
                    var lease = TryAcquire(directory, ".isay-worker.lock", out var unknown);
                    if (lease is not null) return lease;
                    if (unknown) uncertain++;
                }
                if (uncertain == 2) throw new NativeDataLeaseException("智能整理数据槽状态待核验，已保留原文。请先完全退出iSay并检查残留整理进程，勿直接删除租约文件。");
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            }
        }

        public static async Task<DirectoryLease> AcquireBootstrapAsync(string root, CancellationToken cancellationToken)
        {
            var fullRoot = ValidateRoot(root);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lease = TryAcquire(fullRoot, ".isay-bootstrap.lock", out var unknown);
                if (lease is not null) return lease;
                if (unknown) throw new NativeDataLeaseException("智能整理初始化锁状态待核验，已保留原文。请先完全退出iSay并检查残留整理进程。");
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            }
        }

        private static string ValidateRoot(string root)
        {
            if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("内置整理数据目录必须是本地绝对路径。");
            var full = Path.GetFullPath(root);
            if (NativeSmartOrganizerOptions.StrictUtf8.GetByteCount(Path.Combine(full, "slot2")) > 4095)
                throw new IOException("内置整理数据目录过长。");
            return full;
        }

        private static DirectoryLease? TryAcquire(string directory, string fileName, out bool unknown)
        {
            unknown = false;
            Directory.CreateDirectory(directory);
            FileStream? handle = null;
            try
            {
                handle = new FileStream(Path.Combine(directory, fileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (handle.Length > 0)
                {
                    if (handle.Length > 4096) { unknown = true; handle.Dispose(); return null; }
                    var bytes = new byte[(int)handle.Length];
                    handle.ReadExactly(bytes);
                    LeaseRecord? record;
                    try { record = JsonSerializer.Deserialize<LeaseRecord>(bytes); }
                    catch (JsonException) { unknown = true; handle.Dispose(); return null; }
                    // 父进程异常退出也不能等同子进程退出。缺少可验证子进程身份时保守占用。
                    if (record?.Worker is null || record.Worker.Pid <= 0 || record.Worker.StartTicks <= 0)
                    { unknown = true; handle.Dispose(); return null; }
                    if (!record.Worker.ConfirmExited()) { handle.Dispose(); return null; }
                }
                return new DirectoryLease(handle, directory);
            }
            catch (IOException) { handle?.Dispose(); return null; }
            catch { handle?.Dispose(); throw; }
        }

        public void RecordWorker(ProcessIdentity identity) => Write(new(parent, identity));

        private void Write(LeaseRecord record)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            stream.Position = 0;
            stream.Write(bytes);
            stream.SetLength(bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        public void ReleaseKnownSafe()
        {
            if (closed) return;
            try { stream.SetLength(0); stream.Flush(flushToDisk: true); }
            catch (IOException) { }
        }

        public void Dispose()
        {
            if (closed) return;
            closed = true;
            stream.Dispose();
        }
    }

    private sealed record WorkerResult(string? Text, bool NoRewrite);

    private sealed class Worker : IAsyncDisposable
    {
        private const string Prefix = "ISAY_SMART_";
        private readonly Process process;
        private readonly NamedPipeServerStream machinePipe;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception> protocolFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task stdout = Task.CompletedTask;
        private Task stderr = Task.CompletedTask;
        private Task machineOutput = Task.CompletedTask;
        private readonly ProcessJob job;
        private readonly object disposeLock = new();
        private Task? disposal;
        private readonly bool persistent;
        private TaskCompletionSource<WorkerResult>? response;
        private readonly object protocolLock = new();
        public bool CanReuse => persistent && !invalidated && !protocolFailure.Task.IsCompleted && !process.HasExited;
        public void Invalidate() => invalidated = true;
        private bool hasReady;
        private bool hasResult;
        private volatile bool requestSent;
        private volatile bool invalidated;
        private Task processExit = Task.CompletedTask;
        private WorkerResult? parsedResult;
        private bool processStarted;
        public bool ExitConfirmed { get; private set; }
        public Task Ready => ready.Task;

        public Worker(NativeSmartOrganizerOptions options, string dataDirectory)
        {
            persistent = options.Persistent;
            job = new ProcessJob();
            var pipeName = "iSaySmart_" + Guid.NewGuid().ToString("N");
            try
            {
                machinePipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch { job.Dispose(); throw; }
            var info = CreateWorkerStartInfo(options, dataDirectory, pipeName);
            process = new Process { StartInfo = info };
        }

        public void Start(Action<ProcessIdentity> recordStarted)
        {
            if (!NativeHelperProcess.Start(process)) throw new IOException("内置整理进程无法启动。");
            processStarted = true;
            processExit = process.WaitForExitAsync();
            recordStarted(ProcessIdentity.Capture(process));
            job.Attach(process);
            machineOutput = ReadMachineOutputAsync();
            stdout = DrainAsync(process.StandardOutput.BaseStream);
            stderr = DrainAsync(process.StandardError.BaseStream);
        }

        private async Task ReadMachineOutputAsync()
        {
            try
            {
                await machinePipe.WaitForConnectionAsync().ConfigureAwait(false);
                await ReadOutputAsync(machinePipe).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
                protocolFailure.TrySetResult(ex);
                throw;
            }
        }

        public async Task<WorkerResult> ExecuteAsync(byte[] text, CancellationToken cancellationToken)
        {
            if (persistent)
            {
                // 上次等待被取消时仍须等到其终态和空闲回执；此时不会向新请求发送任何正文。
                var previous = response;
                if (previous is not null)
                {
                    var previousTerminal = await Task.WhenAny(previous.Task, protocolFailure.Task, processExit).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    if (!ReferenceEquals(previousTerminal, previous.Task)) throw new IOException("上一段整理进程已结束或协议失败。");
                    await previous.Task.ConfigureAwait(false);
                }
                lock (protocolLock)
                {
                    response = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    hasResult = false; parsedResult = null;
                }
            }
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)text.Length);
            requestSent = true;
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.WriteAsync(text, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 半帧取消会破坏输入边界，必须销毁该进程；完整帧发送后才允许取消等待并保留复用。
                invalidated = true;
                throw;
            }
            if (persistent)
            {
                var result = response!.Task;
                var stopped = processExit;
                var terminal = await Task.WhenAny(result, protocolFailure.Task, stopped).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (ReferenceEquals(terminal, protocolFailure.Task)) throw await protocolFailure.Task.ConfigureAwait(false);
                if (!ReferenceEquals(terminal, result)) throw new IOException("常驻整理进程提前结束。");
                return await result.ConfigureAwait(false);
            }
            process.StandardInput.Close();
            var exited = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(exited, protocolFailure.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(completed, protocolFailure.Task)) throw await protocolFailure.Task.ConfigureAwait(false);
            await exited.ConfigureAwait(false);
            await Task.WhenAll(machineOutput, stdout, stderr).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || !hasResult) throw new IOException("内置整理未成功结束。");
            return parsedResult ?? throw new IOException("内置整理没有正文。");
        }

        private async Task ReadOutputAsync(Stream stream)
        {
            var buffer = new byte[8192];
            var line = new List<byte>();
            try
            {
                while (true)
                {
                    var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (count == 0) break;
                    for (var index = 0; index < count; index++)
                    {
                        var value = buffer[index];
                        if (value == (byte)'\n')
                        {
                            if (line.Count < Prefix.Length) throw new IOException("内置整理机器协议不完整。");
                            ParseLine(line);
                            line.Clear();
                            continue;
                        }
                        if (line.Count < Prefix.Length && value != (byte)Prefix[line.Count])
                            throw new IOException("内置整理机器协议前缀无效。");
                        if (line.Count >= NativeSmartOrganizerOptions.MaximumTextBytes * 2 + 128)
                            throw new IOException("内置整理协议结果超出容量。");
                        line.Add(value);
                    }
                }
                if (line.Count > 0) throw new IOException("内置整理机器协议被截断。");
                if (!hasReady) throw new IOException("内置整理未准备就绪。");
                if (!hasResult && requestSent) throw new IOException("内置整理没有完整结果。");
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
                protocolFailure.TrySetResult(ex);
                throw;
            }
        }

        private void ParseLine(List<byte> bytes)
        {
            if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
            if (bytes.Any(value => value > 127)) throw new IOException("内置整理协议无效。");
            var line = Encoding.ASCII.GetString(bytes.ToArray());
            lock (protocolLock) ParseProtocolLine(line);
        }

        private void ParseProtocolLine(string line)
        {
            if (persistent && line == "ISAY_SMART_IDLE")
            {
                if (!hasReady || !requestSent || !hasResult || parsedResult is null || response is null || response.Task.IsCompleted)
                    throw new IOException("常驻整理空闲回执次序错误。");
                requestSent = false;
                response.TrySetResult(parsedResult);
                return;
            }
            if (line == "ISAY_SMART_READY" && !hasReady && !hasResult)
            { hasReady = true; ready.TrySetResult(); return; }
            if (!hasReady || hasResult || !requestSent) throw new IOException("内置整理协议状态无效。");
            if (line == "ISAY_SMART_NO_REWRITE")
            { hasResult = true; parsedResult = new(null, true); return; }
            const string resultPrefix = "ISAY_SMART_RESULT_HEX|";
            if (line.StartsWith(resultPrefix, StringComparison.Ordinal))
            {
                var encoded = line[resultPrefix.Length..];
                if (encoded.Length == 0 || encoded.Length % 2 != 0 || encoded.Length > NativeSmartOrganizerOptions.MaximumTextBytes * 2)
                    throw new IOException("内置整理协议正文无效。");
                var value = NativeSmartOrganizerOptions.StrictUtf8.GetString(Convert.FromHexString(encoded));
                if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) throw new IOException("内置整理返回空正文或无效正文。");
                hasResult = true;
                parsedResult = new(value, false);
                return;
            }
            throw new IOException("内置整理协议失败。");
        }

        private static async Task DrainAsync(Stream stream)
        {
            // 原生输出可能含敏感文本，持续排空但不记录、不显示、不用于猜测成功。
            var buffer = new byte[8192];
            while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
        }

        public ValueTask DisposeAsync()
        {
            lock (disposeLock) return new(disposal ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            machinePipe.Dispose();
            job.Dispose();
            if (!processStarted) ExitConfirmed = true;
            try { if (processStarted && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            try
            {
                if (processStarted) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                ExitConfirmed = true;
            }
            catch { }
            try { await Task.WhenAll(machineOutput, stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { }
            process.Dispose();
        }
    }

    private sealed class ProcessJob : IDisposable
    {
        private readonly SafeFileHandle handle;
        public ProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new IOException("无法建立内置整理进程保护。");
            var info = new ExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            { handle.Dispose(); throw new IOException("无法设置内置整理进程保护。"); }
        }
        public void Attach(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle)) throw new IOException("无法关联内置整理进程保护。");
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

internal sealed class NativeSmartOrganizerOptions
{
    public const int MaximumTextBytes = 1024 * 1024;
    public static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public required string RuntimeDirectory { get; init; }
    public required string DataDirectoryRoot { get; init; }
    public string BootstrapDirectoryRoot { get; init; } = Path.Combine(Path.GetTempPath(), "iSay", "SmartBootstrap");
    public required string ExecutableName { get; init; }
    public required IReadOnlyDictionary<string, string> RequiredHashes { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = ["--test-smart-organize", "--text", "placeholder", "--warmup-ms", "2000"];
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(40);
    public TimeSpan ReadyWaitTimeout { get; init; } = Timeout.InfiniteTimeSpan;
    public bool Persistent { get; init; }
    public Action<string>? TestLifecycleObserver { get; init; }

    private static string DefaultDataDirectory()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iSay", "VoiceDesk");
        var current = Path.Combine(root, "智能整理");
        var legacy = Path.Combine(root, "豆包整理");
        // 已有资料继续由原租约保护，不在其他实例可能持有文件时移动或重建。
        return Directory.Exists(legacy) ? legacy : current;
    }

    public static NativeSmartOrganizerOptions Production(string? directory, string? dataDirectoryRoot = null) => new()
    {
        RuntimeDirectory = Path.GetFullPath(directory ?? Path.Combine(AppContext.BaseDirectory, "smart-runtime")),
        DataDirectoryRoot = Path.GetFullPath(dataDirectoryRoot ?? DefaultDataDirectory()),
        ExecutableName = "ImeService.exe",
        Persistent = true,
        ReadyWaitTimeout = TimeSpan.FromMilliseconds(1500),
        OperationTimeout = TimeSpan.FromSeconds(6),
        RequiredHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 只允许经过本项目隔离协议改造并固定哈希的运行时，不能执行任意安装版本。
            ["ImeService.exe"] = "BF373B2D2F522E232533708A49658561D7511C1336AD4A48C5FAF730DD140747",
            ["ui.dll"] = "571F5B226E88D0D594285AD8A841A638AFD632A961D36824AFBF813D50D22950",
            ["oime-config.dll"] = "3AAB6921DEFB4C2A99721C71737AA15120C6058FF335C6453620BC98A32B87E4",
            ["audioeffect-mt.dll"] = "7A88EDBB24237780CDBC0C1FDD33310B85E704FB1548A3DE12CD5DCE880746C6",
            ["applogrs.dll"] = "8E880A5FE3BE1C02EFFD8E71C6BDDA8B02D8B551592BBB0A3D9B39477435251E",
            ["doubaoime-rpc-new.dll"] = "A3116866686A6C3F2B5D022FF9575AD70849899E5016D519DC5B5EB2B5BBF791",
            ["parfait.dll"] = "A0D091EE9D8688AB76E9A427E6422B475D3A3E2E6EDAAD0A474E6D8B80D93DEF",
            ["Dbghelp.dll"] = "874A3146CD342A6ABFD956E4020373B44AC227E40C8F13B3847552C25E786C73",
            ["sscronet.dll"] = "099FB538B7434CFC1AD0F9935C52AAB83D5D6AA0DE74EC39CE14CB3FAAE068F0"
        }
    };
}
