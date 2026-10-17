using System.Text.Json;

namespace ZemiLog.Core;

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonOptions) { WriteIndented = false };

    public SessionStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZemiLog", "sessions");
        Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }

    public async Task<SessionInfo> CreateAsync(SessionMetadata metadata, string? presentationPath, CancellationToken cancellationToken = default)
    {
        var directoryName = $"{metadata.StartedAt:yyyyMMdd-HHmmss}-{metadata.Id}";
        var directory = Path.Combine(RootDirectory, directoryName);
        Directory.CreateDirectory(directory);

        if (!string.IsNullOrWhiteSpace(presentationPath) && File.Exists(presentationPath))
        {
            File.Copy(presentationPath, Path.Combine(directory, "presentation" + Path.GetExtension(presentationPath)), false);
        }

        await SaveMetadataAsync(directory, metadata, cancellationToken);
        return new SessionInfo(directory, metadata);
    }

    public Task SaveMetadataAsync(string sessionDirectory, SessionMetadata metadata, CancellationToken cancellationToken = default) =>
        WriteJsonAtomicAsync(Path.Combine(sessionDirectory, "session.json"), metadata, cancellationToken);

    public async Task SavePresentationAsync(string sessionDirectory, PresentationContext presentation, CancellationToken cancellationToken = default) =>
        await WriteJsonAtomicAsync(Path.Combine(sessionDirectory, "deck.json"), presentation, cancellationToken);

    public async Task<PresentationContext> LoadPresentationAsync(string sessionDirectory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(sessionDirectory, "deck.json");
        if (!File.Exists(path)) return PresentationContext.Empty;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PresentationContext>(stream, JsonOptions, cancellationToken) ?? PresentationContext.Empty;
    }

    public Task AppendTranscriptAsync(string sessionDirectory, TranscriptSegment segment, CancellationToken cancellationToken = default) =>
        AppendJsonLineAsync(Path.Combine(sessionDirectory, "transcript.jsonl"), segment, cancellationToken);

    public Task AppendSummaryAsync(string sessionDirectory, ResearchSummary summary, CancellationToken cancellationToken = default) =>
        AppendJsonLineAsync(Path.Combine(sessionDirectory, "summaries.jsonl"), summary, cancellationToken);

    public Task MarkSummaryPendingAsync(string sessionDirectory, string reason, CancellationToken cancellationToken = default) =>
        WriteJsonAtomicAsync(Path.Combine(sessionDirectory, "summary-pending.json"), new { queuedAt = DateTimeOffset.Now, reason }, cancellationToken);

    public void ClearSummaryPending(string sessionDirectory)
    {
        var path = Path.Combine(sessionDirectory, "summary-pending.json");
        if (File.Exists(path)) File.Delete(path);
    }

    public async Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptAsync(string sessionDirectory, CancellationToken cancellationToken = default) =>
        await ReadJsonLinesAsync<TranscriptSegment>(Path.Combine(sessionDirectory, "transcript.jsonl"), cancellationToken);

    public async Task<IReadOnlyList<ResearchSummary>> LoadSummariesAsync(string sessionDirectory, CancellationToken cancellationToken = default) =>
        await ReadJsonLinesAsync<ResearchSummary>(Path.Combine(sessionDirectory, "summaries.jsonl"), cancellationToken);

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var sessions = new List<SessionInfo>();
        foreach (var directory in Directory.EnumerateDirectories(RootDirectory).OrderByDescending(x => x))
        {
            var metadataPath = Path.Combine(directory, "session.json");
            if (!File.Exists(metadataPath)) continue;
            try
            {
                await using var stream = File.OpenRead(metadataPath);
                var metadata = await JsonSerializer.DeserializeAsync<SessionMetadata>(stream, JsonOptions, cancellationToken);
                if (metadata is not null) sessions.Add(new SessionInfo(directory, metadata));
            }
            catch (JsonException)
            {
                // A partially written or manually edited session must not hide healthy sessions.
            }
        }

        return sessions;
    }

    public async Task<IReadOnlyList<string>> FindRecoverableWaveFilesAsync(CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return Directory.EnumerateFiles(RootDirectory, "*.recording.wav", SearchOption.AllDirectories).ToArray();
    }

    private static async Task AppendJsonLineAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, CompactJsonOptions);
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, true);
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        stream.Flush(true);
    }

    private static async Task<IReadOnlyList<T>> ReadJsonLinesAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        var result = new List<T>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, JsonOptions);
                if (item is not null) result.Add(item);
            }
            catch (JsonException)
            {
                // Ignore only the damaged line; append-only data before it remains readable.
            }
        }
        return result;
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(true);
        }
        File.Move(temporaryPath, path, true);
    }
}
