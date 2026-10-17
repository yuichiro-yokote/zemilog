using System.IO;
using ZemiLog.Core;

namespace ZemiLog.App;

public sealed class SpeechChunker
{
    private readonly SpeakerRole _speaker;
    private readonly int _bytesPerSecond;
    private readonly double _threshold;
    private readonly MemoryStream _active = new();
    private readonly Queue<byte[]> _preRoll = new();
    private readonly object _gate = new();
    private int _preRollBytes;
    private double _silenceSeconds;
    private TimeSpan _chunkStart;

    public SpeechChunker(SpeakerRole speaker, int sampleRate = 16000, double threshold = 260)
    {
        _speaker = speaker;
        _bytesPerSecond = sampleRate * sizeof(short);
        _threshold = threshold;
    }

    public event Action<AudioChunk>? ChunkReady;

    public void Feed(ReadOnlySpan<byte> data, TimeSpan now)
    {
        if (data.Length < 2) return;
        AudioChunk? completed = null;
        lock (_gate)
        {
            var copy = data.ToArray();
            var isSpeech = CalculateRms(data) >= _threshold;
            if (_active.Length == 0)
            {
                AddPreRoll(copy);
                if (!isSpeech) return;
                var preRollDuration = TimeSpan.FromSeconds((double)_preRollBytes / _bytesPerSecond);
                _chunkStart = now - preRollDuration;
                while (_preRoll.TryDequeue(out var packet)) _active.Write(packet);
                _preRollBytes = 0;
                _silenceSeconds = 0;
            }
            else
            {
                _active.Write(copy);
                _silenceSeconds = isSpeech ? 0 : _silenceSeconds + (double)copy.Length / _bytesPerSecond;
            }

            var activeSeconds = (double)_active.Length / _bytesPerSecond;
            if ((_silenceSeconds >= 0.8 && activeSeconds >= 0.45) || activeSeconds >= 12)
                completed = Complete(now);
        }
        if (completed is not null) ChunkReady?.Invoke(completed);
    }

    public void Flush(TimeSpan now)
    {
        AudioChunk? completed;
        lock (_gate) completed = _active.Length == 0 ? null : Complete(now);
        if (completed is not null) ChunkReady?.Invoke(completed);
    }

    private void AddPreRoll(byte[] packet)
    {
        _preRoll.Enqueue(packet);
        _preRollBytes += packet.Length;
        var maximum = (int)(_bytesPerSecond * 0.3);
        while (_preRollBytes > maximum && _preRoll.TryDequeue(out var removed)) _preRollBytes -= removed.Length;
    }

    private AudioChunk Complete(TimeSpan now)
    {
        var bytes = _active.ToArray();
        _active.SetLength(0);
        _silenceSeconds = 0;
        return new AudioChunk(bytes, _chunkStart < TimeSpan.Zero ? TimeSpan.Zero : _chunkStart, now, _speaker);
    }

    private static double CalculateRms(ReadOnlySpan<byte> data)
    {
        double sum = 0;
        var sampleCount = data.Length / 2;
        for (var index = 0; index < sampleCount * 2; index += 2)
        {
            var sample = (short)(data[index] | data[index + 1] << 8);
            sum += (double)sample * sample;
        }
        return Math.Sqrt(sum / sampleCount);
    }
}
