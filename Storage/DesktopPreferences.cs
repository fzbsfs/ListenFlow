using System.Text.Json;

namespace ListenFlow.Storage;

internal sealed class DesktopPreferences
{
    public bool FollowReading { get; set; } = true;
    public int ReadingFontSize { get; set; } = 13;
    public bool ShowMascot { get; set; } = true;
    public int? FloatingLeft { get; set; }
    public int? FloatingTop { get; set; }

    private static string SettingsPath => Path.Combine(AppPaths.DataDirectory, "desktop-preferences.json");

    public static DesktopPreferences Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(SettingsPath)) ?? new()
                : new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Diagnostics.AppLog.WriteException("读取界面设置失败，使用默认值", exception);
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.WriteException("保存界面设置失败", exception);
        }
    }
}
