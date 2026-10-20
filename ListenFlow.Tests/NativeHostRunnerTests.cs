using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Threading.Channels;
using ListenFlow.NativeHost;
using ListenFlow.Protocol;

namespace ListenFlow.Tests;

public sealed class NativeHostRunnerTests
{
    [Fact]
    public async Task 浏览器诊断消息_只写本地元数据且不连接桌面管道()
    {
        await using var nativePair = await PipePair.CreateAsync();
        var browser = new LengthPrefixedMessageStream(nativePair.Client, nativePair.Client);
        var native = new LengthPrefixedMessageStream(nativePair.Server, nativePair.Server);
        var factory = new ScriptedConnectionFactory();
        var records = new ConcurrentBag<(string Stage, IReadOnlyDictionary<string, object?>? Details)>();
        await using var runner = new NativeHostRunner(
            native,
            factory,
            diagnostic: (stage, details, _) => records.Add((stage, details)));
        var runTask = runner.RunAsync();

        await browser.WriteAsync(new BridgeMessage
        {
            Type = ProtocolConstants.Types.Diagnostic,
            DiagnosticStage = "edge-snapshot-created",
            SessionId = "session-1",
            TextLength = 30000,
            NodeCount = 2700,
            Source = "article"
        });
        await Task.Delay(100);

        Assert.Equal(0, factory.ConnectionCount);
        var record = Assert.Single(records, item => item.Stage == "edge-snapshot-created");
        Assert.Equal(30000, record.Details?["textLength"]);
        Assert.Equal(2700, record.Details?["nodeCount"]);
        nativePair.Client.Dispose();
        Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task 管道断开后_下一条新命令重连且旧开始命令不重放()
    {
        await using var nativePair = await PipePair.CreateAsync();
        var browser = new LengthPrefixedMessageStream(nativePair.Client, nativePair.Client);
        var native = new LengthPrefixedMessageStream(nativePair.Server, nativePair.Server);
        var factory = new ScriptedConnectionFactory();
        await using var runner = new NativeHostRunner(native, factory);
        var runTask = runner.RunAsync();

        var start = new BridgeMessage
        {
            Type = ProtocolConstants.Types.Start,
            RequestId = "start-1",
            SessionId = "session-1",
            SnapshotId = "snapshot-1",
            SnapshotVersion = 1,
            CommandSeq = 1,
            OffsetEncoding = ProtocolConstants.OffsetEncoding,
            Offset = 0,
            Text = "第一句。第二句。"
        };
        await browser.WriteAsync(start);
        await using var desktop1 = await factory.TakeServerAsync();
        var desktopTransport1 = new LengthPrefixedMessageStream(desktop1, desktop1);
        var firstReceived = await desktopTransport1.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("start-1", firstReceived?.RequestId);
        desktop1.Dispose();

        var disconnected = await ReadUntilAsync(browser, ProtocolConstants.Types.Error);
        Assert.Equal("pipe_disconnected", disconnected.Code);
        Assert.True(disconnected.Recoverable);

        await browser.WriteAsync(new BridgeMessage
        {
            Type = ProtocolConstants.Types.Ping,
            RequestId = "ping-2"
        });
        await using var desktop2 = await factory.TakeServerAsync();
        var desktopTransport2 = new LengthPrefixedMessageStream(desktop2, desktop2);
        var secondReceived = await desktopTransport2.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProtocolConstants.Types.Ping, secondReceived?.Type);
        Assert.Equal("ping-2", secondReceived?.RequestId);
        await desktopTransport2.WriteAsync(new BridgeMessage
        {
            Type = ProtocolConstants.Types.Pong,
            RequestId = "ping-2"
        });
        var pong = await ReadUntilAsync(browser, ProtocolConstants.Types.Pong);
        Assert.Equal("ping-2", pong.RequestId);
        Assert.Equal(2, factory.ConnectionCount);

        nativePair.Client.Dispose();
        Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<BridgeMessage> ReadUntilAsync(
        LengthPrefixedMessageStream transport,
        string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var message = await transport.ReadAsync(timeout.Token);
            Assert.NotNull(message);
            if (message.Type == type) return message;
        }
    }

    private sealed class ScriptedConnectionFactory : IDesktopConnectionFactory
    {
        private readonly Channel<NamedPipeServerStream> _servers = Channel.CreateUnbounded<NamedPipeServerStream>();

        public int ConnectionCount { get; private set; }

        public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            var pair = await PipePair.CreateAsync(cancellationToken);
            ConnectionCount++;
            await _servers.Writer.WriteAsync(pair.Server, cancellationToken);
            return pair.DetachClient();
        }

        public Task<NamedPipeServerStream> TakeServerAsync() =>
            _servers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class PipePair : IAsyncDisposable
    {
        private bool _clientDetached;

        private PipePair(NamedPipeServerStream server, NamedPipeClientStream client)
        {
            Server = server;
            Client = client;
        }

        public NamedPipeServerStream Server { get; }
        public NamedPipeClientStream Client { get; }

        public static async Task<PipePair> CreateAsync(CancellationToken cancellationToken = default)
        {
            var pipeName = "ListenFlow.NativeHost.Tests." + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            var wait = server.WaitForConnectionAsync(cancellationToken);
            await client.ConnectAsync(5000, cancellationToken);
            await wait;
            return new PipePair(server, client);
        }

        public NamedPipeClientStream DetachClient()
        {
            _clientDetached = true;
            return Client;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            if (!_clientDetached)
                await Client.DisposeAsync();
        }
    }
}
