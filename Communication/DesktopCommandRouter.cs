using ListenFlow.Core;
using ListenFlow.Protocol;

namespace ListenFlow.Communication;

public sealed class DesktopCommandRouter : IDisposable
{
    private static readonly IReadOnlyList<string> Capabilities =
    [
        "start",
        "pause",
        "resume",
        "stop",
        "seek",
        "progress:utf-16",
        "session-generation"
    ];

    private readonly IRemotePlaybackTarget _target;
    private readonly SynchronizationContext _stateContext;
    private string? _sessionId;
    private string? _snapshotId;
    private long? _snapshotVersion;
    private long _lastCommandSeq = -1;
    private bool _disposed;

    public DesktopCommandRouter(IRemotePlaybackTarget target, SynchronizationContext stateContext)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _stateContext = stateContext ?? throw new ArgumentNullException(nameof(stateContext));
        _target.PlaybackChanged += OnPlaybackChanged;
    }

    public event EventHandler<BridgeMessage>? Outbound;

    public BridgeMessage Handle(BridgeMessage command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Invoke(() => HandleCore(command));
    }

    public BridgeMessage CreateReadyMessage() => Invoke(() => new BridgeMessage
    {
        Type = ProtocolConstants.Types.Ready,
        SessionId = _sessionId,
        SnapshotId = _snapshotId,
        SnapshotVersion = _snapshotVersion,
        PlaybackGeneration = _target.PlaybackGeneration,
        State = StateName(_target.PlaybackState),
        OffsetEncoding = ProtocolConstants.OffsetEncoding,
        Capabilities = Capabilities
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _target.PlaybackChanged -= OnPlaybackChanged;
    }

    private BridgeMessage HandleCore(BridgeMessage command)
    {
        if (_disposed)
            return Error(command, "router_disposed", "通信路由已经关闭。", recoverable: false);

        var validation = ProtocolValidator.ValidateCommand(command);
        if (!validation.IsValid)
            return Error(command, validation.Code!, validation.Message!, recoverable: true);

        if (command.Type == ProtocolConstants.Types.Hello)
            return CreateReadyMessage() with { RequestId = command.RequestId };
        if (command.Type == ProtocolConstants.Types.Ping)
            return new BridgeMessage
            {
                Type = ProtocolConstants.Types.Pong,
                RequestId = command.RequestId,
                SessionId = _sessionId,
                PlaybackGeneration = _target.PlaybackGeneration,
                State = StateName(_target.PlaybackState)
            };

        if (command.Type == ProtocolConstants.Types.Start)
            return HandleStart(command);

        var sessionError = ValidateActiveSession(command);
        if (sessionError is not null) return sessionError;
        if (command.CommandSeq <= _lastCommandSeq)
            return Error(command, "stale_command", "命令序号没有递增，已拒绝过期或重复命令。", true);

        try
        {
            switch (command.Type)
            {
                case ProtocolConstants.Types.Pause:
                    _target.Pause();
                    break;
                case ProtocolConstants.Types.Resume:
                    _target.Resume();
                    break;
                case ProtocolConstants.Types.Stop:
                    _target.Stop();
                    break;
                case ProtocolConstants.Types.Seek:
                    var snapshotError = ValidateActiveSnapshot(command);
                    if (snapshotError is not null) return snapshotError;
                    if (!ProtocolValidator.IsUtf16Boundary(_target.Text, command.Offset!.Value))
                        return Error(command, "invalid_offset", "定位位置超出当前快照或切断了 UTF-16 代理项。", true);
                    _target.Seek(command.Offset.Value);
                    break;
                default:
                    return Error(command, "unknown_command", "不支持的播放命令。", true);
            }

            _lastCommandSeq = command.CommandSeq!.Value;
            return Ack(command);
        }
        catch (Exception exception)
        {
            return Error(command, "command_failed", exception.Message, true);
        }
    }

    private BridgeMessage HandleStart(BridgeMessage command)
    {
        if (string.Equals(command.SessionId, _sessionId, StringComparison.Ordinal) &&
            command.CommandSeq <= _lastCommandSeq)
        {
            return Error(command, "stale_command", "命令序号没有递增，已拒绝过期或重复命令。", true);
        }

        _sessionId = command.SessionId;
        _snapshotId = command.SnapshotId;
        _snapshotVersion = command.SnapshotVersion;
        _lastCommandSeq = command.CommandSeq!.Value;
        try
        {
            _target.Start(new RemoteStartRequest(
                command.Text!,
                command.Offset ?? 0,
                string.IsNullOrWhiteSpace(command.Title) ? "网页文章" : command.Title,
                command.SourceUri,
                command.VoiceName,
                command.Rate,
                command.SessionId!,
                command.SnapshotId!,
                command.SnapshotVersion!.Value));
            return Ack(command);
        }
        catch (Exception exception)
        {
            return Error(command, "start_failed", exception.Message, true);
        }
    }

    private BridgeMessage? ValidateActiveSession(BridgeMessage command)
    {
        if (_sessionId is null)
            return Error(command, "no_active_session", "当前没有网页播放会话。", true);
        if (!string.Equals(command.SessionId, _sessionId, StringComparison.Ordinal))
            return Error(command, "stale_session", "命令属于过期的播放会话。", true);
        return null;
    }

    private BridgeMessage? ValidateActiveSnapshot(BridgeMessage command)
    {
        if (!string.Equals(command.SnapshotId, _snapshotId, StringComparison.Ordinal) ||
            command.SnapshotVersion != _snapshotVersion)
        {
            return Error(command, "stale_snapshot", "定位命令属于过期的网页正文快照。", true);
        }
        return null;
    }

    private BridgeMessage Ack(BridgeMessage command) => new()
    {
        Type = ProtocolConstants.Types.Ack,
        RequestId = command.RequestId,
        SessionId = _sessionId,
        SnapshotId = _snapshotId,
        SnapshotVersion = _snapshotVersion,
        CommandSeq = command.CommandSeq,
        PlaybackGeneration = _target.PlaybackGeneration,
        State = StateName(_target.PlaybackState),
        OffsetEncoding = ProtocolConstants.OffsetEncoding
    };

    private BridgeMessage Error(BridgeMessage command, string code, string message, bool recoverable) => new()
    {
        Type = ProtocolConstants.Types.Error,
        RequestId = command.RequestId,
        SessionId = command.SessionId ?? _sessionId,
        SnapshotId = command.SnapshotId ?? _snapshotId,
        SnapshotVersion = command.SnapshotVersion ?? _snapshotVersion,
        CommandSeq = command.CommandSeq,
        PlaybackGeneration = _target.PlaybackGeneration,
        State = StateName(_target.PlaybackState),
        Code = code,
        Message = message,
        Recoverable = recoverable
    };

    private void OnPlaybackChanged(object? sender, ReaderChangedEventArgs eventArgs) =>
        Invoke(() =>
        {
            PublishPlaybackChanged(eventArgs);
            return true;
        });

    private void PublishPlaybackChanged(ReaderChangedEventArgs eventArgs)
    {
        if (_disposed || _sessionId is null) return;
        var state = new BridgeMessage
        {
            Type = ProtocolConstants.Types.State,
            SessionId = _sessionId,
            SnapshotId = _snapshotId,
            SnapshotVersion = _snapshotVersion,
            PlaybackGeneration = eventArgs.Generation,
            State = StateName(eventArgs.State),
            OffsetEncoding = ProtocolConstants.OffsetEncoding,
            Offset = eventArgs.Position,
            Length = 0,
            Code = eventArgs.State == PlaybackState.Error ? "playback_error" : null,
            Message = eventArgs.ErrorMessage,
            Recoverable = eventArgs.State == PlaybackState.Error ? false : null
        };
        Outbound?.Invoke(this, state);

        if (eventArgs.SpokenRange.Length <= 0) return;
        Outbound?.Invoke(this, new BridgeMessage
        {
            Type = ProtocolConstants.Types.Progress,
            SessionId = _sessionId,
            SnapshotId = _snapshotId,
            SnapshotVersion = _snapshotVersion,
            PlaybackGeneration = eventArgs.Generation,
            State = StateName(eventArgs.State),
            OffsetEncoding = ProtocolConstants.OffsetEncoding,
            Offset = eventArgs.SpokenRange.Start,
            Length = eventArgs.SpokenRange.Length
        });
    }

    private T Invoke<T>(Func<T> action)
    {
        if (ReferenceEquals(SynchronizationContext.Current, _stateContext))
            return action();

        T? result = default;
        Exception? error = null;
        _stateContext.Send(_ =>
        {
            try { result = action(); }
            catch (Exception exception) { error = exception; }
        }, null);
        if (error is not null) throw error;
        return result!;
    }

    private static string StateName(PlaybackState state) => state switch
    {
        PlaybackState.Ready => "ready",
        PlaybackState.Speaking => "speaking",
        PlaybackState.Paused => "paused",
        PlaybackState.Stopped => "stopped",
        PlaybackState.Completed => "completed",
        PlaybackState.Error => "error",
        _ => "unknown"
    };
}
