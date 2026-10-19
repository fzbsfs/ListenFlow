using ListenFlow.Communication;
using ListenFlow.Core;
using ListenFlow.Protocol;

namespace ListenFlow.Tests;

public sealed class DesktopCommandRouterTests
{
    [Fact]
    public void Start_建立会话并把真实语音范围映射为进度消息()
    {
        using var context = new DedicatedThreadSynchronizationContext();
        var target = new FakeRemotePlaybackTarget();
        using var router = new DesktopCommandRouter(target, context);
        var outbound = new List<BridgeMessage>();
        router.Outbound += (_, message) => outbound.Add(message);

        var command = StartCommand(commandSeq: 1);
        var response = router.Handle(command);
        target.EmitProgress(position: 3, length: 2);

        Assert.Equal(ProtocolConstants.Types.Ack, response.Type);
        Assert.Equal("session-1", response.SessionId);
        Assert.Equal(1, response.CommandSeq);
        var progress = Assert.Single(outbound, message => message.Type == ProtocolConstants.Types.Progress);
        Assert.Equal("snapshot-1", progress.SnapshotId);
        Assert.Equal(1, progress.SnapshotVersion);
        Assert.Equal(target.PlaybackGeneration, progress.PlaybackGeneration);
        Assert.Equal(3, progress.Offset);
        Assert.Equal(2, progress.Length);
        Assert.Equal(ProtocolConstants.OffsetEncoding, progress.OffsetEncoding);
    }

    [Fact]
    public void 重复命令序号_拒绝且不重复执行()
    {
        using var context = new DedicatedThreadSynchronizationContext();
        var target = new FakeRemotePlaybackTarget();
        using var router = new DesktopCommandRouter(target, context);
        Assert.Equal(ProtocolConstants.Types.Ack, router.Handle(StartCommand(1)).Type);
        var pause = SessionCommand(ProtocolConstants.Types.Pause, 2);
        Assert.Equal(ProtocolConstants.Types.Ack, router.Handle(pause).Type);

        var duplicate = router.Handle(pause);

        Assert.Equal(ProtocolConstants.Types.Error, duplicate.Type);
        Assert.Equal("stale_command", duplicate.Code);
        Assert.Equal(1, target.PauseCount);
    }

    [Fact]
    public void Seek_旧快照与越界位置_均不触发播放目标()
    {
        using var context = new DedicatedThreadSynchronizationContext();
        var target = new FakeRemotePlaybackTarget();
        using var router = new DesktopCommandRouter(target, context);
        router.Handle(StartCommand(1));
        var stale = router.Handle(SeekCommand(2, "old-snapshot", 1, 2));
        var invalid = router.Handle(SeekCommand(3, "snapshot-1", 1, 999));

        Assert.Equal("stale_snapshot", stale.Code);
        Assert.Equal("invalid_offset", invalid.Code);
        Assert.Empty(target.SeekOffsets);
    }

    [Fact]
    public void 新会话允许重新从较小命令序号开始()
    {
        using var context = new DedicatedThreadSynchronizationContext();
        var target = new FakeRemotePlaybackTarget();
        using var router = new DesktopCommandRouter(target, context);
        router.Handle(StartCommand(10));

        var response = router.Handle(StartCommand(1) with
        {
            SessionId = "session-2",
            SnapshotId = "snapshot-2"
        });

        Assert.Equal(ProtocolConstants.Types.Ack, response.Type);
        Assert.Equal("session-2", response.SessionId);
        Assert.Equal(2, target.StartCount);
    }

    [Fact]
    public async Task 后台播放回调_出站进度仍由指定状态线程发布()
    {
        using var context = new DedicatedThreadSynchronizationContext();
        var target = new FakeRemotePlaybackTarget();
        using var router = new DesktopCommandRouter(target, context);
        router.Handle(StartCommand(1));
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.Outbound += (_, message) =>
        {
            if (message.Type == ProtocolConstants.Types.Progress)
                completion.TrySetResult(Environment.CurrentManagedThreadId);
        };

        await Task.Run(() => target.EmitProgress(2, 1));
        var eventThread = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(context.ThreadId, eventThread);
    }

    private static BridgeMessage StartCommand(long commandSeq) => new()
    {
        Type = ProtocolConstants.Types.Start,
        RequestId = "request-start",
        SessionId = "session-1",
        SnapshotId = "snapshot-1",
        SnapshotVersion = 1,
        CommandSeq = commandSeq,
        OffsetEncoding = ProtocolConstants.OffsetEncoding,
        Offset = 0,
        Text = "第一句。第二句。",
        VoiceName = "Microsoft Xiaoxiao",
        Rate = 0
    };

    private static BridgeMessage SessionCommand(string type, long commandSeq) => new()
    {
        Type = type,
        RequestId = "request-" + commandSeq,
        SessionId = "session-1",
        CommandSeq = commandSeq
    };

    private static BridgeMessage SeekCommand(long commandSeq, string snapshotId, long snapshotVersion, int offset) => new()
    {
        Type = ProtocolConstants.Types.Seek,
        RequestId = "request-seek-" + commandSeq,
        SessionId = "session-1",
        SnapshotId = snapshotId,
        SnapshotVersion = snapshotVersion,
        CommandSeq = commandSeq,
        OffsetEncoding = ProtocolConstants.OffsetEncoding,
        Offset = offset
    };

    private sealed class FakeRemotePlaybackTarget : IRemotePlaybackTarget
    {
        public event EventHandler<ReaderChangedEventArgs>? PlaybackChanged;

        public int StartCount { get; private set; }
        public int PauseCount { get; private set; }
        public List<int> SeekOffsets { get; } = [];
        public int TextLength => Text.Length;
        public string Text { get; private set; } = string.Empty;
        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Ready;
        public long PlaybackGeneration { get; private set; }

        public void Start(RemoteStartRequest request)
        {
            StartCount++;
            Text = request.Text;
            PlaybackGeneration++;
            PlaybackState = PlaybackState.Speaking;
            Raise(request.Offset, new TextRange(request.Offset, 0));
        }

        public void Pause()
        {
            PauseCount++;
            PlaybackState = PlaybackState.Paused;
            Raise(0, new TextRange(0, 0));
        }

        public void Resume()
        {
            PlaybackState = PlaybackState.Speaking;
            Raise(0, new TextRange(0, 0));
        }

        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            Raise(0, new TextRange(0, 0));
        }

        public void Seek(int offset)
        {
            SeekOffsets.Add(offset);
            PlaybackGeneration++;
            Raise(offset, new TextRange(offset, 0));
        }

        public void EmitProgress(int position, int length) =>
            Raise(position, new TextRange(position, length));

        private void Raise(int position, TextRange spokenRange) =>
            PlaybackChanged?.Invoke(this, new ReaderChangedEventArgs(
                PlaybackState,
                position,
                new TextRange(position, Math.Min(3, Math.Max(0, Text.Length - position))),
                spokenRange,
                PlaybackGeneration,
                null));
    }
}
