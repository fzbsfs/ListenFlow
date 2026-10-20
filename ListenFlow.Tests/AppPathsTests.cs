using ListenFlow.Storage;

namespace ListenFlow.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void ResolveDataDirectory_诊断标记把验收数据与原版目录隔离()
    {
        var local = Path.Combine(Path.GetTempPath(), "ListenFlow.Paths.Tests", Guid.NewGuid().ToString("N"));
        var baseDirectory = Path.Combine(local, "Programs", "ListenFlow-EdgeTest", "app");
        var marker = Path.Combine(baseDirectory, "edge-test-isolated-data.marker");

        var isolated = AppPaths.ResolveDataDirectory(baseDirectory, null, local, path => path == marker);
        var regular = AppPaths.ResolveDataDirectory(baseDirectory, null, local, _ => false);

        Assert.Equal(Path.Combine(local, "ListenFlow-EdgeTest", "Data"), isolated);
        Assert.Equal(Path.Combine(local, "ListenFlow"), regular);
        Assert.NotEqual(isolated, regular);
    }

    [Fact]
    public void ResolveDataDirectory_显式环境变量优先于诊断标记()
    {
        var expected = Path.Combine(Path.GetTempPath(), "ListenFlow.Custom", Guid.NewGuid().ToString("N"));

        var actual = AppPaths.ResolveDataDirectory("C:\\app", expected, "C:\\local", _ => true);

        Assert.Equal(Path.GetFullPath(expected), actual);
    }
}
