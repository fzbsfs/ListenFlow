using System.Text;
using System.Text.Json;

namespace ListenFlow.Storage;

public sealed record StateLoadResult(ReaderState? State, string? Warning);

public sealed class ProgressStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public ProgressStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("状态文件路径不能为空。", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public StateLoadResult Load()
    {
        if (!File.Exists(_path)) return new StateLoadResult(null, null);
        try
        {
            var state = ReadValidated(_path, out var migrated);
            return new StateLoadResult(state, migrated ? "已读取旧版进度，保存时会升级状态格式。" : null);
        }
        catch (Exception primaryError)
        {
            var backupPath = _path + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    var backup = ReadValidated(backupPath, out _);
                    return new StateLoadResult(backup, "主状态文件损坏，已从备份恢复。原因：" + primaryError.Message);
                }
                catch
                {
                }
            }

            return new StateLoadResult(null, "状态文件无法读取，已使用空白状态。原因：" + primaryError.Message);
        }
    }

    public void Save(ReaderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        var backupPath = _path + ".bak";
        var json = JsonSerializer.Serialize(state, JsonOptions);

        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   16 * 1024,
                   FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_path))
        {
            File.Replace(temporaryPath, _path, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, _path);
        }
    }

    private static ReaderState ReadValidated(string path, out bool migrated)
    {
        var state = JsonSerializer.Deserialize<ReaderState>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
                    ?? throw new InvalidDataException("状态文件内容为空。");
        migrated = false;
        if (string.IsNullOrEmpty(state.TextHash))
        {
            migrated = true;
            return ReaderState.Create(
                string.IsNullOrWhiteSpace(state.FilePath) ? "恢复的文章" : Path.GetFileNameWithoutExtension(state.FilePath),
                state.Text,
                state.CharacterPosition,
                state.FilePath,
                state.VoiceName,
                state.Rate);
        }
        if (state.SchemaVersion != ReaderState.CurrentSchemaVersion)
            throw new InvalidDataException($"不支持的状态版本：{state.SchemaVersion}。");
        if (!state.MatchesText(state.Text))
            throw new InvalidDataException("状态文件中的正文校验失败。");
        if (state.CharacterPosition < 0 || state.CharacterPosition > state.Text.Length)
            throw new InvalidDataException("状态文件中的阅读位置无效。");
        return state;
    }
}
