using ListenFlow.Diagnostics;

namespace ListenFlow;

internal static class Program
{
    private const string InstanceMutexName = "Local\\ListenFlow.Desktop.Singleton.7E5E7364";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
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
            Application.Run(new MainForm());
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
