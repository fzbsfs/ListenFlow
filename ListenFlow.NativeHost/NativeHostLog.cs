using System.Text;
using ListenFlow.Diagnostics;

namespace ListenFlow.NativeHost;

internal static class NativeHostLog
{
    private static readonly object Gate = new();

    public static void WriteEvent(string stage, IReadOnlyDictionary<string, object?>? details = null,
        Exception? exception = null) => LocalDiagnosticLog.Write("native-host", stage, details, exception);

    public static void Write(string context, Exception? exception)
    {
        WriteEvent(context, exception: exception);
        try
        {
            lock (Gate)
            {
                var root = Environment.GetEnvironmentVariable("LISTENFLOW_DATA_DIR");
                if (string.IsNullOrWhiteSpace(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ListenFlow");
                var directory = Path.Combine(root, "logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "native-host-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                var text = new StringBuilder()
                    .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ")
                    .AppendLine(context)
                    .AppendLine(exception?.ToString())
                    .AppendLine()
                    .ToString();
                File.AppendAllText(path, text, new UTF8Encoding(false));
            }
        }
        catch
        {
        }
    }
}
