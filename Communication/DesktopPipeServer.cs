using System.IO.Pipes;
using System.Threading.Channels;
using ListenFlow.Protocol;

namespace ListenFlow.Communication;

public sealed class DesktopPipeServer : IAsyncDisposable
{
    private readonly DesktopCommandRouter _router;
    private readonly string _pipeName;
    private readonly Action<string, Exception?>? _log;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;

    public DesktopPipeServer(
        DesktopCommandRouter router,
        string pipeName = ProtocolConstants.DesktopPipeName,
        Action<string, Exception?>? log = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("命名管道名称不能为空。", nameof(pipeName))
            : pipeName;
        _log = log;
    }

    public void Start()
    {
        if (_serverTask is not null) return;
        _serverTask = RunAsync(_shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_serverTask is not null)
        {
            try { await _serverTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ServeConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log?.Invoke("命名管道服务异常", exception);
                try { await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken serverCancellation)
    {
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        var outbound = Channel.CreateBounded<BridgeMessage>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var transport = new LengthPrefixedMessageStream(pipe, pipe);
        EventHandler<BridgeMessage> handler = (_, message) =>
        {
            if (!outbound.Writer.TryWrite(message) && message.Type != ProtocolConstants.Types.Progress)
                _log?.Invoke("通信事件队列已满，已丢弃非进度事件：" + message.Type, null);
        };
        _router.Outbound += handler;
        await outbound.Writer.WriteAsync(_router.CreateReadyMessage(), serverCancellation).ConfigureAwait(false);

        var writer = Task.Run(async () =>
        {
            await foreach (var message in outbound.Reader.ReadAllAsync(connectionCancellation.Token))
                await transport.WriteAsync(message, connectionCancellation.Token).ConfigureAwait(false);
        }, connectionCancellation.Token);

        try
        {
            while (!serverCancellation.IsCancellationRequested && pipe.IsConnected)
            {
                var command = await transport.ReadAsync(serverCancellation).ConfigureAwait(false);
                if (command is null) break;
                var response = _router.Handle(command);
                await outbound.Writer.WriteAsync(response, serverCancellation).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException exception)
        {
            _log?.Invoke("命名管道连接中断", exception);
        }
        finally
        {
            _router.Outbound -= handler;
            outbound.Writer.TryComplete();
            connectionCancellation.Cancel();
            try { await writer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
}
