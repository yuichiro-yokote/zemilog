using ZemiLog.Core;

namespace ZemiLog.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ZemiLogTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Transcript_IsAppendOnlyAndReadableAfterDamagedTail()
    {
        var store = new SessionStore(_root);
        var metadata = Metadata();
        var session = await store.CreateAsync(metadata, null);
        var first = Segment(TimeSpan.FromSeconds(1), SpeakerRole.Self, "発表を始めます");
        var second = Segment(TimeSpan.FromSeconds(3), SpeakerRole.Remote, "この図を説明してください");
        await store.AppendTranscriptAsync(session.DirectoryPath, first);
        await store.AppendTranscriptAsync(session.DirectoryPath, second);
        await File.AppendAllTextAsync(Path.Combine(session.DirectoryPath, "transcript.jsonl"), "{damaged\n");

        var loaded = await store.LoadTranscriptAsync(session.DirectoryPath);

        Assert.Equal([first, second], loaded);
        Assert.Equal(3, File.ReadLines(Path.Combine(session.DirectoryPath, "transcript.jsonl")).Count());
    }

    [Fact]
    public async Task Metadata_IsAtomicallyUpdatedAndListedNewestFirst()
    {
        var store = new SessionStore(_root);
        var old = await store.CreateAsync(Metadata(DateTimeOffset.Now.AddHours(-1), "old"), null);
        var recent = await store.CreateAsync(Metadata(DateTimeOffset.Now, "recent"), null);
        await store.SaveMetadataAsync(recent.DirectoryPath, recent.Metadata with { Status = "Completed" });

        var sessions = await store.ListAsync();

        Assert.Equal(2, sessions.Count);
        Assert.Equal("recent", sessions[0].Metadata.Id);
        Assert.Equal("Completed", sessions[0].Metadata.Status);
        Assert.Equal("old", sessions[1].Metadata.Id);
        Assert.False(File.Exists(Path.Combine(recent.DirectoryPath, "session.json.tmp")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static TranscriptSegment Segment(TimeSpan start, SpeakerRole speaker, string text) =>
        new(Guid.NewGuid(), start, start.Add(TimeSpan.FromSeconds(1)), speaker, text, DateTimeOffset.Now);

    private static SessionMetadata Metadata(DateTimeOffset? started = null, string id = "test") =>
        new(id, started ?? DateTimeOffset.Now, null, "Recording", "Mic", "Teams", 123, "Headphones", null, "small");
}
