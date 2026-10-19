using System.Diagnostics;
using System.IO.Pipes;
using ListenFlow.Protocol;

namespace ListenFlow.NativeHost;

public interface IDesktopConnectionFactory
{
    Task<Stream> ConnectAsync(CancellationToken cancellationToken);
}

public interface IDesktopAppLauncher
{
    void Start();
}

public sealed class DesktopAppLauncher(NativeHostOptions options) : IDesktopAppLauncher
{
    public void Start()
    {
        if (!File.Exists(options.DesktopExecutablePath))
            throw new FileNotFoundException("找不到 ListenFlow 主程序。", options.DesktopExecutablePath);

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = options.DesktopExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(options.DesktopExecutablePath)!,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("无法启动 ListenFlow 主程序。");
    }
}

public sealed class DesktopConnectionFactory(
    NativeHostOptions options,
    IDesktopAppLauncher launcher) : IDesktopConnectionFactory
{
    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var first = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
        if (first is not null) return first;

        var desktopExists = Mutex.TryOpenExisting(ProtocolConstants.DesktopMutexName, out var mutex);
        mutex?.Dispose();
        if (!desktopExists)
            launcher.Start();

        var deadline = DateTime.UtcNow + options.StartupTimeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var pipe = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
                if (pipe is not null) return pipe;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                lastError = exception;
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("无法在限定时间内连接 ListenFlow 主程序。", lastError);
    }

    private async Task<NamedPipeClientStream?> TryConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            var timeoutMilliseconds = Math.Max(1, (int)options.ConnectionAttemptTimeout.TotalMilliseconds);
            await pipe.ConnectAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            pipe.ReadMode = PipeTransmissionMode.Byte;
            return pipe;
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }
}
