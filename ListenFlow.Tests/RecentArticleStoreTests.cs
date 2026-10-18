using ListenFlow.Storage;

namespace ListenFlow.Tests;

public sealed class RecentArticleStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ListenFlow.History.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_超过容量_只保留最近文章并可完整恢复()
    {
        var store = new RecentArticleStore(_directory, capacity: 2);
        var first = ReaderState.Create("第一篇", "一。", 1, null, "Microsoft Xiaoxiao", 0, "article-1");
        var second = ReaderState.Create("第二篇", "二。", 1, null, "Microsoft Xiaoxiao", 1, "article-2");
        var third = ReaderState.Create("第三篇", "三。", 1, null, "Microsoft Xiaoxiao", 2, "article-3");

        store.Save(first);
        Thread.Sleep(10);
        store.Save(second);
        Thread.Sleep(10);
        store.Save(third);
        var recent = store.LoadRecent();

        Assert.Equal(2, recent.Count);
        Assert.Equal("第三篇", recent[0].Title);
        Assert.Equal("第二篇", recent[1].Title);
        Assert.Equal("三。", store.Load("article-3")!.Text);
        Assert.Null(store.Load("article-1"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
