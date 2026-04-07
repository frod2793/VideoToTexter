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
using Avalonia.Threading;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System.Text;

namespace VideoToText.Avalonia.ViewModels;

/// <summary>
/// [설명]: 메인 윈도우의 상태 관리 및 비즈니스 로직 연동을 담당하는 뷰모델입니다.
/// MVVM 패턴을 준수하며, 프로퍼티 주입을 통해 의존성을 관리합니다.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    #region 의존성 주입 (Property Injection)
    /// <summary>
    /// [설명]: 핵심 텍스트 변환 애플리케이션 서비스입니다.
    /// </summary>
    public VideoToTextApp? App { get; set; }

    /// <summary>
    /// [설명]: AI 모델 다운로드를 담당하는 서비스입니다.
    /// </summary>
    public ModelDownloader? Downloader { get; set; }
    #endregion

    #region 에디터 및 상태 필드
    [ObservableProperty]
    private string m_videoPath = string.Empty;

    [ObservableProperty]
    private string m_outputPath = string.Empty;

    [ObservableProperty]
    private string m_status = "준비됨";

    [ObservableProperty]
    private double m_progress = 0;

    [ObservableProperty]
    private bool m_isProcessing = false;

    [ObservableProperty]
    private string m_selectedModel = string.Empty;

    [ObservableProperty]
    private string m_estimatedTimeRemaining = string.Empty;

    [ObservableProperty]
    private bool m_isModelDownloading = false;

    private readonly Stopwatch m_stopwatch = new();

    public ObservableCollection<string> Logs { get; } = new();

    public ObservableCollection<string> AvailableModels { get; } = new();
    #endregion

    #region 데이터 모델 (DTO/Registry)
    private readonly Dictionary<string, string> m_modelRegistry = new()
    {
        { "ggml-tiny.bin", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin" },
        { "ggml-base.bin", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin" },
        { "ggml-small.bin", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin" },
        { "ggml-medium.bin", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin" },
        { "model_q4_1.gguf", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium-q5_0.bin" },
        { "ggml-large-v3-turbo.bin", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin" },
        { "ggml-large-v3.bin", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin" }
    };
    #endregion

    #region 초기화 로직
    public MainWindowViewModel()
    {
        // FFmpeg 초기화는 ServiceConfigurator에서 앱 시작 시 1회 수행
        RefreshAvailableModels();
    }

    /// <summary>
    /// [설명]: 로컬에 보관된 모델 파일 목록을 갱신합니다.
    /// </summary>
    private void RefreshAvailableModels()
    {
        AvailableModels.Clear();
        string localModelsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");

        if (!Directory.Exists(localModelsPath))
        {
            Directory.CreateDirectory(localModelsPath);
        }

        var localFiles = Directory.GetFiles(localModelsPath, "*.*")
            .Where(f => f.EndsWith(".gguf") || f.EndsWith(".bin"))
            .Select(Path.GetFileName)
            .ToHashSet()!;

        foreach (var modelName in m_modelRegistry.Keys.OrderBy(f => f))
        {
            string statusTag = localFiles.Contains(modelName) ? "[보관됨]" : "[다운로드 필요]";
            AvailableModels.Add($"{modelName} {statusTag}");
        }

        if (AvailableModels.Count > 0)
        {
            SelectedModel = AvailableModels[0];
        }
    }
    #endregion

    #region 공개 메서드 및 명령 (Commands)
    /// <summary>
    /// [설명]: 선택된 AI 모델을 인터넷에서 다운로드합니다.
    /// </summary>
    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (Downloader == null) return;

        string pureModelName = GetPureModelFileName(SelectedModel);
        string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", pureModelName);

        if (File.Exists(modelPath))
        {
            Status = "이미 파일이 존재합니다.";
            return;
        }

        if (!m_modelRegistry.TryGetValue(pureModelName, out string? downloadUrl))
        {
            Status = "다운로드 정보를 찾을 수 없습니다.";
            return;
        }

        try
        {
            IsModelDownloading = true;
            IsProcessing = true;
            Status = $"{pureModelName} 다운로드 시작...";
            AddLog($"[시스템] {pureModelName} 다운로드를 시작합니다 (URL: {downloadUrl})");

            await Downloader.DownloadFileAsync(downloadUrl, modelPath, (progress) =>
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Progress = progress * 100;
                    Status = $"{pureModelName} 다운로드 중.. ({Progress:F0}%)";
                });
            });

            Status = "다운로드 완료!";
            AddLog("[시스템] 모델 다운로드 성공.");
            RefreshAvailableModels();
        }
        catch (Exception ex)
        {
            Status = "다운로드 오류 발생";
            AddLog($"[오류] 다운로드 중 문제가 발생했습니다: {ex.Message}");
        }
        finally
        {
            IsModelDownloading = false;
            IsProcessing = false;
            Progress = 0;
        }
    }

    /// <summary>
    /// [설명]: 현재까지 쌓인 모든 로그 내용을 클립보드에 복사합니다.
    /// </summary>
    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        if (Logs.Count == 0) return;

        try
        {
            var sb = new StringBuilder();
            foreach (var log in Logs)
            {
                sb.AppendLine(log);
            }

            if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow?.Clipboard != null)
                {
                    await desktop.MainWindow.Clipboard.SetTextAsync(sb.ToString());
                    Status = "로그가 클립보드에 복사되었습니다.";
                }
            }
        }
        catch (Exception ex)
        {
            AddLog($"[오류] 로그 복사 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// [설명]: 비디오 파일을 텍스트로 변환하는 메인 프로세스를 시작합니다.
    /// </summary>
    [RelayCommand]
    private async Task StartTranscriptionAsync()
    {
        if (string.IsNullOrEmpty(VideoPath))
        {
            Status = "오류: 영상 파일을 선택해주세요.";
            return;
        }

        if (IsProcessing) return;

        IsProcessing = true;
        Status = "처리 중...";
        Logs.Clear();
        AddLog("[시스템] 변환 작업 시작...");

        try
        {
            string pureModelName = GetPureModelFileName(SelectedModel);
            string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", pureModelName);

            if (!File.Exists(modelPath))
            {
                Status = "오류: 모델 파일이 없습니다. [다운로드] 버튼을 눌러주세요.";
                return;
            }

            // 의존성 확인 루틴
            VideoToTextApp? activeApp = App;
            if (activeApp == null)
            {
                // [방어 코드]: 만약 주입되지 않았다면 로컬에서 생성 시도 (안정성 확보)
                var sttService = new WhisperTranscriptionService(modelPath);
                var audioExtractor = new FFmpegAudioExtractor();
                var exporters = new List<ISubtitleExportService> { new SrtSubtitleExporter(), new PlainTextExporter() };
                activeApp = new VideoToTextApp(audioExtractor, sttService, exporters);
                App = activeApp;
            }

            m_stopwatch.Reset();
            EstimatedTimeRemaining = "남은 시간 계산 중...";

            string? outDir = string.IsNullOrWhiteSpace(OutputPath)
                ? Path.GetDirectoryName(VideoPath)
                : OutputPath;

            if (string.IsNullOrEmpty(outDir)) outDir = AppDomain.CurrentDomain.BaseDirectory;

            if (activeApp != null)
            {
                await activeApp.RunAsync(
                    VideoPath,
                    outputDirectory: outDir,
                    onStatusUpdate: (msg) =>
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            Status = msg;
                            AddLog(msg);
                        });
                    },
                    onSegmentDetected: (result) =>
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            AddLog($"[결과] {result}");
                        });
                    },
                    onProgressChanged: (p) =>
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            Progress = p * 100;
                            UpdateETR(p);
                        });
                    }
                );
            }

            Status = "변환 완료!";
            EstimatedTimeRemaining = "완료";
            m_stopwatch.Stop();
            AddLog("[시스템] 변환 작업이 성공적으로 마무리되었습니다.");
        }
        catch (Exception ex)
        {
            Status = "오류 발생";
            AddLog($"[오류] {ex.Message}");
            Debug.WriteLine(ex.StackTrace);
        }
        finally
        {
            IsProcessing = false;
        }
    }
    #endregion

    #region 내부 로직 (Private)
    private string GetPureModelFileName(string selectedString)
    {
        return selectedString.Split(' ')[0];
    }

    private void AddLog(string message)
    {
        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (Logs.Count > 500) Logs.RemoveAt(0);
    }

    private void UpdateETR(double progress)
    {
        if (progress > 0.01)
        {
            if (!m_stopwatch.IsRunning) m_stopwatch.Start();

            var elapsed = m_stopwatch.Elapsed;
            var totalEstimate = TimeSpan.FromTicks((long)(elapsed.Ticks / progress));
            var remaining = totalEstimate - elapsed;

            EstimatedTimeRemaining = remaining.TotalHours >= 1 
                ? $"약 {remaining:hh\\:mm\\:ss} 남음" 
                : $"약 {remaining:mm\\:ss} 남음";
        }
    }
    #endregion
}
