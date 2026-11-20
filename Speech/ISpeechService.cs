namespace ListenFlow.Speech;

public sealed record LocalVoice(string Name, string Culture, bool Enabled, string? Identifier = null)
{
    public bool IsKnownOnline => IsKnownOnlineIdentifier(Identifier);

    public string StatusDescription => IsKnownOnline
        ? "已识别为在线或存在同名在线语音，不可选择"
        : Enabled ? "已启用，未识别为在线，可选择；离线能力需实测" : "已安装但未启用，不可选择";

    public static IReadOnlyList<LocalVoice> Coalesce(IEnumerable<LocalVoice> voices) =>
        voices.GroupBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(voice => voice.IsKnownOnline) ?? group
                .OrderByDescending(voice => voice.Enabled)
                .ThenBy(voice => voice.Culture, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(voice => !voice.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            .ThenBy(voice => voice.Culture, StringComparer.OrdinalIgnoreCase)
            .ThenBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool IsKnownOnlineIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return false;
        const string enumerator = @"\TokenEnums\NaturalVoiceEnumerator\";
        var index = identifier.LastIndexOf(enumerator, StringComparison.OrdinalIgnoreCase);
        if (index < 0 && identifier.Contains('\\')) return false;
        var token = index < 0 ? identifier : identifier[(index + enumerator.Length)..];
        return token.StartsWith("Edge-", StringComparison.OrdinalIgnoreCase) ||
               token.StartsWith("Azure-", StringComparison.OrdinalIgnoreCase);
    }
}
public sealed record SpeechRequest(Guid Id, string Text, int Rate);

public sealed class SpeechProgressEventArgs(Guid promptId, int characterPosition, int characterCount) : EventArgs
{
    public Guid PromptId { get; } = promptId;
    public int CharacterPosition { get; } = characterPosition;
    public int CharacterCount { get; } = characterCount;
}

public sealed class SpeechCompletedEventArgs(Guid promptId, bool cancelled, Exception? error) : EventArgs
{
    public Guid PromptId { get; } = promptId;
    public bool Cancelled { get; } = cancelled;
    public Exception? Error { get; } = error;
}

public interface ISpeechService : IDisposable
{
    event EventHandler<SpeechProgressEventArgs>? Progressed;
    event EventHandler<SpeechCompletedEventArgs>? Completed;

    IReadOnlyList<LocalVoice> Voices { get; }
    string? SelectedVoiceName { get; }

    void SelectVoice(string name);
    Guid SpeakAsync(string text, int rate);
    void Pause();
    void Resume();
    void CancelAll();
}
