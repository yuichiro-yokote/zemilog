using NAudio.CoreAudioApi;
using NAudio.Wave;
using ZemiLog.Core;

namespace ZemiLog.App;

public sealed class AudioCaptureCoordinator : IAsyncDisposable
{
    public static readonly WaveFormat CaptureFormat = new(16000, 16, 1);
    private readonly object _micGate = new();
    private readonly object _remoteGate = new();
    private WasapiRecorder? _microphone;
    private WasapiRecorder? _remote;
    private WaveFileWriter? _microphoneWriter;
    private WaveFileWriter? _remoteWriter;
    private SpeechChunker? _microphoneChunker;
    private SpeechChunker? _remoteChunker;
    private DateTimeOffset _startedAt;
    private bool _running;

    public event Action<AudioChunk>? ChunkReady;
    public event Action<Exception>? CaptureFailed;

    public static IReadOnlyList<MicrophoneChoice> GetMicrophones()
    {
        using var enumerator = new MMDeviceEnumerator();
        // 前回の選択が無いときは2番目（Windowsの通話用の既定マイク）が選ばれる。ステレオミキサーの類は最後に回す
        var defaultId = enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var defaultDevice) ? defaultDevice.ID : null;
        return [MicrophoneChoice.None, .. enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new MicrophoneChoice(device.ID, device.FriendlyName))
            .OrderBy(choice => choice.IsMixer)
            .ThenBy(choice => choice.Id != defaultId)];
    }

    public static IReadOnlyList<ProcessChoice> GetAudioProcesses() =>
        [ProcessChoice.WholeSystem, .. System.Diagnostics.Process.GetProcesses()
            .Where(process => process.Id != Environment.ProcessId)
            .Select(process =>
            {
                try { return new ProcessChoice(process.Id, string.IsNullOrWhiteSpace(process.MainWindowTitle) ? process.ProcessName : process.MainWindowTitle); }
                catch { return null; }
            })
            .Where(process => process is not null)
            .Cast<ProcessChoice>()
            .DistinctBy(process => process.Id)
            .OrderBy(process => process.Name, StringComparer.CurrentCultureIgnoreCase)];

    public async Task StartAsync(string sessionDirectory, string microphoneId, int processId, bool speakerMode)
    {
        if (_running) throw new InvalidOperationException("録音はすでに開始されています。");
        _startedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(sessionDirectory);
        // マイクIDが空なら「使わない」。マイク側の録音・WAV・発話区切りを作らず、相手の音だけを録る
        if (microphoneId.Length > 0)
        {
            using var enumerator = new MMDeviceEnumerator();
            var micBuilder = new WasapiRecorderBuilder()
                .WithDevice(enumerator.GetDevice(microphoneId))
                .WithFormat(CaptureFormat);
            if (speakerMode) micBuilder.WithCommunicationsMode();
            _microphone = micBuilder.Build();
            _microphoneWriter = new WaveFileWriter(Path.Combine(sessionDirectory, "mic.recording.wav"), CaptureFormat);
            _microphoneChunker = new SpeechChunker(SpeakerRole.Self);
            _microphoneChunker.ChunkReady += ForwardChunk;
            _microphone.DataAvailable += OnMicrophoneData;
            _microphone.RecordingStopped += OnRecordingStopped;
        }
        var remoteBuilder = new WasapiRecorderBuilder().WithFormat(CaptureFormat);
        // PC全体のときは既定のスピーカー出力をループバックで録る。録音中に出力先を切り替えると追従しない
        _remote = processId == ProcessChoice.WholeSystemId
            ? remoteBuilder.WithLoopbackCapture().Build()
            : await remoteBuilder.WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree).BuildAsync();

        _remoteWriter = new WaveFileWriter(Path.Combine(sessionDirectory, "remote.recording.wav"), CaptureFormat);
        _remoteChunker = new SpeechChunker(SpeakerRole.Remote, threshold: 180);
        _remoteChunker.ChunkReady += ForwardChunk;
        _remote.DataAvailable += OnRemoteData;
        _remote.RecordingStopped += OnRecordingStopped;
        _running = true;
        _microphone?.StartRecording();
        _remote.StartRecording();
    }

    public async Task StopAsync()
    {
        if (!_running) return;
        _running = false;
        _microphone?.StopRecording();
        _remote?.StopRecording();
        await Task.Delay(200);
        var elapsed = DateTimeOffset.Now - _startedAt;
        _microphoneChunker?.Flush(elapsed);
        _remoteChunker?.Flush(elapsed);
        lock (_micGate) { _microphoneWriter?.Dispose(); _microphoneWriter = null; }
        lock (_remoteGate) { _remoteWriter?.Dispose(); _remoteWriter = null; }
        if (_microphone is not null) await _microphone.DisposeAsync();
        if (_remote is not null) await _remote.DisposeAsync();
        _microphone = null;
        _remote = null;
    }

    private void OnMicrophoneData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        try
        {
            lock (_micGate) _microphoneWriter?.Write(buffer);
            _microphoneChunker?.Feed(buffer, DateTimeOffset.Now - _startedAt);
        }
        catch (Exception exception) { CaptureFailed?.Invoke(exception); }
    }

    private void OnRemoteData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        try
        {
            lock (_remoteGate) _remoteWriter?.Write(buffer);
            _remoteChunker?.Feed(buffer, DateTimeOffset.Now - _startedAt);
        }
        catch (Exception exception) { CaptureFailed?.Invoke(exception); }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) CaptureFailed?.Invoke(args.Exception);
    }

    private void ForwardChunk(AudioChunk chunk) => ChunkReady?.Invoke(chunk);

    public async ValueTask DisposeAsync() => await StopAsync();
}
