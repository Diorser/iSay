using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace iSay.Desktop.Services;

internal enum TsfLearningState : uint { Disabled, Watching, Edited, Completed, Discarded }
internal sealed record TsfLearningReply(int HResult, TsfLearningState State, ulong Revision, string? SourceText, string? TargetText)
{
    public bool HasModification => HResult == 0 && State == TsfLearningState.Completed &&
        !string.IsNullOrEmpty(SourceText) && !string.IsNullOrEmpty(TargetText) && SourceText != TargetText;
}

/// <summary>只跟随已成功交付的原会话，不重新捕获当前焦点、窗口标题或文档。</summary>
internal interface ITsfLearningObserver
{
    string ApplicationName { get; }
    Task<TsfLearningReply> ArmAsync(Guid session, string expectedCommittedText, CancellationToken cancellationToken = default);
    Task<TsfLearningReply> QueryAsync(Guid session, CancellationToken cancellationToken = default);
    Task<TsfLearningReply> CancelAsync(Guid session, CancellationToken cancellationToken = default);
}

/// <summary>整理新建的原范围使用独立原生会话，学习层仍按原录音段管理生命周期。</summary>
internal sealed class RefinementLearningObserver(ITsfLearningObserver inner, Guid logical, Guid actual) : ITsfLearningObserver
{
    public string ApplicationName => inner.ApplicationName;
    private static Task<TsfLearningReply> Refused() => Task.FromResult(new TsfLearningReply(
        unchecked((int)0x80070005), TsfLearningState.Discarded, 0, null, null));
    public Task<TsfLearningReply> ArmAsync(Guid session, string expectedCommittedText, CancellationToken cancellationToken = default) =>
        session == logical ? inner.ArmAsync(actual, expectedCommittedText, cancellationToken) : Refused();
    public Task<TsfLearningReply> QueryAsync(Guid session, CancellationToken cancellationToken = default) =>
        session == logical ? inner.QueryAsync(actual, cancellationToken) : Refused();
    public Task<TsfLearningReply> CancelAsync(Guid session, CancellationToken cancellationToken = default) =>
        session == logical ? inner.CancelAsync(actual, cancellationToken) : Refused();
}

internal sealed class TsfLearningClient(TsfEndpoint endpoint) : ITsfLearningObserver
{
    internal const int MaximumTextLength = 65536;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly UnicodeEncoding encoding = new(false, false, true);
    private ulong sequence;
    private Guid armedSession;
    public string ApplicationName
    {
        get
        {
            try { using var process = Process.GetProcessById(checked((int)endpoint.ProcessId)); return process.ProcessName + ".exe"; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
            { return "未知应用"; }
        }
    }

    public Task<TsfLearningReply> ArmAsync(Guid session, string expectedCommittedText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedCommittedText);
        if (expectedCommittedText.Length == 0) throw new ArgumentException("学习基线不能为空。", nameof(expectedCommittedText));
        return SendAsync(10, session, expectedCommittedText, cancellationToken);
    }
    public Task<TsfLearningReply> QueryAsync(Guid session, CancellationToken cancellationToken = default) => SendWithBusyRetryAsync(11, session, cancellationToken);
    public Task<TsfLearningReply> CancelAsync(Guid session, CancellationToken cancellationToken = default) => SendWithBusyRetryAsync(12, session, cancellationToken);

    private async Task<TsfLearningReply> SendWithBusyRetryAsync(uint command, Guid session, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(700));
        for (var attempt = 0; ; attempt++)
        {
            var reply = await SendAsync(command, session, "", deadline.Token).ConfigureAwait(false);
            // TF_E_LOCKED 证明请求尚未应用；仅对明确忙作有界重试，不重放未知结果的命令。
            if (reply.HResult != unchecked((int)0x80040500) || attempt >= 4) return reply;
            await Task.Delay(20, deadline.Token).ConfigureAwait(false);
        }
    }

    private async Task<TsfLearningReply> SendAsync(uint command, Guid session, string text, CancellationToken cancellationToken)
    {
        if (session == Guid.Empty || text.Length > MaximumTextLength || text.Contains('\0')) throw new ArgumentException("学习会话或正文无效。");
        var payload = encoding.GetBytes(text);
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (armedSession != Guid.Empty && armedSession != session) throw new InvalidOperationException("学习观察器只允许绑定原语音会话。");
            if (command == 10) armedSession = session;
            using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(100, cancellationToken).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverProcess) || serverProcess != endpoint.ProcessId)
                throw new UnauthorizedAccessException("学习观察端点不属于原目标进程。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
            var header = new byte[40];
            BinaryPrimitives.WriteUInt32LittleEndian(header, TsfCompositionClient.Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), TsfCompositionClient.Version);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), command);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)text.Length);
            session.TryWriteBytes(header.AsSpan(16, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), ++sequence);
            await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            if (payload.Length != 0) await pipe.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            var response = new byte[32];
            await pipe.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(response) != TsfCompositionClient.Magic ||
                BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4)) != TsfCompositionClient.Version)
                throw new InvalidDataException("学习观察回复版本无效。");
            var state = (TsfLearningState)BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(12));
            var sourceLength = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(16));
            var targetLength = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(20));
            var operation = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(8));
            if (!Enum.IsDefined(state) || sourceLength > MaximumTextLength || targetLength > MaximumTextLength ||
                (state != TsfLearningState.Completed || operation != 0) && (sourceLength != 0 || targetLength != 0))
                throw new InvalidDataException("学习观察回复状态或长度无效。");
            var body = new byte[checked(((int)sourceLength + (int)targetLength) * 2)];
            if (body.Length != 0) await pipe.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
            var source = sourceLength == 0 ? null : encoding.GetString(body.AsSpan(0, (int)sourceLength * 2));
            var target = targetLength == 0 ? null : encoding.GetString(body.AsSpan((int)sourceLength * 2));
            if (source?.Contains('\0') == true || target?.Contains('\0') == true ||
                state == TsfLearningState.Completed && operation == 0 && (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || source == target))
                throw new InvalidDataException("学习观察回复未提供有效修改对。");
            return new(operation, state, BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(24)), source, target);
        }
        finally { serial.Release(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint processId);
}
