using System.IO;
using Whisper.net;
using Whisper.net.Ggml;
using ZemiLog.Core;

namespace ZemiLog.App;

public sealed class WhisperTranscriptionProvider : ITranscriptionProvider, IAsyncDisposable
{
    private readonly string _modelPath;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public WhisperTranscriptionProvider(string? modelDirectory = null)
    {
        var directory = modelDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemiLog", "models");
        Directory.CreateDirectory(directory);
        _modelPath = Path.Combine(directory, "ggml-small-q5_0.bin");
    }

    public bool IsModelReady => File.Exists(_modelPath);

    public async Task EnsureModelAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (File.Exists(_modelPath)) return;
        var partialPath = _modelPath + ".partial";
        progress?.Report(0);
        await using var input = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
            GgmlType.Small, QuantizationType.Q5_0, cancellationToken);
        await using var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (input.CanSeek && input.Length > 0) progress?.Report((double)downloaded / input.Length);
        }
        await output.FlushAsync(cancellationToken);
        output.Close();
        File.Move(partialPath, _modelPath, true);
        progress?.Report(1);
    }

    public async Task<string> TranscribeAsync(Stream waveAudio, CancellationToken cancellationToken = default)
    {
        await EnsureModelAsync(cancellationToken: cancellationToken);
        _factory ??= WhisperFactory.FromPath(_modelPath);
        _processor ??= _factory.CreateBuilder()
            .WithLanguage("ja")
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            .Build();
        var parts = new List<string>();
        await foreach (var result in _processor.ProcessAsync(waveAudio, cancellationToken))
        {
            var text = result.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
        }
        return string.Join("", parts).Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null) await _processor.DisposeAsync();
        _factory?.Dispose();
    }
}
