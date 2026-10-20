using System.Text.Json;
using ListenFlow.Diagnostics;

namespace ListenFlow.Tests;

public sealed class LocalDiagnosticLogTests
{
    [Fact]
    public void Serialize_只保留白名单元数据且不写入正文标题网址()
    {
        var json = LocalDiagnosticLog.Serialize("desktop", "snapshot", new Dictionary<string, object?>
        {
            ["sessionId"] = "session-1",
            ["textLength"] = 12345,
            ["nodeCount"] = 321,
            ["text"] = "这是不得写入的正文",
            ["title"] = "不得写入的标题",
            ["sourceUri"] = "https://example.invalid/private"
        });

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("session-1", root.GetProperty("sessionId").GetString());
        Assert.Equal(12345, root.GetProperty("textLength").GetInt32());
        Assert.Equal(321, root.GetProperty("nodeCount").GetInt32());
        Assert.False(root.TryGetProperty("text", out _));
        Assert.False(root.TryGetProperty("title", out _));
        Assert.False(root.TryGetProperty("sourceUri", out _));
        Assert.DoesNotContain("这是不得写入的正文", json, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_异常保留类型消息堆栈并遮盖网址()
    {
        Exception exception;
        try { throw new InvalidOperationException("失败于 https://example.invalid/private"); }
        catch (Exception caught) { exception = caught; }

        var json = LocalDiagnosticLog.Serialize("desktop", "failure", exception: exception);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(typeof(InvalidOperationException).FullName, root.GetProperty("errorType").GetString());
        Assert.Contains("[网址已省略]", root.GetProperty("errorMessage").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("stack").GetString()));
        Assert.DoesNotContain("example.invalid", json, StringComparison.Ordinal);
    }
}
