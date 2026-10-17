using System.Text.Json.Serialization;

namespace ZemiLog.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpeakerRole
{
    Self,
    Remote
}

public sealed record TranscriptSegment(
    Guid Id,
    TimeSpan Start,
    TimeSpan End,
    SpeakerRole Speaker,
    string Text,
    DateTimeOffset CreatedAt);

public sealed record PresentationSlide(int Number, string Title, string Text, string Notes);

public sealed record PresentationContext(string SourceFileName, IReadOnlyList<PresentationSlide> Slides)
{
    public static PresentationContext Empty { get; } = new(string.Empty, []);
}

public sealed record SummaryItem(string Text, IReadOnlyList<int> SlideNumbers, IReadOnlyList<string> EvidenceTimes);

public sealed record ResearchSummary(
    DateTimeOffset GeneratedAt,
    bool IsFinal,
    IReadOnlyList<SummaryItem> CurrentTopic,
    IReadOnlyList<SummaryItem> Strengths,
    IReadOnlyList<SummaryItem> QuestionsAndConcerns,
    IReadOnlyList<SummaryItem> ImprovementSuggestions,
    IReadOnlyList<SummaryItem> RevisionActions,
    IReadOnlyList<SummaryItem> UnresolvedItems)
{
    public static ResearchSummary Empty { get; } = new(
        DateTimeOffset.MinValue, false, [], [], [], [], [], []);
}

public sealed record SessionMetadata(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status,
    string MicrophoneName,
    string TargetProcessName,
    int TargetProcessId,
    string PlaybackMode,
    string? PresentationFileName,
    string WhisperModel,
    string? LastError = null);

public sealed record SessionInfo(string DirectoryPath, SessionMetadata Metadata);

public sealed record SummaryRequest(
    IReadOnlyList<TranscriptSegment> Transcript,
    PresentationContext Presentation,
    ResearchSummary? PreviousSummary,
    bool IsFinal,
    bool AllowThinking = true)
{
    // 思考を使うのは最終要約だけ。最終要約でも、思考ありが失敗したときの取り直しでは切る
    public bool UseThinking => IsFinal && AllowThinking;
}

public sealed record QwenSettings(
    string BaseUrl,
    string Model,
    int TimeoutSeconds = 90,
    string? ApiKey = null)
{
    public bool IsConfigured => Uri.TryCreate(BaseUrl, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(Model);
}
