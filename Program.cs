using ListenFlow.Communication;
using ListenFlow.Diagnostics;
using ListenFlow.Protocol;

namespace ListenFlow;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            ProtocolConstants.DesktopMutexName,
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "ListenFlow 已经在运行。请切换到现有窗口。",
                "ListenFlow",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
        {
            AppLog.WriteException("界面线程异常", eventArgs.Exception);
            MessageBox.Show(
                "程序遇到异常，详细信息已写入本地日志。\n\n" + eventArgs.Exception.Message,
                "ListenFlow 错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                AppLog.WriteException("未处理异常", exception);
        };

        try
        {
            using var form = new MainForm();
            DesktopCommandRouter? router = null;
            DesktopPipeServer? pipeServer = null;
            form.Shown += (_, _) =>
            {
                var context = SynchronizationContext.Current
                              ?? throw new InvalidOperationException("未取得桌面状态线程。");
                router = new DesktopCommandRouter(form, context, AppLog.WriteEvent);
                pipeServer = new DesktopPipeServer(
                    router,
                    log: (contextMessage, exception) =>
                    {
                        AppLog.WriteEvent(contextMessage, exception: exception);
                        if (exception is not null)
                            AppLog.WriteException(contextMessage, exception);
                    });
                pipeServer.Start();
            };
            Application.Run(form);
            if (pipeServer is not null)
                pipeServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            router?.Dispose();
        }
        catch (Exception exception)
        {
            AppLog.WriteException("启动失败", exception);
            MessageBox.Show(
                "ListenFlow 启动失败，详细信息已写入本地日志。\n\n" + exception.Message,
                "ListenFlow 启动失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            instanceMutex.ReleaseMutex();
        }
    }
}
