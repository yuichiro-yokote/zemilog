using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ZemiLog.Core;

public sealed class OpenAiCompatibleSummaryProvider : ISummaryProvider
{
    private readonly HttpClient _httpClient;
    private readonly QwenSettings _settings;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public OpenAiCompatibleSummaryProvider(HttpClient httpClient, QwenSettings settings)
    {
        _httpClient = httpClient;
        _settings = settings;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds));
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    public async Task<ResearchSummary> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured) throw new InvalidOperationException("Qwen接続先が設定されていません。");
        var endpoint = new Uri(new Uri(_settings.BaseUrl.TrimEnd('/') + "/"), "chat/completions");
        var payload = new Dictionary<string, object>
        {
            ["model"] = _settings.Model,
            ["temperature"] = 0.1,
            ["messages"] = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = BuildUserPrompt(request) }
            },
            ["response_format"] = new { type = "json_object" }
        };
        // 会議中の要約は約45秒ごとに走るので、Qwen3の思考を切って速さを優先する（約45秒→約8秒、2026-10-17実測）。
        // 最終要約は思考ありのまま正確さを優先する
        if (!request.UseThinking)
            payload["chat_template_kwargs"] = new { enable_thinking = false };
        using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Qwen要約に失敗しました ({(int)response.StatusCode}): {responseText}");

        using var envelope = JsonDocument.Parse(responseText);
        var content = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidDataException("Qwenの応答にcontentがありません。");
        content = StripCodeFence(content);
        var dto = JsonSerializer.Deserialize<SummaryDto>(content, JsonOptions)
            ?? throw new InvalidDataException("Qwenの要約JSONを読み取れませんでした。");
        return new ResearchSummary(DateTimeOffset.Now, request.IsFinal,
            Convert(dto.CurrentTopic), Convert(dto.Strengths), Convert(dto.QuestionsAndConcerns),
            Convert(dto.ImprovementSuggestions), Convert(dto.RevisionActions), Convert(dto.UnresolvedItems));
    }

    private static IReadOnlyList<SummaryItem> Convert(List<SummaryItemDto>? items) =>
        items?.Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => new SummaryItem(x.Text.Trim(), x.SlideNumbers ?? [], x.EvidenceTimes ?? []))
            .ToArray() ?? [];

    private static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine >= 0 && lastFence > firstNewLine ? trimmed[(firstNewLine + 1)..lastFence].Trim() : trimmed;
    }

    private static string BuildUserPrompt(SummaryRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine(request.IsFinal ? "これは会議終了後の最終要約です。" : "これは会議中のローリング要約です。");
        if (request.PreviousSummary is not null)
        {
            builder.AppendLine("\n前回要約:");
            builder.AppendLine(JsonSerializer.Serialize(request.PreviousSummary, JsonOptions));
        }
        if (request.Presentation.Slides.Count > 0)
        {
            builder.AppendLine("\n発表資料:");
            foreach (var slide in request.Presentation.Slides)
                builder.AppendLine($"[Slide {slide.Number}] {slide.Title}\n{slide.Text}\nNotes: {slide.Notes}");
        }
        builder.AppendLine("\n確定済み文字起こし:");
        foreach (var segment in request.Transcript.OrderBy(x => x.Start))
        {
            var speaker = segment.Speaker == SpeakerRole.Self ? "自分" : "相手";
            builder.AppendLine($"[{segment.Start:hh\\:mm\\:ss}-{segment.End:hh\\:mm\\:ss}] {speaker}: {segment.Text}");
        }
        return builder.ToString();
    }

    private const string SystemPrompt = """
あなたは日本語の研究ゼミ記録を整理するアシスタントです。与えられた発話だけを根拠にし、推測を事実として書かないでください。
発表資料との関連が明確な場合だけslideNumbersを付け、発話根拠の開始時刻をHH:MM:SS形式でevidenceTimesへ入れてください。
次のキーを必ず持つJSONオブジェクトだけを返してください。各値は {"text": string, "slideNumbers": number[], "evidenceTimes": string[]} の配列です。
currentTopic, strengths, questionsAndConcerns, improvementSuggestions, revisionActions, unresolvedItems
同じ内容を複数区分へ重複させず、発言されていない改善案を創作しないでください。
""";

    private sealed record SummaryDto(
        List<SummaryItemDto>? CurrentTopic,
        List<SummaryItemDto>? Strengths,
        List<SummaryItemDto>? QuestionsAndConcerns,
        List<SummaryItemDto>? ImprovementSuggestions,
        List<SummaryItemDto>? RevisionActions,
        List<SummaryItemDto>? UnresolvedItems);
    private sealed record SummaryItemDto(string Text, List<int>? SlideNumbers, List<string>? EvidenceTimes);
}
