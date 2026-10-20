using ListenFlow.Protocol;

namespace ListenFlow.NativeHost;

internal static class Program
{
    private static async Task<int> Main()
    {
        var nativeTransport = new LengthPrefixedMessageStream(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput());
        try
        {
            var options = NativeHostOptions.FromEnvironment(AppContext.BaseDirectory);
            var connector = new DesktopConnectionFactory(options, new DesktopAppLauncher(options));
            await using var runner = new NativeHostRunner(
                nativeTransport, connector, NativeHostLog.Write, NativeHostLog.WriteEvent);
            return await runner.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            NativeHostLog.Write("原生消息宿主启动失败", exception);
            try
            {
                await nativeTransport.WriteAsync(new BridgeMessage
                {
                    Type = ProtocolConstants.Types.Error,
                    Code = "native_host_failed",
                    Message = exception.Message,
                    Recoverable = false
                }).ConfigureAwait(false);
            }
            catch
            {
            }
            return 1;
        }
    }
}
