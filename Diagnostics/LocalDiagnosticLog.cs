using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ListenFlow.Diagnostics;

// 只允许写入关联标识、范围和运行状态；不得把正文消息整体写入诊断文件。
internal static class LocalDiagnosticLog
{
    private const long MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "sessionId", "snapshotId", "snapshotVersion", "commandSeq", "requestId", "generation",
        "promptId", "textLength", "chunkCount", "chunkIndex", "queueCount", "queueDepth",
        "rangeStart", "rangeLength", "rangeEnd", "position", "characterCount", "state",
        "messageType", "connectionState", "code", "reason", "voiceName", "rate", "cancelled",
        "sapiStarted", "hasProgress", "lastProgressPosition", "lastProgressLength", "lastProgressUtc",
        "elapsedMilliseconds", "droppedCount", "nodeCount", "source", "frameId", "result"
    };

    public static string Serialize(string component, string stage,
        IReadOnlyDictionary<string, object?>? details = null, Exception? exception = null)
    {
        var record = new Dictionary<string, object?>
        {
            ["time"] = DateTimeOffset.UtcNow,
            ["processId"] = Environment.ProcessId,
            ["component"] = Limit(component, 80),
            ["stage"] = Limit(stage, 160)
        };
        if (details is not null)
        {
            foreach (var (key, value) in details)
            {
                if (!AllowedFields.Contains(key)) continue;
                // 不接受任意对象，防止未来调用方意外序列化整篇文章或请求对象。
                if (value is null or bool or byte or short or int or long or float or double or decimal)
                    record[key] = value;
                else if (value is Guid or DateTimeOffset or DateTime)
                    record[key] = value.ToString();
                else if (value is string text)
                    record[key] = Limit(text, 240);
            }
        }
        if (exception is not null)
        {
            record["errorType"] = exception.GetType().FullName;
            record["errorMessage"] = Limit(exception.Message, 1024);
            record["stack"] = Limit(exception.StackTrace ?? string.Empty, 8192);
            if (exception.InnerException is { } inner)
            {
                record["innerErrorType"] = inner.GetType().FullName;
                record["innerErrorMessage"] = Limit(inner.Message, 1024);
            }
        }
        return JsonSerializer.Serialize(record);
    }

    public static void Write(string component, string stage,
        IReadOnlyDictionary<string, object?>? details = null, Exception? exception = null,
        string? directoryOverride = null)
    {
        try
        {
            lock (Gate)
            {
                var root = Environment.GetEnvironmentVariable("LISTENFLOW_DATA_DIR");
                if (string.IsNullOrWhiteSpace(root))
                {
                    var isolatedMarker = Path.Combine(AppContext.BaseDirectory, "edge-test-isolated-data.marker");
                    root = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        File.Exists(isolatedMarker) ? "ListenFlow-EdgeTest\\Data" : "ListenFlow");
                }
                var directory = directoryOverride ?? Path.Combine(root, "logs");
                Directory.CreateDirectory(directory);
                var prefix = component == "native-host" ? "native-host-diagnostic-" : "listenflow-diagnostic-";
                var path = Path.Combine(directory, prefix + DateTime.Now.ToString("yyyyMMdd") + ".jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumFileBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, Serialize(component, stage, details, exception) + Environment.NewLine,
                    new UTF8Encoding(false));
                foreach (var old in Directory.EnumerateFiles(directory, prefix + "*")
                             .OrderByDescending(File.GetLastWriteTimeUtc).Skip(20))
                    File.Delete(old);
            }
        }
        catch (Exception exceptionDuringLogging)
        {
            // 诊断写入失败不能改变播放行为；只写调试输出，不能污染原生消息标准输出。
            System.Diagnostics.Debug.WriteLine("ListenFlow 本地诊断写入失败：" + exceptionDuringLogging.GetType().Name);
        }
    }

    private static string Limit(string value, int length)
    {
        var redacted = Regex.Replace(value, @"https?://[^\s]+", "[网址已省略]", RegexOptions.IgnoreCase);
        return redacted.Length <= length ? redacted : redacted[..length];
    }
}
