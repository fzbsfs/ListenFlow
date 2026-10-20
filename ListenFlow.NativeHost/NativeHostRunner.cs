using ListenFlow.Protocol;

namespace ListenFlow.NativeHost;

public sealed class NativeHostRunner : IAsyncDisposable
{
    private readonly LengthPrefixedMessageStream _nativeTransport;
    private readonly IDesktopConnectionFactory _connectionFactory;
    private readonly Action<string, Exception?>? _log;
    private readonly Action<string, IReadOnlyDictionary<string, object?>?, Exception?>? _diagnostic;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sessionGate = new();
    private PipeSession? _session;

    public NativeHostRunner(
        LengthPrefixedMessageStream nativeTransport,
        IDesktopConnectionFactory connectionFactory,
        Action<string, Exception?>? log = null,
        Action<string, IReadOnlyDictionary<string, object?>?, Exception?>? diagnostic = null)
    {
        _nativeTransport = nativeTransport ?? throw new ArgumentNullException(nameof(nativeTransport));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _log = log;
        _diagnostic = diagnostic;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        while (!linked.IsCancellationRequested)
        {
            BridgeMessage? command;
            try
            {
                command = await _nativeTransport.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log?.Invoke("读取浏览器消息失败", exception);
                await WriteNativeErrorAsync(null, "invalid_native_message", exception.Message, false, linked.Token)
                    .ConfigureAwait(false);
                return 1;
            }

            if (command is null) break;
            Log("native-command-received", CommandDetails(command));
            if (command.Type == ProtocolConstants.Types.Diagnostic)
            {
                Log(command.DiagnosticStage ?? "browser-diagnostic", CommandDetails(command));
                continue;
            }
            await SendCommandOnceAsync(command, linked.Token).ConfigureAwait(false);
        }
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        PipeSession? session;
        lock (_sessionGate)
        {
            session = _session;
            _session = null;
        }
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
        _connectionGate.Dispose();
        _shutdown.Dispose();
    }

    private async Task SendCommandOnceAsync(BridgeMessage command, CancellationToken cancellationToken)
    {
        PipeSession session;
        try
        {
            session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log?.Invoke("连接 ListenFlow 主程序失败", exception);
            await WriteNativeErrorAsync(command, "desktop_unavailable", exception.Message, true, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await session.Transport.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            Log("native-command-sent-to-pipe", CommandDetails(command));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _log?.Invoke("向 ListenFlow 发送命令失败", exception);
            InvalidateSession(session);
            await WriteNativeErrorAsync(
                    command,
                    "pipe_send_failed",
                    "命令没有重放，请由调用方使用新的命令序号重试。" + exception.Message,
                    true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<PipeSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        lock (_sessionGate)
        {
            if (_session is { IsClosed: false } active)
            {
                Log("native-pipe-reused", new Dictionary<string, object?> { ["connectionState"] = "connected" });
                return active;
            }
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sessionGate)
            {
                if (_session is { IsClosed: false } active)
                    return active;
            }

            var stream = await _connectionFactory.ConnectAsync(cancellationToken).ConfigureAwait(false);
            Log("native-pipe-connected", new Dictionary<string, object?> { ["connectionState"] = "connected" });
            var session = new PipeSession(stream);
            lock (_sessionGate)
                _session = session;
            session.ReaderTask = ReadDesktopMessagesAsync(session, _shutdown.Token);
            return session;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task ReadDesktopMessagesAsync(PipeSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await session.Transport.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (message is null) break;
                Log("native-event-received-from-pipe", CommandDetails(message));
                await _nativeTransport.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }
            if (!cancellationToken.IsCancellationRequested)
                await NotifyDisconnectedOnceAsync(session, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or ObjectDisposedException)
        {
            _log?.Invoke("ListenFlow 命名管道连接已断开", exception);
            if (!cancellationToken.IsCancellationRequested)
                await NotifyDisconnectedOnceAsync(session, exception, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            InvalidateSession(session);
        }
    }

    private async Task NotifyDisconnectedOnceAsync(
        PipeSession session,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref session.DisconnectNotified, 1) != 0) return;
        await WriteNativeErrorAsync(
                null,
                "pipe_disconnected",
                "ListenFlow 连接已断开；下一条新命令会重新连接，旧命令不会自动重放。" + exception?.Message,
                true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void InvalidateSession(PipeSession session)
    {
        lock (_sessionGate)
        {
            if (ReferenceEquals(_session, session))
                _session = null;
        }
        session.MarkClosed();
    }

    private ValueTask WriteNativeErrorAsync(
        BridgeMessage? command,
        string code,
        string message,
        bool recoverable,
        CancellationToken cancellationToken) =>
        _nativeTransport.WriteAsync(new BridgeMessage
        {
            Type = ProtocolConstants.Types.Error,
            RequestId = command?.RequestId,
            SessionId = command?.SessionId,
            SnapshotId = command?.SnapshotId,
            SnapshotVersion = command?.SnapshotVersion,
            CommandSeq = command?.CommandSeq,
            Code = code,
            Message = message,
            Recoverable = recoverable
        }, cancellationToken);

    private void Log(string stage, IReadOnlyDictionary<string, object?>? details = null,
        Exception? exception = null)
    {
        try { _diagnostic?.Invoke(stage, details, exception); }
        catch { }
    }

    private static IReadOnlyDictionary<string, object?> CommandDetails(BridgeMessage message) =>
        new Dictionary<string, object?>
        {
            ["messageType"] = message.Type,
            ["sessionId"] = message.SessionId,
            ["snapshotId"] = message.SnapshotId,
            ["snapshotVersion"] = message.SnapshotVersion,
            ["commandSeq"] = message.CommandSeq,
            ["requestId"] = message.RequestId,
            ["generation"] = message.PlaybackGeneration,
            ["position"] = message.Offset,
            ["rangeLength"] = message.Length,
            ["textLength"] = message.TextLength ?? message.Text?.Length,
            ["nodeCount"] = message.NodeCount,
            ["elapsedMilliseconds"] = message.ElapsedMilliseconds,
            ["reason"] = message.Reason,
            ["source"] = message.Source,
            ["frameId"] = message.FrameId,
            ["state"] = message.State,
            ["code"] = message.Code
        };

    private sealed class PipeSession(Stream stream) : IAsyncDisposable
    {
        private int _closed;

        public Stream Stream { get; } = stream;
        public LengthPrefixedMessageStream Transport { get; } = new(stream, stream);
        public Task? ReaderTask { get; set; }
        public int DisconnectNotified;
        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public void MarkClosed()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
                Stream.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            MarkClosed();
            if (ReaderTask is not null &&
                (!Task.CurrentId.HasValue || ReaderTask.Id != Task.CurrentId.Value))
            {
                try { await ReaderTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
    }
}
