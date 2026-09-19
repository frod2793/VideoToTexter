using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoToText.Application;
using VideoToText.Infrastructure;
using VideoToText.Core;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Linq;
using Xabe.FFmpeg;
using System.Net.Http;
using System.Threading;
using Avalonia.Threading;
using VideoToText.Avalonia.Services;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VideoToText.Checks")]

namespace VideoToText.Avalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public static IReadOnlyList<string> SupportedMediaPatterns { get; } =
        new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.mp3", "*.m4a", "*.wav" };

    internal static bool IsSupportedMediaPath(string path)
    {
        string extension = Path.GetExtension(path);
        return SupportedMediaPatterns.Any(pattern =>
            string.Equals(extension, pattern.Substring(1), StringComparison.OrdinalIgnoreCase));
    }

    internal static string GetPrimaryModelsDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoTexter", "Models");
    }

    internal static string GetLegacyModelsDirectory()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
    }

    internal static string? ResolveModelPath(string fileName, string? primaryDir = null, string? legacyDir = null)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        string primary = primaryDir ?? GetPrimaryModelsDirectory();
        string primaryPath = Path.Combine(primary, fileName);
        if (File.Exists(primaryPath)) return primaryPath;

        string legacy = legacyDir ?? GetLegacyModelsDirectory();
        string legacyPath = Path.Combine(legacy, fileName);
        if (File.Exists(legacyPath)) return legacyPath;

        return null;
    }

    internal static async Task DownloadModelFileAsync(
        HttpClient httpClient,
        string url,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string partPath = destinationPath + ".part";
        if (File.Exists(partPath))
        {
            try { File.Delete(partPath); } catch { }
        }

        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        long totalRead = 0;

        try
        {
            using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                byte[] buffer = new byte[8192];
                int readCount;
                while ((readCount = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, readCount, cancellationToken);
                    totalRead += readCount;
                    if (contentLength.HasValue && contentLength.Value > 0)
                    {
                        progress?.Report((double)totalRead / contentLength.Value * 100.0);
                    }
                }
                await fileStream.FlushAsync(cancellationToken);
            }

            if (contentLength.HasValue && contentLength.Value >= 0)
            {
                if (totalRead != contentLength.Value)
                {
                    throw new IOException($"다운로드된 바이트 수({totalRead})가 Content-Length({contentLength.Value})와 일치하지 않습니다.");
                }
            }

            File.Move(partPath, destinationPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(partPath)) File.Delete(partPath);
            }
            catch { }
            throw;
        }
    }

    private readonly IJobQueueManager m_queueManager;
    private WhisperTranscriptionService? m_cachedSttService;
    private VideoToTextApp? m_cachedApp;
    private string? m_lastModelPath;
    private FFmpegMicrophoneRecorder? m_microphoneRecorder;
    private DispatcherTimer? m_recordingTimer;
    private DateTime m_recordingStartedAt;

    [ObservableProperty]
    private string m_videoPath = string.Empty;

    [ObservableProperty]
    private string m_outputPath = string.Empty;

    [ObservableProperty]
    private string m_status = "준비됨";

    [ObservableProperty]
    private double m_progress = 0;

    [ObservableProperty]
    private double m_totalProgress = 0;

    [ObservableProperty]
    private string m_totalProgressText = "";

    [ObservableProperty]
    private string m_currentJobProgressText = "";

    [ObservableProperty]
    private bool m_isProcessing = false;

    [ObservableProperty]
    private string m_selectedModel = string.Empty;

    [ObservableProperty]
    private string m_estimatedTimeRemaining = string.Empty;

    [ObservableProperty]
    private double m_downloadProgress = 0;

    [ObservableProperty]
    private bool m_isDownloading = false;

    [ObservableProperty]
    private string? m_selectedLog;

    [ObservableProperty]
    private AudioInputDevice? m_selectedAudioInputDevice;

    [ObservableProperty]
    private bool m_isRecording;

    [ObservableProperty]
    private string m_recordingElapsed = "00:00:00";

    public ObservableCollection<string> Logs { get; } = new();

    public ObservableCollection<ModelInfo> AvailableModels { get; } = new();

    public ObservableCollection<AudioInputDevice> AudioInputDevices { get; } = new();

    public string RecordingButtonText => IsRecording ? "녹음 중지" : "녹음 시작";

    // UI의 리스트 뷰가 바인딩되는 컬렉션 (ReadOnly 같은 느낌으로 사용)
    public ObservableCollection<JobViewModel> Jobs { get; } = new();

    public MainWindowViewModel()
    {
        AudioInputDevices.Add(new AudioInputDevice(null, "시스템 기본 마이크"));
        SelectedAudioInputDevice = AudioInputDevices[0];
        string? ffmpegPath = InitializeFfmpegPath();
        if (ffmpegPath is not null) m_microphoneRecorder = new FFmpegMicrophoneRecorder(ffmpegPath);
        InitializeModels();
        RefreshAvailableModels();

        // 1. 큐 매니저 단일 생성 (SSOT)
        m_queueManager = new JobQueueManager(GetOrCreateVideoToTextApp);

        // 2. 단방향 이벤트 바인딩
        m_queueManager.JobAdded += OnJobAdded;
        m_queueManager.JobRemoved += OnJobRemoved;
        m_queueManager.JobUpdated += OnJobUpdated;
        m_queueManager.ProcessingStateChanged += OnProcessingStateChanged;
        m_queueManager.SegmentDetected += OnSegmentDetected;

        // 3. 백그라운드 워커 즉시 구동 (작업이 없으면 무한 대기함)
        // 실행이 끝날 일이 없으므로 discard
        _ = m_queueManager.StartBackgroundWorkerAsync();
    }

    private VideoToTextApp GetOrCreateVideoToTextApp(TranscriptionJobDTO job)
    {
        string modelName = !string.IsNullOrEmpty(job.ModelName) ? job.ModelName : SelectedModel;
        string? modelPath = ResolveModelPath(modelName);
        if (modelPath == null || !File.Exists(modelPath)) throw new FileNotFoundException("모델 파일이 없습니다: " + modelName);

        Environment.SetEnvironmentVariable("GGML_METAL", "0");

        if (m_cachedSttService == null || m_cachedApp == null || m_lastModelPath != modelPath)
        {
            m_cachedSttService?.Dispose();
            m_cachedSttService = new WhisperTranscriptionService(modelPath);
            var audioExtractor = new FFmpegAudioExtractor();
            var exporters = new List<ISubtitleExportService> { new SrtSubtitleExporter(), new PlainTextExporter() };
            m_cachedApp = new VideoToTextApp(audioExtractor, m_cachedSttService, exporters);
            m_lastModelPath = modelPath;
        }

        return m_cachedApp;
    }

    private string? InitializeFfmpegPath()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string embeddedFfmpeg = Path.Combine(appDir, "ffmpeg");
        string embeddedFfprobe = Path.Combine(appDir, "ffprobe");

        if (File.Exists(embeddedFfmpeg) && File.Exists(embeddedFfprobe))
        {
            FFmpeg.SetExecutablesPath(appDir);
            return embeddedFfmpeg;
        }

        string homebrewPath = "/opt/homebrew/bin";
        if (File.Exists(Path.Combine(homebrewPath, "ffmpeg")))
        {
            FFmpeg.SetExecutablesPath(homebrewPath);
            return Path.Combine(homebrewPath, "ffmpeg");
        }

        string localBinPath = "/usr/local/bin";
        if (File.Exists(Path.Combine(localBinPath, "ffmpeg")))
        {
            FFmpeg.SetExecutablesPath(localBinPath);
            return Path.Combine(localBinPath, "ffmpeg");
        }

        return null;
    }

    private void InitializeModels()
    {
        string[] modelNames = { "tiny", "base", "small", "medium", "large-v1", "large-v2", "large-v3", "large-v3-turbo" };
        foreach (var name in modelNames)
        {
            AvailableModels.Add(new ModelInfo { Name = name, FileName = $"ggml-{name}.bin" });
        }
    }

    internal void RefreshAvailableModels(string? primaryDir = null, string? legacyDir = null)
    {
        foreach (var model in AvailableModels)
        {
            string? fullPath = ResolveModelPath(model.FileName, primaryDir, legacyDir);
            model.IsDownloaded = fullPath != null;
        }

        // 기존 SelectedModel이 ResolveModelPath로 유효하면 유지
        bool isCurrentSelectedValid = !string.IsNullOrWhiteSpace(SelectedModel) && ResolveModelPath(SelectedModel, primaryDir, legacyDir) != null;

        if (isCurrentSelectedValid)
        {
            Status = "준비됨";
        }
        else
        {
            // 초기 생성 시에만 선택값이 비어있거나 무효일 때 첫 다운로드 모델로 설정
            var firstDownloaded = AvailableModels.FirstOrDefault(m => m.IsDownloaded);
            if (firstDownloaded != null)
            {
                SelectedModel = firstDownloaded.FileName;
                Status = "준비됨";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(SelectedModel) && AvailableModels.Count > 0)
                {
                    SelectedModel = AvailableModels[0].FileName;
                }
                Status = "사용 가능한 모델이 없습니다. [모델 다운로드] 버튼을 눌러주세요.";
            }
        }
    }

    private DateTime? m_queueStartTime;

    // --- 큐 매니저 이벤트 핸들러 (UI 동기화) ---
    private void OnJobAdded(TranscriptionJobDTO dto)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
            Jobs.Add(new JobViewModel(dto));
            RefreshOverallProgress();
        });
    }

    private void OnJobRemoved(TranscriptionJobDTO dto)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
            var vm = Jobs.FirstOrDefault(j => j.Id == dto.Id);
            if (vm != null) Jobs.Remove(vm);
            RefreshOverallProgress();
        });
    }

    private void AddLog(string log)
    {
        while (Logs.Count >= 100)
        {
            Logs.RemoveAt(0);
        }
        Logs.Add(log);
    }

    private void RefreshOverallProgress(TranscriptionJobDTO? runningDto = null, JobViewModel? runningVm = null)
    {
        int totalJobs = Jobs.Count;
        if (totalJobs == 0)
        {
            Status = "대기열이 비어 있습니다.";
            TotalProgress = 0;
            Progress = 0;
            TotalProgressText = "";
            CurrentJobProgressText = "";
            EstimatedTimeRemaining = "";
            m_queueStartTime = null;
            return;
        }

        int completedCount = Jobs.Count(j => j.IsCompleted);
        int failedCount = Jobs.Count(j => j.IsFailed);
        int cancelledCount = Jobs.Count(j => j.IsCancelled);
        int processedCount = completedCount + failedCount + cancelledCount;
        double currentJobProgUrl = runningVm != null && runningVm.IsRunning && runningDto != null ? runningDto.Progress : 0;

        TotalProgress = ((processedCount * 100.0) + currentJobProgUrl) / totalJobs;
        TotalProgressText = $"완료: {completedCount}건, 실패: {failedCount}건, 취소: {cancelledCount}건 / 전체: {totalJobs}건 ({TotalProgress:F1}%)";

        if (runningDto != null && runningVm != null && runningDto.Status == JobStatus.Running)
        {
            Status = $"처리 중: {runningVm.FileName}";
            Progress = runningDto.Progress;
            CurrentJobProgressText = $"현재 파일: {Progress:F1}%";

            if (m_queueStartTime.HasValue)
            {
                var elapsed = DateTime.Now - m_queueStartTime.Value;
                // 현재까지 처리 완료된 파일의 소수점 반영 (예: 1.5개 완료)
                double currentItemsProcessed = processedCount + (currentJobProgUrl / 100.0);

                if (currentItemsProcessed > 0 && elapsed.TotalSeconds > 10) // 10초 이상 지나야 안정된 예측
                {
                    double avgSecondsPerItem = elapsed.TotalSeconds / currentItemsProcessed;
                    double remainingItems = totalJobs - currentItemsProcessed;
                    double remainingTotalSec = remainingItems * avgSecondsPerItem;

                    double remainingCurrentItemSec = avgSecondsPerItem * (1.0 - (currentJobProgUrl / 100.0));

                    EstimatedTimeRemaining = $"시간: (전체 예상 {TimeSpan.FromSeconds(Math.Max(0, remainingTotalSec)):hh\\:mm\\:ss}) / (현재 예상 {TimeSpan.FromSeconds(Math.Max(0, remainingCurrentItemSec)):hh\\:mm\\:ss})";
                }
                else
                {
                    EstimatedTimeRemaining = "남은 시간 계산 중...";
                }
            }
        }
        else if (processedCount == totalJobs)
        {
            if (failedCount > 0 || cancelledCount > 0)
            {
                Status = $"작업 완료 (성공: {completedCount}건, 실패: {failedCount}건, 취소: {cancelledCount}건)";
            }
            else
            {
                Status = "모든 작업 완료!";
            }
            Progress = 100;
            TotalProgress = 100;
            CurrentJobProgressText = "100%";
            EstimatedTimeRemaining = "완료";
            m_queueStartTime = null;
        }
    }

    private void OnJobUpdated(TranscriptionJobDTO dto)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
            var vm = Jobs.FirstOrDefault(j => j.Id == dto.Id);
            if (vm != null)
            {
                vm.UpdateFromDto();
                RefreshOverallProgress(dto, vm);
            }
        });
    }

    private void OnSegmentDetected(TranscriptionJobDTO job, TranscriptionResultDTO result)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
            // [00:12] 안녕하세요 형식으로 출력
            string timestamp = result.Start.ToString(@"hh\:mm\:ss");
            AddLog($"[{timestamp}] {result.Text}");
        });
    }

    private void OnProcessingStateChanged(bool isRunning)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
            IsProcessing = isRunning;
            if (isRunning)
            {
                if (!m_queueStartTime.HasValue) m_queueStartTime = DateTime.Now;
                AddLog("[시스템] 대기열 작업을 진행합니다.");
            }
            else
            {
                AddLog("[시스템] 대기열 작업이 일시정지되었습니다.");
            }
        });
    }

    // --- 커맨드 ---

    // View(MainWindow.axaml.cs) 쪽에서 파일 선택 시 직접 이 메서드 호출
    public void AddFileToQueue(string path)
    {
        if (!IsSupportedMediaPath(path))
        {
            Status = $"지원하지 않는 미디어 형식입니다: {Path.GetFileName(path)}";
            return;
        }

        // 중복 체크 및 큐 등록
        if (m_queueManager.Jobs.Any(j => j.VideoPath == path)) return;

        var jobDto = new TranscriptionJobDTO
        {
            VideoPath = path,
            OutputPath = OutputPath,
            ModelName = SelectedModel,
            StatusMessage = "대기열 추가됨"
        };
        m_queueManager.AddJob(jobDto);
    }

    internal void AddCompletedRecordingToQueue(string path)
    {
        if (!m_queueManager.Jobs.Any(job => job.Status == JobStatus.Running))
        {
            m_queueManager.PauseProcessing();
        }
        AddFileToQueue(path);
    }

    [RelayCommand]
    private async Task RefreshAudioInputDevicesAsync()
    {
        if (m_microphoneRecorder is null)
        {
            Status = "[녹음 오류] FFmpeg를 찾을 수 없습니다.";
            return;
        }

        try
        {
            int? selectedIndex = SelectedAudioInputDevice?.Index;
            IReadOnlyList<AudioInputDevice> devices = await m_microphoneRecorder.ListAudioInputDevicesAsync();
            AudioInputDevices.Clear();
            foreach (AudioInputDevice device in devices) AudioInputDevices.Add(device);
            SelectedAudioInputDevice = AudioInputDevices.FirstOrDefault(device => device.Index == selectedIndex) ?? AudioInputDevices[0];
            Status = "오디오 입력 장치를 새로고침했습니다.";
        }
        catch (Exception exception)
        {
            Status = $"[녹음 오류] {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await StopRecordingAsync();
            return;
        }

        if (m_microphoneRecorder is null)
        {
            Status = "[녹음 오류] FFmpeg를 찾을 수 없습니다.";
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPath) || !Directory.Exists(OutputPath))
        {
            Status = "[녹음 오류] 녹음 저장 폴더를 먼저 선택해주세요.";
            return;
        }

        try
        {
            await m_microphoneRecorder.StartAsync(SelectedAudioInputDevice ?? AudioInputDevices[0], OutputPath);
            m_recordingStartedAt = DateTime.Now;
            RecordingElapsed = "00:00:00";
            IsRecording = true;
            StartRecordingTimer();
        }
        catch (Exception exception)
        {
            Status = $"[녹음 오류] {exception.Message}";
        }
    }

    internal async Task StopRecordingForShutdownAsync()
    {
        if (IsRecording) await StopRecordingAsync();
        m_recordingTimer?.Stop();
        if (m_microphoneRecorder is not null)
        {
            await m_microphoneRecorder.DisposeAsync();
            m_microphoneRecorder = null;
        }
    }

    private async Task StopRecordingAsync()
    {
        if (m_microphoneRecorder is null) return;

        m_recordingTimer?.Stop();
        try
        {
            string path = await m_microphoneRecorder.StopAsync();
            IsRecording = false;
            AddCompletedRecordingToQueue(path);
            Status = $"녹음 저장 및 대기열 추가: {Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            IsRecording = false;
            Status = $"[녹음 오류] {exception.Message}";
        }
    }

    private void StartRecordingTimer()
    {
        m_recordingTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        m_recordingTimer.Tick -= OnRecordingTimerTick;
        m_recordingTimer.Tick += OnRecordingTimerTick;
        m_recordingTimer.Start();
    }

    private async void OnRecordingTimerTick(object? sender, EventArgs eventArgs)
    {
        if (!IsRecording) return;
        if (m_microphoneRecorder is not { IsRecording: true })
        {
            await StopRecordingAsync();
            return;
        }

        RecordingElapsed = (DateTime.Now - m_recordingStartedAt).ToString(@"hh\:mm\:ss");
    }

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingButtonText));
    }

    [RelayCommand]
    private void RemoveJob(JobViewModel? job)
    {
        if (job != null)
        {
            m_queueManager.RemoveJob(job.Id);
        }
    }

    [RelayCommand]
    private void ClearJobs()
    {
        m_queueManager.ClearJobs();
        // 큐 매니저의 JobRemoved 이벤트를 통해 비-Running 작업들만 UI Jobs에서 동기화되어 제거됨
    }

    [RelayCommand]
    private void StopTranscription()
    {
        // 진행을 잠시 멈춤 (Pause)
        m_queueManager.PauseProcessing();
    }

    [RelayCommand]
    private void StartTranscription()
    {
        // 이미 앱 시작부터 돌아가고 있으므로 단순히 Resume 해줌
        if (Jobs.Count == 0)
        {
            Status = "대기열이 비어 있습니다. 영상을 추가해주세요.";
            return;
        }

        string? modelPath = ResolveModelPath(SelectedModel);
        if (modelPath == null || !File.Exists(modelPath))
        {
            Status = "오류: 모델 파일을 찾을 수 없습니다.";
            return;
        }

        m_queueManager.ResumeProcessing();
    }

    [RelayCommand]
    private async Task DownloadModelAsync(string? fileName)
    {
        var model = AvailableModels.FirstOrDefault(m => m.FileName == (fileName ?? SelectedModel));
        if (model == null) return;

        IsDownloading = true;
        DownloadProgress = 0;
        Status = $"{model.Name} 모델 다운로드 중...";

        try
        {
            string url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{model.FileName}";
            string destinationPath = Path.Combine(GetPrimaryModelsDirectory(), model.FileName);

            var progress = new Progress<double>(p => DownloadProgress = p);

            using var client = new HttpClient();
            await DownloadModelFileAsync(client, url, destinationPath, progress);

            Status = $"{model.Name} 다운로드 완료!";
            RefreshAvailableModels();
        }
        catch (Exception ex)
        {
            Status = $"다운로드 실패: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        var allLogs = string.Join(Environment.NewLine, Logs);
        await CopyToClipboardAsync(allLogs);
        Status = "전체 로그가 클립보드에 복사되었습니다.";
    }

    [RelayCommand]
    private async Task CopySelectedLogAsync()
    {
        if (string.IsNullOrEmpty(SelectedLog)) return;
        await CopyToClipboardAsync(SelectedLog);
        Status = "선택한 로그가 클립보드에 복사되었습니다.";
    }

    private async Task CopyToClipboardAsync(string text)
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var clipboard = desktop.MainWindow?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
    }
}

public partial class ModelInfo : ObservableObject
{
    [ObservableProperty]
    private string m_name = string.Empty;

    [ObservableProperty]
    private string m_fileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private bool m_isDownloaded = false;

    public string StatusText => IsDownloaded ? "[다운로드됨]" : "[미다운로드]";
    public string DisplayName => $"{Name} {StatusText}";
}
