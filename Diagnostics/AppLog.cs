using System.Text;
using ListenFlow.Storage;

namespace ListenFlow.Diagnostics;

public static class AppLog
{
    private static readonly object Gate = new();

    public static string LogDirectory => Path.Combine(AppPaths.DataDirectory, "logs");

    public static void WriteEvent(string stage, IReadOnlyDictionary<string, object?>? details = null,
        Exception? exception = null) => LocalDiagnosticLog.Write("desktop", stage, details, exception);

    public static void WriteException(string context, Exception exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, "listenflow-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                var message = new StringBuilder()
                    .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ")
                    .AppendLine(context)
                    .AppendLine(exception.ToString())
                    .AppendLine()
                    .ToString();
                File.AppendAllText(path, message, new UTF8Encoding(false));
                RemoveOldLogs();
            }
        }
        catch
        {
        }
    }

    private static void RemoveOldLogs()
    {
        foreach (var path in Directory.EnumerateFiles(LogDirectory, "listenflow-*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(10))
        {
            try { File.Delete(path); }
            catch { }
        }
    }
}
