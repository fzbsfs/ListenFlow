using ListenFlow.Core;

namespace ListenFlow.Communication;

public sealed record RemoteStartRequest(
    string Text,
    int Offset,
    string Title,
    string? SourceUri,
    string? VoiceName,
    int? Rate,
    string SessionId,
    string SnapshotId,
    long SnapshotVersion);

public interface IRemotePlaybackTarget
{
    event EventHandler<ReaderChangedEventArgs>? PlaybackChanged;

    int TextLength { get; }
    string Text { get; }
    PlaybackState PlaybackState { get; }
    long PlaybackGeneration { get; }

    void Start(RemoteStartRequest request);
    void Pause();
    void Resume();
    void Stop();
    void Seek(int offset);
}
