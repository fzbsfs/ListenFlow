using ListenFlow.Storage;

namespace ListenFlow.Tests;

public sealed class ProgressStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ListenFlow.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveLoad_有效状态_完整往返并保留备份()
    {
        var path = Path.Combine(_directory, "progress.json");
        var store = new ProgressStore(path);
        var first = ReaderState.Create("第一篇", "一。二。", 2, null, "Microsoft Xiaoxiao", 1);
        var second = ReaderState.Create("第二篇", "三。四。", 3, "C:\\文章.txt", "Microsoft Xiaoxiao", 2);

        store.Save(first);
        store.Save(second);
        var result = store.Load();

        Assert.NotNull(result.State);
        Assert.Equal(second.Text, result.State!.Text);
        Assert.Equal(second.CharacterPosition, result.State.CharacterPosition);
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Load_主文件损坏_从备份恢复并返回警告()
    {
        var path = Path.Combine(_directory, "progress.json");
        var store = new ProgressStore(path);
        var state = ReaderState.Create("文章", "第一句。第二句。", 4, null, "Microsoft Xiaoxiao", 0);
        store.Save(state);
        File.Copy(path, path + ".bak", overwrite: true);
        File.WriteAllText(path, "{损坏的 JSON");

        var result = store.Load();

        Assert.NotNull(result.State);
        Assert.Equal(state.Text, result.State!.Text);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void Load_旧版状态文件_迁移正文进度语音与语速()
    {
        var path = Path.Combine(_directory, "progress.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path,
            """
            {
              "Text": "旧版文章。第二句。",
              "FilePath": null,
              "CharacterPosition": 5,
              "Rate": 2,
              "VoiceName": "Microsoft Xiaoxiao"
            }
            """);
        var store = new ProgressStore(path);

        var result = store.Load();

        Assert.NotNull(result.State);
        Assert.Equal("旧版文章。第二句。", result.State!.Text);
        Assert.Equal(5, result.State.CharacterPosition);
        Assert.Equal(2, result.State.Rate);
        Assert.Equal("Microsoft Xiaoxiao", result.State.VoiceName);
        Assert.NotNull(result.Warning);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
