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

namespace VideoToText.Avalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IJobQueueManager m_queueManager;
    private WhisperTranscriptionService? m_cachedSttService;
    private VideoToTextApp? m_cachedApp;
    private string? m_lastModelPath;
    
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

    public ObservableCollection<string> Logs { get; } = new();

    public ObservableCollection<ModelInfo> AvailableModels { get; } = new();

    // UI의 리스트 뷰가 바인딩되는 컬렉션 (ReadOnly 같은 느낌으로 사용)
    public ObservableCollection<JobViewModel> Jobs { get; } = new();

    public MainWindowViewModel()
    {
        InitializeFfmpegPath();
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

    private VideoToTextApp GetOrCreateVideoToTextApp()
    {
        string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", SelectedModel);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("모델 파일이 없습니다: " + SelectedModel);

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

    private void InitializeFfmpegPath()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string embeddedFfmpeg = Path.Combine(appDir, "ffmpeg");
        string embeddedFfprobe = Path.Combine(appDir, "ffprobe");

        if (File.Exists(embeddedFfmpeg) && File.Exists(embeddedFfprobe))
        {
            FFmpeg.SetExecutablesPath(appDir);
            return;
        }

        string homebrewPath = "/opt/homebrew/bin";
        if (File.Exists(Path.Combine(homebrewPath, "ffmpeg")))
        {
            FFmpeg.SetExecutablesPath(homebrewPath);
            return;
        }

        string localBinPath = "/usr/local/bin";
        if (File.Exists(Path.Combine(localBinPath, "ffmpeg")))
        {
            FFmpeg.SetExecutablesPath(localBinPath);
            return;
        }
    }

    private void InitializeModels()
    {
        string[] modelNames = { "tiny", "base", "small", "medium", "large-v1", "large-v2", "large-v3", "large-v3-turbo" };
        foreach (var name in modelNames)
        {
            AvailableModels.Add(new ModelInfo { Name = name, FileName = $"ggml-{name}.bin" });
        }
    }

    private void RefreshAvailableModels()
    {
        string localModelsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
        foreach (var model in AvailableModels)
        {
            string fullPath = Path.Combine(localModelsPath, model.FileName);
            model.IsDownloaded = File.Exists(fullPath);
        }

        var firstDownloaded = AvailableModels.FirstOrDefault(m => m.IsDownloaded);
        if (firstDownloaded != null)
        {
            SelectedModel = firstDownloaded.FileName;
            Status = "준비됨";
        }
        else
        {
            SelectedModel = AvailableModels[0].FileName;
            Status = "사용 가능한 모델이 없습니다. [모델 다운로드] 버튼을 눌러주세요.";
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

        int completedCount = Jobs.Count(j => j.IsCompleted || j.IsFailed);
        double currentJobProgUrl = runningVm != null && runningVm.IsRunning && runningDto != null ? runningDto.Progress : 0;

        TotalProgress = ((completedCount * 100.0) + currentJobProgUrl) / totalJobs;
        TotalProgressText = $"전체: {completedCount}/{totalJobs} 완료 ({TotalProgress:F1}%)";

        if (runningDto != null && runningVm != null && runningDto.Status == JobStatus.Running)
        {
            Status = $"처리 중: {runningVm.FileName}";
            Progress = runningDto.Progress;
            CurrentJobProgressText = $"현재 파일: {Progress:F1}%";

            if (m_queueStartTime.HasValue)
            {
                var elapsed = DateTime.Now - m_queueStartTime.Value;
                // 현재까지 처리 완료된 파일의 소수점 반영 (예: 1.5개 완료)
                double currentItemsProcessed = completedCount + (currentJobProgUrl / 100.0);
                
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
        else if (completedCount == totalJobs)
        {
            Status = "모든 작업 완료!";
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
            Logs.Add($"[{timestamp}] {result.Text}");
        });
    }

    private void OnProcessingStateChanged(bool isRunning)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
            IsProcessing = isRunning;
            if (isRunning) 
            {
                if (!m_queueStartTime.HasValue) m_queueStartTime = DateTime.Now;
                Logs.Add("[시스템] 대기열 작업을 진행합니다.");
            }
            else 
            {
                Logs.Add("[시스템] 대기열 작업이 일시정지되었습니다.");
            }
        });
    }

    // --- 커맨드 ---

    // View(MainWindow.axaml.cs) 쪽에서 파일 선택 시 직접 이 메서드 호출
    public void AddFileToQueue(string path)
    {
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
        Jobs.Clear(); // 큐 삭제 연산은 이벤트가 개별로 안 오므로 수동 초기화
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

        string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", SelectedModel);
        if (!File.Exists(modelPath))
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
            string modelsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
            
            if (!Directory.Exists(modelsPath)) Directory.CreateDirectory(modelsPath);
            string destinationPath = Path.Combine(modelsPath, model.FileName);

            using var client = new HttpClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            var totalRead = 0L;
            var readCount = 0;

            while ((readCount = await contentStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
            {
                await fileStream.WriteAsync(buffer, 0, readCount);
                totalRead += readCount;

                if (totalBytes != -1)
                {
                    DownloadProgress = (double)totalRead / totalBytes * 100;
                }
            }

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
