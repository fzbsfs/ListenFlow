using System.Diagnostics;
using ListenFlow.Protocol;

namespace ListenFlow.CommunicationProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("用法：探针 <宿主程序> <桌面程序> <隔离数据目录> <.NET 根目录>");
            return 2;
        }

        var hostPath = Path.GetFullPath(args[0]);
        var desktopPath = Path.GetFullPath(args[1]);
        var dataDirectory = Path.GetFullPath(args[2]);
        var dotnetRoot = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(dataDirectory);
        EnsureNoTargetDesktopProcess(desktopPath);

        Process? desktop = null;
        try
        {
            Console.WriteLine("阶段 1：宿主自动启动桌面程序并建立通信。");
            await using (var first = StartHost(hostPath, desktopPath, dataDirectory, dotnetRoot))
            {
                await first.SendAsync(new BridgeMessage
                {
                    Type = ProtocolConstants.Types.Hello,
                    RequestId = "hello-first"
                });
                var ready = await first.ReadUntilAsync(message =>
                    message.Type == ProtocolConstants.Types.Ready && message.RequestId == "hello-first");
                Console.WriteLine($"握手成功：状态={ready.State}，代次={ready.PlaybackGeneration}");
                desktop = await WaitForDesktopProcessAsync(desktopPath);
                Console.WriteLine("桌面进程号：" + desktop.Id);

                var text = string.Concat(Enumerable.Range(1, 20).Select(index =>
                    $"第{index:00}句用于通信与真实语音进度验证。"));
                await first.SendAsync(new BridgeMessage
                {
                    Type = ProtocolConstants.Types.Start,
                    RequestId = "start-1",
                    SessionId = "probe-session",
                    SnapshotId = "probe-snapshot",
                    SnapshotVersion = 1,
                    CommandSeq = 1,
                    OffsetEncoding = ProtocolConstants.OffsetEncoding,
                    Offset = 0,
                    Text = text,
                    Title = "通信探针文本",
                    VoiceName = "Microsoft Xiaoxiao",
                    Rate = 5
                });
                var startResult = await first.ReadUntilAllAsync(
                    message => message.Type == ProtocolConstants.Types.Ack && message.RequestId == "start-1",
                    message => message.Type == ProtocolConstants.Types.Progress && message.Length > 0);
                var progress = startResult.Single(message => message.Type == ProtocolConstants.Types.Progress);
                Console.WriteLine($"真实进度已回传：{progress.Offset}+{progress.Length}，代次={progress.PlaybackGeneration}");

                await first.SendAsync(SessionCommand(ProtocolConstants.Types.Pause, "pause-2", 2));
                var pause = await first.ReadUntilAsync(message =>
                    message.Type == ProtocolConstants.Types.Ack && message.RequestId == "pause-2");
                Console.WriteLine("暂停确认：" + pause.State);

                var target = text.Length / 2;
                while (target < text.Length &&
                       target > 0 &&
                       char.IsHighSurrogate(text[target - 1]) &&
                       char.IsLowSurrogate(text[target]))
                {
                    target++;
                }
                await first.SendAsync(new BridgeMessage
                {
                    Type = ProtocolConstants.Types.Seek,
                    RequestId = "seek-3",
                    SessionId = "probe-session",
                    SnapshotId = "probe-snapshot",
                    SnapshotVersion = 1,
                    CommandSeq = 3,
                    OffsetEncoding = ProtocolConstants.OffsetEncoding,
                    Offset = target
                });
                var seek = await first.ReadUntilAsync(message =>
                    message.Type == ProtocolConstants.Types.Ack && message.RequestId == "seek-3");
                Console.WriteLine($"暂停定位确认：状态={seek.State}，目标={target}，代次={seek.PlaybackGeneration}");

                await first.SendAsync(SessionCommand(ProtocolConstants.Types.Resume, "resume-4", 4));
                var resumed = await first.ReadUntilAsync(message =>
                    message.Type == ProtocolConstants.Types.Progress && message.Offset >= target);
                Console.WriteLine($"继续后的真实进度：{resumed.Offset}+{resumed.Length}");

                await first.SendAsync(SessionCommand(ProtocolConstants.Types.Stop, "stop-5", 5));
                var stopped = await first.ReadUntilAsync(message =>
                    message.Type == ProtocolConstants.Types.Ack && message.RequestId == "stop-5");
                Console.WriteLine("停止确认：" + stopped.State);
            }

            var originalPid = desktop.Id;
            Console.WriteLine("阶段 2：重新启动宿主并复用现有桌面进程。");
            await using (var second = StartHost(hostPath, desktopPath, dataDirectory, dotnetRoot))
            {
                await second.SendAsync(new BridgeMessage
                {
                    Type = ProtocolConstants.Types.Hello,
                    RequestId = "hello-reuse"
                });
                await second.ReadUntilAsync(message =>
                    message.Type == ProtocolConstants.Types.Ready && message.RequestId == "hello-reuse");
                var reused = await WaitForDesktopProcessAsync(desktopPath);
                if (reused.Id != originalPid)
                    throw new InvalidOperationException("宿主没有复用原桌面进程。");
                Console.WriteLine("复用成功，桌面进程号保持为：" + reused.Id);

                Console.WriteLine("阶段 3：强制中断测试实例，验证断线通知与下一条新命令恢复。");
                reused.Kill(entireProcessTree: true);
                await reused.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var disconnected = await second.ReadUntilAsync(message =>
                    message.Type == ProtocolConstants.Types.Error && message.Code == "pipe_disconnected");
                Console.WriteLine("收到可恢复断线错误：" + disconnected.Recoverable);

                await second.SendAsync(new BridgeMessage
                {
                    Type = ProtocolConstants.Types.Hello,
                    RequestId = "hello-reconnect"
                });
                await second.ReadUntilAsync(message =>
                    message.Type == ProtocolConstants.Types.Ready && message.RequestId == "hello-reconnect");
                desktop = await WaitForDesktopProcessAsync(desktopPath, originalPid);
                Console.WriteLine("重连成功，新桌面进程号：" + desktop.Id);
            }

            Console.WriteLine("通信探针全部通过。");
            return 0;
        }
        finally
        {
            if (desktop is { HasExited: false })
            {
                desktop.Kill(entireProcessTree: true);
                await desktop.WaitForExitAsync();
            }
        }
    }

    private static BridgeMessage SessionCommand(string type, string requestId, long commandSeq) => new()
    {
        Type = type,
        RequestId = requestId,
        SessionId = "probe-session",
        CommandSeq = commandSeq
    };

    private static void EnsureNoTargetDesktopProcess(string desktopPath)
    {
        var existing = FindDesktopProcesses(desktopPath).ToArray();
        if (existing.Length > 0)
            throw new InvalidOperationException("检测到正在运行的目标 ListenFlow 测试实例，请先关闭后再运行探针。");
    }

    private static async Task<Process> WaitForDesktopProcessAsync(string desktopPath, int? excludedPid = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var process = FindDesktopProcesses(desktopPath)
                .FirstOrDefault(candidate => candidate.Id != excludedPid && !candidate.HasExited);
            if (process is not null) return process;
            await Task.Delay(100);
        }
        throw new TimeoutException("未发现 ListenFlow 桌面测试进程。");
    }

    private static IEnumerable<Process> FindDesktopProcesses(string desktopPath)
    {
        foreach (var process in Process.GetProcessesByName("ListenFlow"))
        {
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch { }
            if (string.Equals(path, desktopPath, StringComparison.OrdinalIgnoreCase))
                yield return process;
            else
                process.Dispose();
        }
    }

    private sealed class HostSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly LengthPrefixedMessageStream _transport;

        public HostSession(Process process)
        {
            _process = process;
            _transport = new LengthPrefixedMessageStream(
                process.StandardOutput.BaseStream,
                process.StandardInput.BaseStream);
        }

        public ValueTask SendAsync(BridgeMessage message) => _transport.WriteAsync(message);

        public async Task<BridgeMessage> ReadUntilAsync(Func<BridgeMessage, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var message = await _transport.ReadAsync(timeout.Token)
                              ?? throw new EndOfStreamException("原生消息宿主提前退出。");
                if (predicate(message)) return message;
                if (message.Type == ProtocolConstants.Types.Error && message.Recoverable == false)
                    throw new InvalidOperationException(message.Code + "：" + message.Message);
            }
        }

        public async Task<IReadOnlyList<BridgeMessage>> ReadUntilAllAsync(
            params Func<BridgeMessage, bool>[] predicates)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var remaining = predicates.ToList();
            var matches = new List<BridgeMessage>();
            while (remaining.Count > 0)
            {
                var message = await _transport.ReadAsync(timeout.Token)
                              ?? throw new EndOfStreamException("原生消息宿主提前退出。");
                var index = remaining.FindIndex(predicate => predicate(message));
                if (index >= 0)
                {
                    matches.Add(message);
                    remaining.RemoveAt(index);
                }
                if (message.Type == ProtocolConstants.Types.Error && message.Recoverable == false)
                    throw new InvalidOperationException(message.Code + "：" + message.Message);
            }
            return matches;
        }

        public async ValueTask DisposeAsync()
        {
            try { _process.StandardInput.Close(); }
            catch { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            _process.Dispose();
        }
    }

    private static HostSession StartHost(
        string hostPath,
        string desktopPath,
        string dataDirectory,
        string dotnetRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
        startInfo.Environment["DOTNET_ROOT_X64"] = dotnetRoot;
        startInfo.Environment["LISTENFLOW_APP_PATH"] = desktopPath;
        startInfo.Environment["LISTENFLOW_DATA_DIR"] = dataDirectory;
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("无法启动原生消息宿主测试进程。");
        return new HostSession(process);
    }
}
