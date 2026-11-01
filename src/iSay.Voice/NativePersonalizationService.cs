using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace iSay.Voice;

// 复用官方NER与修改对网络入口；候选接纳、去重和用户门控由学习协调器负责。
public sealed class NativePersonalizationService : IVocabularyLearningService, IModificationLearningService, IAsyncDisposable
{
    private readonly NativePersonalizationOptions options;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Worker? worker;
    private int disposed;

    public NativePersonalizationService(NativePersonalizationOptions? options = null)
    {
        this.options = options ?? NativePersonalizationOptions.Production(AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iSay", "VoiceDesk"));
    }

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        try { ThrowIfDisposed(); await EnsureWorkerAsync(linked.Token).ConfigureAwait(false); }
        catch { await ReleaseWorkerAsync().ConfigureAwait(false); throw; }
        finally { lifecycle.Release(); }
    }

    public Task<VocabularyServiceResponse> ExtractAsync(VocabularyNerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[][] fields;
        try
        {
            fields = [Encode(request.EventId, 128, false), Encode(request.FinalText, NativePersonalizationOptions.MaximumTextBytes, false), Encode(request.AppName, 512, true)];
        }
        catch (Exception ex) when (ex is ArgumentException or EncoderFallbackException)
        { return Task.FromResult(new VocabularyServiceResponse(false, null, "native_learning_invalid_input")); }
        return ExecuteAsync(fields, false, cancellationToken);
    }

    public Task<VocabularyServiceResponse> ExtractModificationAsync(VocabularyModifyPairRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[][] fields;
        try
        {
            fields = [Encode(request.EventId, 128, false), Encode(request.SourceText, NativePersonalizationOptions.MaximumTextBytes, false),
                Encode(request.TargetText, NativePersonalizationOptions.MaximumTextBytes, false), Encode(request.AppName, 512, true)];
        }
        catch (Exception ex) when (ex is ArgumentException or EncoderFallbackException)
        { return Task.FromResult(new VocabularyServiceResponse(false, null, "native_learning_invalid_input")); }
        return ExecuteAsync(fields, true, cancellationToken);
    }

    private async Task<VocabularyServiceResponse> ExecuteAsync(byte[][] fields, bool modification, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var native = await EnsureWorkerAsync(linked.Token).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            deadline.CancelAfter(options.RequestTimeout);
            var header = new byte[(fields.Length + 1) * 4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, modification ? 0x31514d49u : 0x31514e49u);
            for (var index = 0; index < fields.Length; index++) BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4 + index * 4), fields[index].Length);
            await native.Process.StandardInput.BaseStream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            foreach (var field in fields) await native.Process.StandardInput.BaseStream.WriteAsync(field, deadline.Token).ConfigureAwait(false);
            await native.Process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
            var response = new byte[24];
            await native.Pipe.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(response);
            var status = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4));
            var http = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(8));
            var code = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(12));
            var length = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(16));
            if (magic != 0x31524e49 || status > 1 || length < 0 || length > NativePersonalizationOptions.MaximumResponseBytes
                || (status != 0 && length != 0) || (status == 0 && (http is < 200 or > 299 || length == 0)))
                throw new IOException("原生学习响应帧不完整。");
            if (status != 0) return new(false, null, "native_learning_http_" + http.ToString(CultureInfo.InvariantCulture) + "_code_" + code.ToString(CultureInfo.InvariantCulture));
            var body = new byte[length];
            await native.Pipe.ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);
            var json = NativePersonalizationOptions.StrictUtf8.GetString(body);
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("原生学习JSON根类型无效。");
            return new(true, json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
        { await ReleaseWorkerAsync().ConfigureAwait(false); throw; }
        catch (OperationCanceledException)
        { await ReleaseWorkerAsync().ConfigureAwait(false); return new(false, null, "native_learning_timeout"); }
        catch (NativeProfileException ex)
        { await ReleaseWorkerAsync().ConfigureAwait(false); return new(false, null, ex.Code); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or DecoderFallbackException or System.ComponentModel.Win32Exception)
        { await ReleaseWorkerAsync().ConfigureAwait(false); return new(false, null, "native_learning_unavailable"); }
        finally { lifecycle.Release(); }
    }

    private static byte[] Encode(string? value, int maximumBytes, bool allowEmpty)
    {
        if (value is null || value.Contains('\0') || (!allowEmpty && string.IsNullOrWhiteSpace(value))) throw new ArgumentException("原生学习字段无效。");
        var bytes = NativePersonalizationOptions.StrictUtf8.GetBytes(value);
        if (bytes.Length > maximumBytes || (!allowEmpty && bytes.Length == 0)) throw new ArgumentException("原生学习字段超出容量。");
        return bytes;
    }

    private async Task<Worker> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (worker is not null && !worker.Process.HasExited) return worker;
        await ReleaseWorkerAsync().ConfigureAwait(false);
        // 各原生组件只在冷初始化到READY期间互斥；已就绪的学习请求不占用此闸门。
        using var startup = await NativeSdkInitializationGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("原生学习仅支持Windows。");
        var directory = Path.GetFullPath(options.RuntimeDirectory);
        if (options.ExecutableName != "ImeService.Personalization.exe" || !options.RequiredHashes.ContainsKey(options.ExecutableName)) throw new IOException("原生学习运行库缺少冻结校验。");
        foreach (var item in options.RequiredHashes)
        {
            var file = Path.GetFullPath(Path.Combine(directory, item.Key));
            if (!file.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("运行库文件超出指定目录。");
            await using var stream = File.OpenRead(file);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).Equals(item.Value, StringComparison.OrdinalIgnoreCase))
                throw new IOException("原生学习运行库校验失败。");
        }
        var lease = ProfileLease.Acquire(options.DataDirectoryRoot);
        var name = "iSayPersonalization_" + Guid.NewGuid().ToString("N");
        NamedPipeServerStream? pipe = null;
        Process? process = null;
        ProcessJob? job = null;
        Worker native;
        try
        {
        job = new ProcessJob();
        pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.Combine(directory, options.ExecutableName))
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "--test-smart-organize", "--text", "placeholder", "--warmup-ms", "2000", "--result-timeout-ms", "25000" }) start.ArgumentList.Add(argument);
        start.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        start.Environment["ISAY_PERSONALIZATION_RESULT_PIPE"] = @"\\.\pipe\" + name;
        start.Environment["ISAY_PERSONALIZATION_DATA_ROOT"] = lease.Directory;
        process = new Process { StartInfo = start };
        native = new Worker(process, pipe, lease, job);
        }
        catch
        {
            job?.Dispose();
            pipe?.Dispose();
            process?.Dispose();
            lease.Release(true);
            throw;
        }
        worker = native;
        try
        {
            native.Start();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.PrepareTimeout);
            var connected = pipe.WaitForConnectionAsync(deadline.Token);
            var exited = native.Process.WaitForExitAsync(deadline.Token);
            var first = await Task.WhenAny(connected, exited).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (first == exited) throw new IOException("原生学习进程就绪前退出。");
            await connected.ConfigureAwait(false);
            var ready = new byte[8];
            await pipe.ReadExactlyAsync(ready, deadline.Token).ConfigureAwait(false);
            if (!ready.AsSpan().SequenceEqual("ISAYNER1"u8)) throw new IOException("原生学习就绪帧无效。");
            return native;
        }
        catch { await ReleaseWorkerAsync().ConfigureAwait(false); throw; }
    }

    private async Task ReleaseWorkerAsync()
    {
        var native = worker;
        worker = null;
        if (native is not null) await native.DisposeAsync().ConfigureAwait(false);
    }
    private void ThrowIfDisposed() { if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(NativePersonalizationService)); }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await ReleaseWorkerAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private sealed class Worker(Process process, NamedPipeServerStream pipe, ProfileLease lease, ProcessJob job) : IAsyncDisposable
    {
        private Task output = Task.CompletedTask;
        private Task error = Task.CompletedTask;
        private bool started;
        private int closed;
        public Process Process => process;
        public NamedPipeServerStream Pipe => pipe;
        public void Start()
        {
            if (!NativeHelperProcess.Start(process)) throw new IOException("无法启动原生学习进程。");
            started = true;
            job.Attach(process);
            lease.RecordChild(process);
            // 原生SDK日志可能有设备标识，直接排空到空流，不解析或保存。
            output = DrainAsync(process.StandardOutput.BaseStream);
            error = DrainAsync(process.StandardError.BaseStream);
        }
        private static async Task DrainAsync(Stream stream)
        { try { await stream.CopyToAsync(Stream.Null).ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            var clean = !started;
            try
            {
                if (started)
                {
                    try { process.StandardInput.Close(); } catch (Exception ex) when (CleanupFailure(ex)) { }
                    // 先关闭作业，父进程退出和取消都不会留下本轮原生子进程。
                    try { job.Dispose(); } catch (Exception ex) when (CleanupFailure(ex)) { }
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (CleanupFailure(ex)) { }
                    try
                    {
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                        clean = true;
                    }
                    catch (Exception ex) when (CleanupFailure(ex)) { }
                }
            }
            finally
            {
                // 清理失败不覆盖原取消/超时；不确定子进程退出时保留未清理租约。
                try { job.Dispose(); } catch (Exception ex) when (CleanupFailure(ex)) { }
                try { await pipe.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) when (CleanupFailure(ex)) { }
                try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception ex) when (CleanupFailure(ex)) { }
                try { process.Dispose(); } catch (Exception ex) when (CleanupFailure(ex)) { }
                try { lease.Release(clean); } catch (Exception ex) when (CleanupFailure(ex)) { }
            }
        }
        private static bool CleanupFailure(Exception error) => error is IOException or ObjectDisposedException or
            InvalidOperationException or TimeoutException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;
    }

    private sealed class NativeProfileException(string code) : IOException { public string Code => code; }
    private sealed record LeaseState(bool Clean, int ParentPid, long ParentTicks, int? ChildPid, long? ChildTicks);
    private sealed class ProfileLease(FileStream handle, string directory)
    {
        public string Directory => directory;
        public static ProfileLease Acquire(string root)
        {
            var path = Path.GetFullPath(root);
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.TrimEnd('\\') == Path.GetPathRoot(path)?.TrimEnd('\\'))
                throw new NativeProfileException("native_learning_invalid_data_root");
            System.IO.Directory.CreateDirectory(path);
            FileStream? file = null;
            try
            {
                file = new FileStream(Path.Combine(path, "学习资料租约.json"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (file.Length > 16384) throw new NativeProfileException("native_learning_profile_unknown");
                if (file.Length > 0)
                {
                    var old = JsonSerializer.Deserialize<LeaseState>(file);
                    if (old is null) throw new NativeProfileException("native_learning_profile_unknown");
                    if (!old.Clean)
                    {
                        if (old.ChildPid is not > 0 || old.ChildTicks is not > 0) throw new NativeProfileException("native_learning_profile_unknown");
                        try
                        {
                            using var child = Process.GetProcessById(old.ChildPid.Value);
                            if (!child.HasExited && child.StartTime.ToUniversalTime().Ticks == old.ChildTicks.Value)
                                throw new NativeProfileException("native_learning_profile_busy");
                        }
                        catch (ArgumentException) { }
                    }
                }
                var lease = new ProfileLease(file, path);
                lease.Write(new(false, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, null, null));
                return lease;
            }
            catch (NativeProfileException) { file?.Dispose(); throw; }
            catch (Exception ex) when (ex is JsonException or UnauthorizedAccessException or InvalidOperationException)
            { file?.Dispose(); throw new NativeProfileException("native_learning_profile_unknown"); }
            catch (IOException) { file?.Dispose(); throw new NativeProfileException("native_learning_profile_busy"); }
        }
        public void RecordChild(Process child) => Write(new(false, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, child.Id, child.StartTime.ToUniversalTime().Ticks));
        private void Write(LeaseState state)
        {
            handle.Position = 0;
            handle.SetLength(0);
            JsonSerializer.Serialize(handle, state);
            handle.Flush(true);
        }
        public void Release(bool clean)
        {
            try { if (clean) Write(new(true, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, null, null)); }
            finally { handle.Dispose(); }
        }
    }

    private sealed class ProcessJob : IDisposable
    {
        private readonly SafeFileHandle handle;
        public ProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("无法创建原生学习进程作业。"); }
            var information = new ExtendedLimitInformation();
            information.BasicLimitInformation.LimitFlags = 0x2000;
            if (!SetInformationJobObject(handle, 9, ref information, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            { handle.Dispose(); throw new IOException("无法设置原生学习退出清理。"); }
        }
        public void Attach(Process process) { if (!AssignProcessToJobObject(handle, process.Handle)) throw new IOException("无法关联原生学习进程作业。"); }
        public void Dispose() => handle.Dispose();
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation { public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}

public sealed record NativePersonalizationOptions
{
    public const int MaximumTextBytes = 1024 * 1024;
    public const int MaximumResponseBytes = 1024 * 1024;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public required string RuntimeDirectory { get; init; }
    public required string DataDirectoryRoot { get; init; }
    public string ExecutableName { get; init; } = "ImeService.Personalization.exe";
    public TimeSpan PrepareTimeout { get; init; } = TimeSpan.FromSeconds(35);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(25);
    public IReadOnlyDictionary<string, string> RequiredHashes { get; init; } = new Dictionary<string, string>();
    public static NativePersonalizationOptions Production(string appDirectory, string ownDataDirectory) => new()
    {
        RuntimeDirectory = Path.Combine(appDirectory, "personalization-runtime"),
        DataDirectoryRoot = Path.Combine(ownDataDirectory, "个性化学习"),
        RequiredHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ImeService.Personalization.exe"] = "393A4977FA0FD7B605A50044798AE44DB6208DBF8B5E55978908F26B5BC7F47A",
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
