namespace ListenFlow.AIReader;

internal sealed record DesktopClipboardReadResult(string? Text, string? Error)
{
    public bool Success => Error is null;
}

internal static class DesktopClipboardConnector
{
    public static DesktopClipboardReadResult Read(Func<string> readText)
    {
        ArgumentNullException.ThrowIfNull(readText);
        try
        {
            var text = readText();
            return string.IsNullOrWhiteSpace(text)
                ? new(null, "剪贴板没有可朗读的文本。请先复制需要朗读的内容。")
                : new(text, null);
        }
        catch (Exception exception)
        {
            return new(null, "无法读取剪贴板：" + exception.Message);
        }
    }
}
