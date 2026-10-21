using System.IO.Pipes;
using ListenFlow.Communication;
using ListenFlow.Core;
using ListenFlow.Protocol;

namespace ListenFlow.Tests;

public sealed class DesktopPipeServerTests
{
    [Fact]
    public async Task 客户端断开后_新连接可重新握手且不重建播放核心()
    {
        using var context = new DedicatedThreadSynchronizationContext();
        var target = new PipeTestTarget();
        using var router = new DesktopCommandRouter(target, context);
        var pipeName = "ListenFlow.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new DesktopPipeServer(router, pipeName);
        server.Start();

        await using (var first = await ConnectAsync(pipeName))
        {
            var transport = new LengthPrefixedMessageStream(first, first);
            var ready = await transport.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ProtocolConstants.Types.Ready, ready?.Type);
            await transport.WriteAsync(new BridgeMessage
            {
                Type = ProtocolConstants.Types.Hello,
                RequestId = "hello-1"
            });
            var reply = await transport.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("hello-1", reply?.RequestId);
        }

        await using (var second = await ConnectAsync(pipeName))
        {
            var transport = new LengthPrefixedMessageStream(second, second);
            var ready = await transport.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ProtocolConstants.Types.Ready, ready?.Type);
            Assert.Equal(0, target.StartCount);
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        return client;
    }

    private sealed class PipeTestTarget : IRemotePlaybackTarget
    {
        public event EventHandler<ReaderChangedEventArgs>? PlaybackChanged;
        public int StartCount { get; private set; }
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
            PlaybackChanged?.Invoke(this, new ReaderChangedEventArgs(
                PlaybackState,
                request.Offset,
                new TextRange(request.Offset, 0),
                new TextRange(request.Offset, 0),
                PlaybackGeneration,
                null));
        }

        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Resume() => PlaybackState = PlaybackState.Speaking;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Seek(int offset) => PlaybackGeneration++;
        public void SetRate(int rate) { }
    }
}
