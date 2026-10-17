using System.Net;
using System.Text;
using System.Text.Json;
using ZemiLog.Core;

namespace ZemiLog.Tests;

public sealed class SummaryProviderTests
{
    [Fact]
    public async Task Summarize_ParsesQwenJsonAndKeepsEvidence()
    {
        var summaryJson = """
        {"currentTopic":[{"text":"評価方法","slideNumbers":[4],"evidenceTimes":["00:00:10"]}],"strengths":[],"questionsAndConcerns":[],"improvementSuggestions":[],"revisionActions":[],"unresolvedItems":[]}
        """;
        var handler = CreateHandler(summaryJson);
        var provider = new OpenAiCompatibleSummaryProvider(new HttpClient(handler), new QwenSettings("http://qwen.test/v1/", "qwen"));

        var result = await provider.SummarizeAsync(CreateRequest(isFinal: false));

        Assert.Equal("評価方法", Assert.Single(result.CurrentTopic).Text);
        Assert.Equal([4], result.CurrentTopic[0].SlideNumbers);
        Assert.Equal("http://qwen.test/v1/chat/completions", handler.RequestUri?.ToString());
        Assert.False(result.IsFinal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Summarize_UsesThinkingOnlyForFinalSummary(bool isFinal, bool allowThinking)
    {
        var handler = CreateHandler("""
        {"currentTopic":[],"strengths":[],"questionsAndConcerns":[],"improvementSuggestions":[],"revisionActions":[],"unresolvedItems":[]}
        """);
        var provider = new OpenAiCompatibleSummaryProvider(new HttpClient(handler), new QwenSettings("http://qwen.test/v1/", "qwen"));

        await provider.SummarizeAsync(CreateRequest(isFinal) with { AllowThinking = allowThinking });

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        var hasKwargs = body.RootElement.TryGetProperty("chat_template_kwargs", out var kwargs);
        if (isFinal && allowThinking)
            Assert.False(hasKwargs);
        else
            Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());
    }

    private static SummaryRequest CreateRequest(bool isFinal) => new(
        [new TranscriptSegment(Guid.NewGuid(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), SpeakerRole.Remote, "評価はどうしましたか", DateTimeOffset.Now)],
        new PresentationContext("deck.pptx", [new PresentationSlide(4, "評価", "実験条件", "")]), null, isFinal);

    private static StubHandler CreateHandler(string summaryJson)
    {
        var envelope = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = summaryJson } } } });
        return new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        });
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
