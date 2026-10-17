using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ZemiLog.Core;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ZemiLog.App;

public partial class MainWindow : Window
{
    private readonly SessionStore _sessionStore = new();
    private readonly PresentationReader _presentationReader = new();
    private readonly MarkdownExporter _markdownExporter = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly AudioFileService _audioFiles = new();
    private readonly SemaphoreSlim _summaryGate = new(1, 1);
    private AudioCaptureCoordinator? _capture;
    private WhisperTranscriptionProvider? _whisper;
    private TranscriptionPipeline? _pipeline;
    private SessionInfo? _activeSession;
    private SessionInfo? _viewedSession;
    private SessionMetadata? _activeMetadata;
    private PresentationContext _presentation = PresentationContext.Empty;
    private ResearchSummary? _latestSummary;
    private CancellationTokenSource? _summaryLoopCts;
    private Task? _summaryLoopTask;
    private DispatcherTimer? _healthTimer;
    private bool _isRecording;
    private string? _presentationPath;
    private string? _savedMicrophoneId;
    private readonly ConcurrentDictionary<int, Task> _enqueueTasks = new();
    private int _enqueueSequence;

    public ObservableCollection<TranscriptRow> TranscriptRows { get; } = [];
    public ObservableCollection<SessionChoice> HistorySessions { get; } = [];
    private readonly List<TranscriptSegment> _transcript = [];
    public bool IsRecording => _isRecording;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var (settings, apiKey) = await _settingsStore.LoadAsync();
            QwenBaseUrlText.Text = settings.BaseUrl;
            QwenModelText.Text = settings.Model;
            QwenApiKeyBox.Password = apiKey;
            PlaybackModeCombo.SelectedIndex = settings.PlaybackMode == "Speakers" ? 1 : 0;
            _savedMicrophoneId = settings.MicrophoneId;
            RefreshDevices();
            await RefreshHistoryAsync();
            var recoverable = await _sessionStore.FindRecoverableWaveFilesAsync();
            StatusText.Text = recoverable.Count > 0 ? $"未変換の録音が {recoverable.Count} 件あります。履歴から再生できます。" : "準備完了";
        }
        catch (Exception exception) { ShowError("起動準備に失敗しました", exception); }
    }

    private void RefreshDevices()
    {
        // 前回録音したときのマイク（「使わない」やステレオミキサーも含む）を選び直す
        var selectedMic = (MicrophoneCombo.SelectedItem as MicrophoneChoice)?.Id ?? _savedMicrophoneId;
        var selectedProcess = (ProcessCombo.SelectedItem as ProcessChoice)?.Id;
        MicrophoneCombo.ItemsSource = AudioCaptureCoordinator.GetMicrophones();
        ProcessCombo.ItemsSource = AudioCaptureCoordinator.GetAudioProcesses();
        MicrophoneCombo.SelectedItem = MicrophoneCombo.Items.Cast<MicrophoneChoice>().FirstOrDefault(x => x.Id == selectedMic)
            ?? MicrophoneCombo.Items.Cast<MicrophoneChoice>().FirstOrDefault(x => !x.IsNone)
            ?? MicrophoneChoice.None;
        // 既定は「PC全体の音」。アプリの一覧は多すぎて選びにくいので、必要なときだけ選び直す
        ProcessCombo.SelectedItem = ProcessCombo.Items.Cast<ProcessChoice>().FirstOrDefault(x => x.Id == selectedProcess)
            ?? ProcessChoice.WholeSystem;
    }

    private async Task RefreshHistoryAsync()
    {
        var selectedPath = _viewedSession?.DirectoryPath;
        HistorySessions.Clear();
        foreach (var session in await _sessionStore.ListAsync()) HistorySessions.Add(new SessionChoice(session));
        HistoryCombo.ItemsSource = HistorySessions;
        HistoryCombo.SelectedItem = HistorySessions.FirstOrDefault(x => x.Session.DirectoryPath == selectedPath);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording) return;
        if (MicrophoneCombo.SelectedItem is not MicrophoneChoice microphone || ProcessCombo.SelectedItem is not ProcessChoice process)
        {
            MessageBox.Show(this, "マイクと会議アプリを選択してください。", "ゼミログ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!process.IsWholeSystem && !IsProcessAlive(process.Id))
        {
            MessageBox.Show(this, "選択した会議アプリは終了しています。再読込してください。", "ゼミログ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            ToggleRecordingUi(true);
            var speakerMode = SelectedPlaybackMode() == "Speakers";
            if (speakerMode)
                MessageBox.Show(this, "スピーカー再生では相手音声がマイクにも入り、発話が重複する場合があります。可能ならヘッドホンを使用してください。", "重複録音の注意", MessageBoxButton.OK, MessageBoxImage.Warning);
            await SaveSettingsAsync();
            _presentation = string.IsNullOrWhiteSpace(_presentationPath) ? PresentationContext.Empty : _presentationReader.Read(_presentationPath);
            var startedAt = DateTimeOffset.Now;
            _activeMetadata = new SessionMetadata(Guid.NewGuid().ToString("N")[..10], startedAt, null, "Recording", microphone.Name,
                process.Name, process.Id, SelectedPlaybackMode(), _presentationPath is null ? null : Path.GetFileName(_presentationPath), "ggml-small-q5_0");
            _activeSession = await _sessionStore.CreateAsync(_activeMetadata, _presentationPath);
            _viewedSession = _activeSession;
            await _sessionStore.SavePresentationAsync(_activeSession.DirectoryPath, _presentation);
            TranscriptRows.Clear();
            _transcript.Clear();
            _latestSummary = null;
            SummaryText.Text = "文字起こしが確定すると、約45秒ごとに要約します。";
            _whisper = new WhisperTranscriptionProvider();
            if (!_whisper.IsModelReady)
            {
                StatusText.Text = "Whisper smallモデルを初回ダウンロード中…";
                var progress = new Progress<double>(value => StatusText.Text = $"Whisperモデルをダウンロード中… {value:P0}");
                await _whisper.EnsureModelAsync(progress);
            }
            _pipeline = new TranscriptionPipeline(_whisper, _sessionStore, _activeSession.DirectoryPath)
            {
                SuppressCrossChannelDuplicates = speakerMode
            };
            _pipeline.SegmentReady += OnSegmentReady;
            _pipeline.ProcessingFailed += exception => Dispatcher.Invoke(() => StatusText.Text = $"文字起こし保留: {exception.Message}");
            _capture = new AudioCaptureCoordinator();
            _capture.ChunkReady += OnAudioChunkReady;
            _capture.CaptureFailed += exception => Dispatcher.Invoke(() => StatusText.Text = $"録音経路エラー: {exception.Message}");
            await _capture.StartAsync(_activeSession.DirectoryPath, microphone.Id, process.Id, speakerMode);
            _isRecording = true;
            StatusText.Text = "録音中";
            StartHealthTimer(process.Id);
            _summaryLoopCts = new CancellationTokenSource();
            _summaryLoopTask = RunSummaryLoopAsync(_summaryLoopCts.Token);
            await RefreshHistoryAsync();
        }
        catch (Exception exception)
        {
            ToggleRecordingUi(false);
            await CleanupAfterFailedStartAsync(exception);
            ShowError("録音を開始できませんでした", exception);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopRecordingAsync();

    private async Task StopRecordingAsync()
    {
        if (!_isRecording || _activeSession is null || _activeMetadata is null) return;
        _isRecording = false;
        _healthTimer?.Stop();
        _summaryLoopCts?.Cancel();
        StopButton.IsEnabled = false;
        StatusText.Text = "録音を停止しています…";
        Exception? conversionError = null;
        try
        {
            if (_capture is not null) await _capture.StopAsync();
            if (_enqueueTasks.Count > 0) await Task.WhenAll(_enqueueTasks.Values);
            StatusText.Text = "残りの文字起こしを処理しています…";
            if (_pipeline is not null) await _pipeline.CompleteAsync();
            if (_summaryLoopTask is not null) await _summaryLoopTask;
            await GenerateSummaryAsync(true);
            StatusText.Text = "録音をFLACへ変換しています…";
            try { await _audioFiles.FinalizeRecordingAsync(_activeSession.DirectoryPath); }
            catch (Exception exception) { conversionError = exception; }
            _activeMetadata = _activeMetadata with { EndedAt = DateTimeOffset.Now, Status = "Completed" };
            await _sessionStore.SaveMetadataAsync(_activeSession.DirectoryPath, _activeMetadata);
            StatusText.Text = "保存完了";
        }
        catch (Exception exception)
        {
            _activeMetadata = _activeMetadata with { EndedAt = DateTimeOffset.Now, Status = "NeedsRecovery", LastError = exception.Message };
            await _sessionStore.SaveMetadataAsync(_activeSession.DirectoryPath, _activeMetadata);
            ShowError("停止処理の一部に失敗しました。録音WAVは保持しています", exception);
        }
        finally
        {
            if (_whisper is not null) await _whisper.DisposeAsync();
            _capture = null;
            _pipeline = null;
            _whisper = null;
            ToggleRecordingUi(false);
            _viewedSession = _activeSession;
            _activeSession = null;
            _activeMetadata = null;
            await RefreshHistoryAsync();
        }
        // 「保存完了」で上書きされないよう、画面の後片付けが済んでから表示する。
        if (conversionError is not null) ShowError("保存しましたが、FLAC変換に失敗したため録音はWAVのまま残しています", conversionError);
    }

    private void OnAudioChunkReady(AudioChunk chunk)
    {
        var pipeline = _pipeline;
        if (pipeline is null) return;
        var sequence = Interlocked.Increment(ref _enqueueSequence);
        var task = Task.Run(async () =>
        {
            try
            {
                await pipeline.EnqueueAsync(chunk);
                Dispatcher.Invoke(() => QueueText.Text = $"未処理 {pipeline.PendingCount} 件");
            }
            catch (Exception exception) { Dispatcher.Invoke(() => StatusText.Text = $"発話保存エラー: {exception.Message}"); }
        });
        _enqueueTasks[sequence] = task;
        _ = task.ContinueWith(_completedTask => _enqueueTasks.TryRemove(sequence, out var _removedTask), TaskScheduler.Default);
    }

    private void OnSegmentReady(TranscriptSegment segment)
    {
        Dispatcher.Invoke(() =>
        {
            _transcript.Add(segment);
            _transcript.Sort((left, right) => left.Start.CompareTo(right.Start));
            TranscriptRows.Add(TranscriptRow.From(segment));
            var ordered = TranscriptRows.OrderBy(x => x.Start).ToArray();
            TranscriptRows.Clear();
            foreach (var item in ordered) TranscriptRows.Add(item);
            TranscriptList.ScrollIntoView(TranscriptRows.LastOrDefault());
            QueueText.Text = _pipeline?.PendingCount > 0 ? $"未処理 {_pipeline.PendingCount} 件" : "追いついています";
        });
    }

    private async Task RunSummaryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken);
                await GenerateSummaryAsync(false, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task GenerateSummaryAsync(bool isFinal, CancellationToken cancellationToken = default)
    {
        var targetSession = _activeSession ?? _viewedSession;
        if (targetSession is null || _transcript.Count == 0) return;
        if (!await _summaryGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var qwen = CurrentQwenSettings();
            if (!qwen.IsConfigured) { Dispatcher.Invoke(() => SummaryUpdatedText.Text = "Qwen未設定・要約保留"); return; }
            // 最終要約は思考ありで会議全体を読むので時間がかかる（24分相当の会議に148秒、2026-10-17実測）。
            // 話題が多いと思考が長引く（ニュース14分で思考2.7万トークン・770秒、2026-10-18実測）ので、300秒で打ち切って思考なしで取り直す
            if (isFinal) qwen = qwen with { TimeoutSeconds = Math.Max(qwen.TimeoutSeconds, 300) };
            Dispatcher.Invoke(() => SummaryUpdatedText.Text = isFinal ? "最終要約中…" : "更新中…");
            using var http = new HttpClient();
            var provider = new OpenAiCompatibleSummaryProvider(http, qwen);
            var snapshot = _transcript.ToArray();
            ResearchSummary summary;
            var withoutThinking = false;
            try { summary = await provider.SummarizeAsync(new SummaryRequest(snapshot, _presentation, _latestSummary, isFinal), cancellationToken); }
            catch (Exception) when (isFinal)
            {
                // 思考ありの最終要約が時間切れなどで失敗したら、思考なしで1回だけ取り直して最終版を必ず残す
                // （同じニュース14分を思考なしで送ると33秒、2026-10-18実測）
                Dispatcher.Invoke(() => SummaryUpdatedText.Text = "最終要約を思考なしで取り直し中…");
                using var retryHttp = new HttpClient();
                var retryProvider = new OpenAiCompatibleSummaryProvider(retryHttp, CurrentQwenSettings() with { TimeoutSeconds = 300 });
                summary = await retryProvider.SummarizeAsync(new SummaryRequest(snapshot, _presentation, _latestSummary, true, AllowThinking: false));
                withoutThinking = true;
            }
            _latestSummary = summary;
            await _sessionStore.AppendSummaryAsync(targetSession.DirectoryPath, summary, cancellationToken);
            _sessionStore.ClearSummaryPending(targetSession.DirectoryPath);
            Dispatcher.Invoke(() =>
            {
                SummaryText.Text = FormatSummary(summary);
                SummaryUpdatedText.Text = $"{summary.GeneratedAt:HH:mm:ss} 更新" + (summary.IsFinal ? "・最終版" : string.Empty) + (withoutThinking ? "（思考なし）" : string.Empty);
            });
        }
        catch (OperationCanceledException) when (!isFinal) { }
        catch (Exception exception)
        {
            await _sessionStore.MarkSummaryPendingAsync(targetSession.DirectoryPath, exception.Message, CancellationToken.None);
            Dispatcher.Invoke(() => { SummaryUpdatedText.Text = "接続待ち・要約保留"; StatusText.Text = $"Qwen要約は保留中: {exception.Message}"; });
        }
        finally { _summaryGate.Release(); }
    }

    private static string FormatSummary(ResearchSummary summary)
    {
        var builder = new StringBuilder();
        Append("現在の話題", summary.CurrentTopic); Append("良かった点", summary.Strengths); Append("質問・懸念", summary.QuestionsAndConcerns);
        Append("改善提案", summary.ImprovementSuggestions); Append("修正アクション", summary.RevisionActions); Append("未解決事項", summary.UnresolvedItems);
        return builder.ToString();
        void Append(string title, IReadOnlyList<SummaryItem> items)
        {
            builder.AppendLine(title);
            if (items.Count == 0) builder.AppendLine("  ・（なし）");
            foreach (var item in items)
            {
                var references = item.SlideNumbers.Count > 0 ? $" [Slide {string.Join(", ", item.SlideNumbers)}]" : string.Empty;
                builder.AppendLine($"  ・{item.Text}{references}");
            }
            builder.AppendLine();
        }
    }

    private async void Summarize_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is not null || _viewedSession is not null) await GenerateSummaryAsync(false);
        else MessageBox.Show(this, "要約する履歴を選択してください。", "ゼミログ");
    }

    private async void SaveAndTestQwen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
            var settings = CurrentQwenSettings();
            if (!settings.IsConfigured) throw new InvalidOperationException("Base URLとモデル名を入力してください。");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Min(settings.TimeoutSeconds, 15)) };
            if (!string.IsNullOrWhiteSpace(settings.ApiKey)) http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
            var response = await http.GetAsync(new Uri(new Uri(settings.BaseUrl.TrimEnd('/') + "/"), "models"));
            response.EnsureSuccessStatusCode();
            MessageBox.Show(this, "Qwenサーバーへ接続できました。", "接続確認", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) { ShowError("Qwenサーバーへ接続できません", exception); }
    }

    private async Task SaveSettingsAsync()
    {
        _savedMicrophoneId = (MicrophoneCombo.SelectedItem as MicrophoneChoice)?.Id ?? _savedMicrophoneId;
        await _settingsStore.SaveAsync(new AppSettings(QwenBaseUrlText.Text.Trim(), QwenModelText.Text.Trim(), 90, SelectedPlaybackMode(), _savedMicrophoneId), QwenApiKeyBox.Password);
    }

    private QwenSettings CurrentQwenSettings() =>
        _settingsStore.ToQwenSettings(new AppSettings(QwenBaseUrlText.Text.Trim(), QwenModelText.Text.Trim(), 90, SelectedPlaybackMode()), QwenApiKeyBox.Password);

    private void SelectPresentation_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PowerPoint (*.pptx)|*.pptx", Title = "発表資料を選択" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var context = _presentationReader.Read(dialog.FileName);
            _presentationPath = dialog.FileName;
            PresentationPathText.Text = $"{dialog.FileName}（{context.Slides.Count}枚）";
        }
        catch (Exception exception) { ShowError("PowerPointを読み取れません", exception); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var session = _activeSession ?? _viewedSession;
        if (session is null) { MessageBox.Show(this, "出力する履歴を選択してください。", "ゼミログ"); return; }
        try
        {
            var transcript = ReferenceEquals(session, _activeSession) ? _transcript.ToArray() : await _sessionStore.LoadTranscriptAsync(session.DirectoryPath);
            var summaries = ReferenceEquals(session, _activeSession) && _latestSummary is not null ? [_latestSummary] : await _sessionStore.LoadSummariesAsync(session.DirectoryPath);
            var dialog = new SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = $"ゼミ記録_{session.Metadata.StartedAt:yyyyMMdd_HHmm}.md" };
            if (dialog.ShowDialog(this) != true) return;
            await _markdownExporter.ExportAsync(dialog.FileName, session.Metadata, transcript, summaries.LastOrDefault());
            StatusText.Text = $"Markdownを保存しました: {dialog.FileName}";
        }
        catch (Exception exception) { ShowError("Markdownを出力できません", exception); }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        var session = _activeSession ?? _viewedSession;
        if (session is null) { MessageBox.Show(this, "再生する履歴を選択してください。", "ゼミログ"); return; }
        try { _audioFiles.PlayMixed(session.DirectoryPath); StatusText.Text = "録音を再生中"; }
        catch (Exception exception) { ShowError("録音を再生できません", exception); }
    }

    private async void HistoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRecording || HistoryCombo.SelectedItem is not SessionChoice choice) return;
        try
        {
            _viewedSession = choice.Session;
            var transcript = await _sessionStore.LoadTranscriptAsync(choice.Session.DirectoryPath);
            var summaries = await _sessionStore.LoadSummariesAsync(choice.Session.DirectoryPath);
            _presentation = await _sessionStore.LoadPresentationAsync(choice.Session.DirectoryPath);
            _transcript.Clear(); _transcript.AddRange(transcript); TranscriptRows.Clear();
            foreach (var segment in transcript.OrderBy(x => x.Start)) TranscriptRows.Add(TranscriptRow.From(segment));
            _latestSummary = summaries.LastOrDefault();
            SummaryText.Text = _latestSummary is null ? "このセッションには要約がありません。" : FormatSummary(_latestSummary);
            SummaryUpdatedText.Text = choice.Session.Metadata.Status;
        }
        catch (Exception exception) { ShowError("履歴を読み取れません", exception); }
    }

    private void StartHealthTimer(int processId)
    {
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _healthTimer.Tick += (_, _) =>
        {
            if (_isRecording && processId != ProcessChoice.WholeSystemId && !IsProcessAlive(processId)) StatusText.Text = "会議アプリが終了しました。PC全体録音には切り替えていません。録音を停止してください。";
            if (_isRecording && _pipeline is not null) QueueText.Text = _pipeline.PendingCount > 0 ? $"未処理 {_pipeline.PendingCount} 件" : "追いついています";
        };
        _healthTimer.Start();
    }

    private static bool IsProcessAlive(int processId) { try { return !Process.GetProcessById(processId).HasExited; } catch { return false; } }
    private string SelectedPlaybackMode() => (PlaybackModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Headphones";

    private void ToggleRecordingUi(bool startingOrRecording)
    {
        StartButton.IsEnabled = !startingOrRecording; StopButton.IsEnabled = startingOrRecording;
        MicrophoneCombo.IsEnabled = !startingOrRecording; ProcessCombo.IsEnabled = !startingOrRecording; HistoryCombo.IsEnabled = !startingOrRecording;
    }

    private async Task CleanupAfterFailedStartAsync(Exception exception)
    {
        try { if (_capture is not null) await _capture.StopAsync(); } catch { }
        if (_activeSession is not null && _activeMetadata is not null)
        {
            _activeMetadata = _activeMetadata with { EndedAt = DateTimeOffset.Now, Status = "NeedsRecovery", LastError = exception.Message };
            await _sessionStore.SaveMetadataAsync(_activeSession.DirectoryPath, _activeMetadata);
        }
        _isRecording = false;
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isRecording)
        {
            e.Cancel = true;
            MessageBox.Show(this, "録音を停止して保存してから終了してください。", "ゼミログ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (System.Windows.Application.Current is App app && !app.IsExplicitShutdown)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _audioFiles.Dispose();
    }

    private void ShowError(string title, Exception exception)
    {
        StatusText.Text = $"{title}: {exception.Message}";
        MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

public sealed record TranscriptRow(TimeSpan Start, string Time, string Speaker, string Text, System.Windows.Media.Brush SpeakerColor)
{
    public static TranscriptRow From(TranscriptSegment segment) => new(segment.Start, segment.Start.ToString(@"hh\:mm\:ss"),
        segment.Speaker == SpeakerRole.Self ? "自分" : "相手", segment.Text, segment.Speaker == SpeakerRole.Self ? Brushes.DarkCyan : Brushes.DarkOrange);
}

public sealed record SessionChoice(SessionInfo Session)
{
    public string DisplayName => $"{Session.Metadata.StartedAt.LocalDateTime:MM/dd HH:mm}  {Session.Metadata.TargetProcessName}  [{Session.Metadata.Status}]";
}
