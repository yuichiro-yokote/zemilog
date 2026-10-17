using System.Threading.Channels;
using NAudio.Wave;
using ZemiLog.Core;

namespace ZemiLog.App;

public sealed class TranscriptionPipeline : IAsyncDisposable
{
    private readonly Channel<PendingChunk> _channel = Channel.CreateUnbounded<PendingChunk>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ITranscriptionProvider _provider;
    private readonly SessionStore _store;
    private readonly string _sessionDirectory;
    private readonly Task _worker;
    private readonly List<TranscriptSegment> _acceptedSegments = [];
    private int _pendingCount;

    public TranscriptionPipeline(ITranscriptionProvider provider, SessionStore store, string sessionDirectory)
    {
        _provider = provider;
        _store = store;
        _sessionDirectory = sessionDirectory;
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "pending"));
        _worker = Task.Run(ProcessAsync);
    }

    public int PendingCount => Volatile.Read(ref _pendingCount);
    public bool SuppressCrossChannelDuplicates { get; init; }
    public event Action<TranscriptSegment>? SegmentReady;
    public event Action<Exception>? ProcessingFailed;

    public async Task EnqueueAsync(AudioChunk chunk, CancellationToken cancellationToken = default)
    {
        var safeStart = (long)chunk.Start.TotalMilliseconds;
        var fileName = $"{safeStart:D10}-{chunk.Speaker}-{Guid.NewGuid():N}.wav";
        var path = Path.Combine(_sessionDirectory, "pending", fileName);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true))
        {
            using var writer = new WaveFileWriter(stream, AudioCaptureCoordinator.CaptureFormat);
            writer.Write(chunk.Pcm16, 0, chunk.Pcm16.Length);
        }
        Interlocked.Increment(ref _pendingCount);
        await _channel.Writer.WriteAsync(new PendingChunk(path, chunk), cancellationToken);
    }

    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        await _worker;
    }

    private async Task ProcessAsync()
    {
        await foreach (var pending in _channel.Reader.ReadAllAsync())
        {
            try
            {
                // 読み終えたら閉じる。開いたままだと後の File.Delete が「別のプロセスが使用中」で失敗する
                string text;
                await using (var stream = File.OpenRead(pending.Path))
                    text = await _provider.TranscribeAsync(stream);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var segment = new TranscriptSegment(Guid.NewGuid(), pending.Chunk.Start, pending.Chunk.End,
                        pending.Chunk.Speaker, text, DateTimeOffset.Now);
                    if (SuppressCrossChannelDuplicates && IsDuplicate(segment))
                    {
                        File.Delete(pending.Path);
                        continue;
                    }
                    await _store.AppendTranscriptAsync(_sessionDirectory, segment);
                    _acceptedSegments.Add(segment);
                    SegmentReady?.Invoke(segment);
                }
                File.Delete(pending.Path);
            }
            catch (Exception exception)
            {
                ProcessingFailed?.Invoke(exception);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingCount);
            }
        }
    }

    public async ValueTask DisposeAsync() => await CompleteAsync();

    private bool IsDuplicate(TranscriptSegment candidate)
    {
        var candidateText = Normalize(candidate.Text);
        return _acceptedSegments.Any(existing => existing.Speaker != candidate.Speaker
            && Math.Abs((existing.Start - candidate.Start).TotalSeconds) < 2
            && Similarity(Normalize(existing.Text), candidateText) >= 0.82);
    }

    private static string Normalize(string text) => new(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)).ToArray());

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        var common = left.Intersect(right).Count();
        return (double)common / Math.Max(left.Distinct().Count(), right.Distinct().Count());
    }

    private sealed record PendingChunk(string Path, AudioChunk Chunk);
}
