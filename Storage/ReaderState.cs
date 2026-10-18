using System.Security.Cryptography;
using System.Text;

namespace ListenFlow.Storage;

public sealed class ReaderState
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string DocumentId { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = "未命名文章";
    public string Text { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public string TextHash { get; init; } = string.Empty;
    public int CharacterPosition { get; init; }
    public int Rate { get; init; }
    public string VoiceName { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static ReaderState Create(
        string title,
        string text,
        int characterPosition,
        string? filePath,
        string voiceName,
        int rate,
        string? documentId = null) => new()
        {
            DocumentId = string.IsNullOrWhiteSpace(documentId) ? Guid.NewGuid().ToString("N") : documentId,
            Title = string.IsNullOrWhiteSpace(title) ? "未命名文章" : title,
            Text = text ?? string.Empty,
            FilePath = filePath,
            TextHash = ComputeHash(text ?? string.Empty),
            CharacterPosition = Math.Clamp(characterPosition, 0, text?.Length ?? 0),
            Rate = Math.Clamp(rate, -10, 10),
            VoiceName = voiceName ?? string.Empty,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    public bool MatchesText(string text) => string.Equals(TextHash, ComputeHash(text), StringComparison.Ordinal);

    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
