using System.Text;

namespace ZemiLog.Core;

public sealed class MarkdownExporter
{
    public async Task ExportAsync(
        string outputPath,
        SessionMetadata metadata,
        IReadOnlyList<TranscriptSegment> transcript,
        ResearchSummary? summary,
        CancellationToken cancellationToken = default)
    {
        var markdown = Build(metadata, transcript, summary);
        await File.WriteAllTextAsync(outputPath, markdown, new UTF8Encoding(false), cancellationToken);
    }

    public string Build(SessionMetadata metadata, IReadOnlyList<TranscriptSegment> transcript, ResearchSummary? summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# ゼミ記録");
        builder.AppendLine();
        builder.AppendLine($"- 開始: {metadata.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        if (metadata.EndedAt is not null) builder.AppendLine($"- 終了: {metadata.EndedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 会議アプリ: {metadata.TargetProcessName}");
        if (!string.IsNullOrWhiteSpace(metadata.PresentationFileName)) builder.AppendLine($"- 資料: {metadata.PresentationFileName}");
        builder.AppendLine();
        builder.AppendLine("## 要約");
        builder.AppendLine();
        AppendSection(builder, "現在の話題", summary?.CurrentTopic);
        AppendSection(builder, "良かった点", summary?.Strengths);
        AppendSection(builder, "質問・懸念", summary?.QuestionsAndConcerns);
        AppendSection(builder, "改善提案", summary?.ImprovementSuggestions);
        AppendSection(builder, "修正アクション", summary?.RevisionActions);
        AppendSection(builder, "未解決事項", summary?.UnresolvedItems);
        builder.AppendLine("## 文字起こし");
        builder.AppendLine();
        foreach (var segment in transcript.OrderBy(x => x.Start))
        {
            var speaker = segment.Speaker == SpeakerRole.Self ? "自分" : "相手";
            builder.AppendLine($"- [{segment.Start:hh\\:mm\\:ss}] **{speaker}**: {segment.Text.Trim()}");
        }
        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string title, IReadOnlyList<SummaryItem>? items)
    {
        builder.AppendLine($"### {title}");
        builder.AppendLine();
        if (items is null || items.Count == 0)
        {
            builder.AppendLine("- （なし）");
        }
        else
        {
            foreach (var item in items)
            {
                var refs = new List<string>();
                if (item.SlideNumbers.Count > 0) refs.Add("スライド " + string.Join(", ", item.SlideNumbers));
                if (item.EvidenceTimes.Count > 0) refs.Add(string.Join(", ", item.EvidenceTimes));
                builder.AppendLine(refs.Count == 0 ? $"- {item.Text}" : $"- {item.Text}（{string.Join(" / ", refs)}）");
            }
        }
        builder.AppendLine();
    }
}
