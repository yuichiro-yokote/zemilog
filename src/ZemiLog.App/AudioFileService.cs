using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ZemiLog.App;

public sealed class AudioFileService : IDisposable
{
    // WindowsのFLACエンコーダー(MFT)は44.1kHz以上の出力形式しか列挙しないため、16kHzの録音は整数倍の48kHzへ上げてから変換する。
    private const int FlacSampleRate = 48000;
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(1);

    private WaveOut? _output;
    private readonly List<IDisposable> _readers = [];

    public async Task FinalizeRecordingAsync(string sessionDirectory, CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        foreach (var stem in new[] { "mic", "remote" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wavePath = Path.Combine(sessionDirectory, stem + ".recording.wav");
            var flacPath = Path.Combine(sessionDirectory, stem + ".flac");
            var tempPath = Path.Combine(sessionDirectory, stem + ".converting.flac");
            if (!File.Exists(wavePath)) continue;
            try
            {
                var hasAudio = await Task.Run(() => ConvertToFlac(wavePath, tempPath), cancellationToken);
                if (hasAudio) File.Move(tempPath, flacPath, overwrite: true);
                File.Delete(wavePath);
            }
            catch (Exception exception)
            {
                TryDelete(tempPath);
                failures.Add($"{stem}: {exception.Message}");
            }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" / ", failures));
    }

    private static bool ConvertToFlac(string wavePath, string flacPath)
    {
        TryDelete(flacPath);
        TimeSpan expected;
        using (var reader = new WaveFileReader(wavePath))
        {
            // 音声が1サンプルも無いWAV(その経路を使わなかった場合)はエンコーダーが失敗するので、FLACを作らずに消す。
            if (reader.Length == 0) return false;
            expected = reader.TotalTime;
            var samples = reader.ToSampleProvider();
            if (samples.WaveFormat.SampleRate != FlacSampleRate) samples = new WdlResamplingSampleProvider(samples, FlacSampleRate);
            MediaFoundationEncoder.EncodeToFlac(samples.ToWaveProvider16(), flacPath);
        }
        using var check = new MediaFoundationReader(flacPath);
        if ((check.TotalTime - expected).Duration() > DurationTolerance)
            throw new InvalidDataException($"変換後の長さが一致しません（WAV {expected:hh\\:mm\\:ss}、FLAC {check.TotalTime:hh\\:mm\\:ss}）");
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void PlayMixed(string sessionDirectory)
    {
        Stop();
        var providers = new List<ISampleProvider>();
        foreach (var source in new[] { "mic", "remote" })
        {
            var path = new[] { source + ".flac", source + ".recording.wav" }
                .Select(file => Path.Combine(sessionDirectory, file))
                .FirstOrDefault(File.Exists);
            if (path is null) continue;
            var reader = new AudioFileReader(path);
            _readers.Add(reader);
            providers.Add(reader);
        }
        if (providers.Count == 0) throw new FileNotFoundException("再生できる録音ファイルがありません。");
        var format = providers[0].WaveFormat;
        var compatible = providers.Select(provider => provider.WaveFormat.Equals(format)
            ? provider
            : new WdlResamplingSampleProvider(provider, format.SampleRate)).ToArray();
        var mixer = new MixingSampleProvider(compatible) { ReadFully = false };
        _output = new WaveOut();
        _output.Init(mixer);
        _output.Play();
    }

    public void Stop()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        foreach (var reader in _readers) reader.Dispose();
        _readers.Clear();
    }

    public void Dispose() => Stop();
}
