namespace ZemiLog.Core;

public interface ISummaryProvider
{
    Task<ResearchSummary> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken = default);
}

public interface ITranscriptionProvider
{
    Task<string> TranscribeAsync(Stream waveAudio, CancellationToken cancellationToken = default);
}
