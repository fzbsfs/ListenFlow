using System.Security.Cryptography;
using System.Text;

namespace ListenFlow.Storage;

public sealed class RecentArticleStore
{
    private readonly string _directory;
    private readonly int _capacity;

    public RecentArticleStore(string directory, int capacity = 10)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("历史目录不能为空。", nameof(directory));
        if (capacity is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _directory = Path.GetFullPath(directory);
        _capacity = capacity;
    }

    public void Save(ReaderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(_directory);
        var path = GetPath(state.DocumentId);
        new ProgressStore(path).Save(state);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        RemoveOverflow();
    }

    public ReaderState? Load(string documentId)
    {
        var path = GetPath(documentId);
        return File.Exists(path) ? new ProgressStore(path).Load().State : null;
    }

    public IReadOnlyList<ReaderState> LoadRecent()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new
            {
                State = new ProgressStore(path).Load().State,
                LastWrite = File.GetLastWriteTimeUtc(path)
            })
            .Where(item => item.State is not null)
            .OrderByDescending(item => item.LastWrite)
            .Take(_capacity)
            .Select(item => item.State!)
            .ToArray();
    }

    private void RemoveOverflow()
    {
        var files = Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(_capacity)
            .ToArray();
        foreach (var path in files)
        {
            File.Delete(path);
            var backupPath = path + ".bak";
            if (File.Exists(backupPath)) File.Delete(backupPath);
            var temporaryPath = path + ".tmp";
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private string GetPath(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("文章标识不能为空。", nameof(documentId));
        var safeName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documentId)));
        return Path.Combine(_directory, safeName + ".json");
    }
}
