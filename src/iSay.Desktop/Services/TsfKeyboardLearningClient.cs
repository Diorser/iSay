using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace iSay.Desktop.Services;

internal enum TsfKeyboardLearningState : uint { Disabled = 0, Watching = 1, FocusUnavailable = 2 }
internal sealed record TsfKeyboardCommit(ulong CommitId, Guid SourceClsid, string Reading, string Text);
internal sealed record TsfKeyboardLearningReply(int HResult, TsfKeyboardLearningState State, IReadOnlyList<TsfKeyboardCommit> Commits);
internal interface ITsfKeyboardLearningObserver
{
    string TargetIdentity { get; }
    string ApplicationName { get; }
    Task<TsfKeyboardLearningReply> EnableAsync(Guid observationId, CancellationToken cancellationToken = default);
    Task<TsfKeyboardLearningReply> QueryAsync(Guid observationId, CancellationToken cancellationToken = default);
    Task<TsfKeyboardLearningReply> AcknowledgeAsync(Guid observationId, ulong commitId, CancellationToken cancellationToken = default);
    Task<TsfKeyboardLearningReply> DisableAsync(Guid observationId, CancellationToken cancellationToken = default);
}

/// <summary>只读取原 TSF 上下文发布的候选提交元数据，不收集原始按键或周围段落。</summary>
internal sealed class TsfKeyboardLearningClient(TsfEndpoint endpoint) : ITsfKeyboardLearningObserver
{
    public string TargetIdentity => $"{endpoint.ProcessId}:{endpoint.ThreadId}:{endpoint.FocusWindow:X}";
    internal const int MaximumBatchCount = 16, MaximumBodyBytes = 16384;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly UnicodeEncoding encoding = new(false, false, true);
    private Guid boundObservation;
    private ulong sequence;
    public string ApplicationName
    {
        get
        {
            try { using var process = Process.GetProcessById(checked((int)endpoint.ProcessId)); return process.ProcessName + ".exe"; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException) { return "未知应用"; }
        }
    }
    public static ITsfKeyboardLearningObserver? TryCaptureForeground() => TsfEndpoint.CaptureForeground() is { } target ? new TsfKeyboardLearningClient(target) : null;
    public Task<TsfKeyboardLearningReply> EnableAsync(Guid observationId, CancellationToken cancellationToken = default) => SendAsync(13, observationId, "", cancellationToken);
    public Task<TsfKeyboardLearningReply> QueryAsync(Guid observationId, CancellationToken cancellationToken = default) => SendAsync(14, observationId, "", cancellationToken);
    public Task<TsfKeyboardLearningReply> DisableAsync(Guid observationId, CancellationToken cancellationToken = default) => SendAsync(15, observationId, "", cancellationToken);
    public Task<TsfKeyboardLearningReply> AcknowledgeAsync(Guid observationId, ulong commitId, CancellationToken cancellationToken = default)
    {
        if (commitId == 0) throw new ArgumentOutOfRangeException(nameof(commitId));
        return SendAsync(16, observationId, commitId.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }
    private async Task<TsfKeyboardLearningReply> SendAsync(uint command, Guid observationId, string text, CancellationToken cancellationToken)
    {
        if (observationId == Guid.Empty) throw new ArgumentException("键盘学习观察编号不能为空。", nameof(observationId));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(700);
        await serial.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (boundObservation != Guid.Empty && boundObservation != observationId) throw new InvalidOperationException("观察器不能改绑其他观察编号。");
            if (command == 13) boundObservation = observationId;
            for (var attempt = 0; ; attempt++)
            {
                if (command == 13 && endpoint.FocusWindow != 0 && !endpoint.StillHasOriginalFocus())
                    return new(unchecked((int)0x800704c7), TsfKeyboardLearningState.FocusUnavailable, []);
                var reply = await SendCoreAsync(command, observationId, text, deadline.Token).ConfigureAwait(false);
                if (reply.HResult != unchecked((int)0x80040500) || attempt >= 4) return reply;
                await Task.Delay(20, deadline.Token).ConfigureAwait(false);
            }
        }
        finally { serial.Release(); }
    }
    private async Task<TsfKeyboardLearningReply> SendCoreAsync(uint command, Guid observationId, string text, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(100, cancellationToken).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var server) || server != endpoint.ProcessId)
            throw new UnauthorizedAccessException("键盘学习端点不属于原进程。");
        var header = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(header, TsfCompositionClient.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), TsfCompositionClient.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), command);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)text.Length);
        observationId.TryWriteBytes(header.AsSpan(16, 16));
        var requestSequence = ++sequence; BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), requestSequence);
        await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (text.Length != 0) await pipe.WriteAsync(encoding.GetBytes(text), cancellationToken).ConfigureAwait(false);
        var response = new byte[32]; await pipe.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        var operation = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(8));
        var state = (TsfKeyboardLearningState)BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(12));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(16));
        var bytes = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(20));
        if (BinaryPrimitives.ReadUInt32LittleEndian(response) != TsfCompositionClient.Magic || BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4)) != TsfCompositionClient.Version ||
            !Enum.IsDefined(state) || count > MaximumBatchCount || bytes > MaximumBodyBytes || BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(24)) != requestSequence ||
            (operation != 0 || command != 14 || state == TsfKeyboardLearningState.Disabled) && (count != 0 || bytes != 0))
            throw new InvalidDataException("键盘学习批次的版本、状态、容量或确认序号无效。");
        var body = new byte[(int)bytes]; if (body.Length != 0) await pipe.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        var commits = new List<TsfKeyboardCommit>(); var offset = 0; ulong previous = 0;
        for (uint index = 0; index < count; index++)
        {
            if (body.Length - offset < 32) throw new InvalidDataException("键盘学习记录不完整。");
            var id = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(offset));
            var source = new Guid(body.AsSpan(offset + 8, 16));
            var readingLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 24));
            var textLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 28)); offset += 32;
            if (id <= previous || source == Guid.Empty || readingLength is 0 or > 128 || textLength is 0 or > 50 || body.Length - offset < checked(((int)readingLength + (int)textLength) * 2))
                throw new InvalidDataException("键盘学习记录编号、来源或正文长度无效。");
            var reading = encoding.GetString(body.AsSpan(offset, (int)readingLength * 2)); offset += (int)readingLength * 2;
            var value = encoding.GetString(body.AsSpan(offset, (int)textLength * 2)); offset += (int)textLength * 2;
            if (reading.Contains('\0') || value.Contains('\0')) throw new InvalidDataException("键盘学习元数据含空字符。");
            commits.Add(new(id, source, reading, value)); previous = id;
        }
        if (offset != body.Length) throw new InvalidDataException("键盘学习批次含多余正文。");
        return new(operation, state, commits);
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint processId);
}
