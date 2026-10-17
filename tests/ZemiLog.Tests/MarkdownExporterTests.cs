using ZemiLog.Core;

namespace ZemiLog.Tests;

public sealed class MarkdownExporterTests
{
    [Fact]
    public void Build_ContainsStructuredSummaryAndSpeakerTranscript()
    {
        var metadata = new SessionMetadata("id", new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.FromHours(9)),
            new DateTimeOffset(2026, 9, 6, 11, 0, 0, TimeSpan.FromHours(9)), "Completed", "Mic", "Teams", 1,
            "Headphones", "発表.pptx", "small");
        var summary = new ResearchSummary(DateTimeOffset.Now, true,
            [new SummaryItem("提案手法の説明", [3], ["00:01:20"])], [], [], [],
            [new SummaryItem("評価条件を追記する", [8], ["00:15:00"])], []);
        var transcript = new[]
        {
            new TranscriptSegment(Guid.NewGuid(), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), SpeakerRole.Self, "説明します", DateTimeOffset.Now),
            new TranscriptSegment(Guid.NewGuid(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7), SpeakerRole.Remote, "質問です", DateTimeOffset.Now)
        };

        var markdown = new MarkdownExporter().Build(metadata, transcript, summary);

        Assert.Contains("### 現在の話題", markdown);
        Assert.Contains("提案手法の説明（スライド 3 / 00:01:20）", markdown);
        Assert.Contains("[00:00:03] **自分**: 説明します", markdown);
        Assert.Contains("[00:00:05] **相手**: 質問です", markdown);
    }
}
