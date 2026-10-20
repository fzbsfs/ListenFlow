using System.IO.Pipes;
using ListenFlow.NativeHost;

namespace ListenFlow.Tests;

public sealed class DesktopConnectionFactoryTests
{
    [Fact]
    public async Task 主程序管道已存在_直接复用且不再次启动()
    {
        var pipeName = "ListenFlow.Connection.Tests." + Guid.NewGuid().ToString("N");
        await using var server = CreateServer(pipeName);
        var wait = server.WaitForConnectionAsync();
        var launcher = new CountingLauncher();
        var factory = new DesktopConnectionFactory(Options(pipeName), launcher, () => false);

        await using var connection = await factory.ConnectAsync(CancellationToken.None);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, launcher.StartCount);
        Assert.True(server.IsConnected);
    }

    [Fact]
    public async Task 主程序未运行_启动一次后连接新管道()
    {
        var pipeName = "ListenFlow.Connection.Tests." + Guid.NewGuid().ToString("N");
        NamedPipeServerStream? server = null;
        Task? wait = null;
        var launcher = new CountingLauncher(() =>
        {
            server = CreateServer(pipeName);
            wait = server.WaitForConnectionAsync();
        });
        var factory = new DesktopConnectionFactory(Options(pipeName), launcher, () => false);

        await using var connection = await factory.ConnectAsync(CancellationToken.None);
        Assert.NotNull(wait);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, launcher.StartCount);
        Assert.True(server?.IsConnected);
        if (server is not null) await server.DisposeAsync();
    }

    private static NativeHostOptions Options(string pipeName) => new(
        pipeName,
        Path.Combine(Path.GetTempPath(), "ListenFlow.exe"),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(100));

    private static NamedPipeServerStream CreateServer(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private sealed class CountingLauncher(Action? onStart = null) : IDesktopAppLauncher
    {
        public int StartCount { get; private set; }

        public void Start()
        {
            StartCount++;
            onStart?.Invoke();
        }
    }
}
