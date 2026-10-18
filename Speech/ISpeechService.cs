namespace ListenFlow.Speech;

public sealed record LocalVoice(string Name, string Culture, bool Enabled);
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
