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
    private VideoToTextApp? m_app;
    
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
    private double m_downloadProgress = 0;

    [ObservableProperty]
    private bool m_isDownloading = false;

    private DateTime m_transcriptionStartTime;

    public ObservableCollection<string> Logs { get; } = new();

    public ObservableCollection<ModelInfo> AvailableModels { get; } = new();

    public MainWindowViewModel()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string embeddedFfmpeg = Path.Combine(appDir, "ffmpeg");
        string homebrewFfmpeg = "/opt/homebrew/bin/ffmpeg";

        if (File.Exists(embeddedFfmpeg))
        {
            FFmpeg.SetExecutablesPath(appDir);
        }
        else if (File.Exists(homebrewFfmpeg))
        {
            FFmpeg.SetExecutablesPath(Path.GetDirectoryName(homebrewFfmpeg));
        }

        InitializeModels();
        RefreshAvailableModels();
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
        if (global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var clipboard = desktop.MainWindow?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(allLogs);
                Status = "로그가 클립보드에 복사되었습니다.";
            }
        }
    }

    [RelayCommand]
    private async Task SelectFileAsync()
    {
        Status = "파일 선택 대기 중...";
    }

    [RelayCommand]
    private async Task StartTranscriptionAsync()
    {
        if (string.IsNullOrEmpty(VideoPath))
        {
            Status = "오류: 영상 파일을 선택해주세요.";
            return;
        }

        IsProcessing = true;
        Status = "처리 중...";
        EstimatedTimeRemaining = "계산 중...";
        m_transcriptionStartTime = DateTime.Now;
        Logs.Clear();
        Logs.Add("[시스템] 변환 작업 시작...");
        
        try
        {
            Environment.SetEnvironmentVariable("GGML_METAL", "0");

            string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", SelectedModel);
            
            if (!File.Exists(modelPath))
            {
                string unityModelsPath = "/Users/woodenshield/Desktop/UNITY/Project/VideoToText/VTOTUnity/Assets/StreamingAssets/Models";
                modelPath = Path.Combine(unityModelsPath, SelectedModel);
            }

            if (!File.Exists(modelPath))
            {
                Status = "오류: 모델 파일을 찾을 수 없습니다.";
                Logs.Add($"[오류] 모델을 찾을 수 없는 경로: {modelPath}");
                return;
            }

            var sttService = new WhisperTranscriptionService(modelPath);
            var audioExtractor = new FFmpegAudioExtractor();
            var exporters = new List<ISubtitleExportService> 
            { 
                new SrtSubtitleExporter(), 
                new PlainTextExporter() 
            };

            m_app = new VideoToTextApp(audioExtractor, sttService, exporters);

            
            string outDir = string.IsNullOrWhiteSpace(OutputPath) 
                ? Path.GetDirectoryName(VideoPath) 
                : OutputPath;

            if (string.IsNullOrEmpty(outDir)) outDir = AppDomain.CurrentDomain.BaseDirectory;

            await m_app.RunAsync(
                VideoPath, 
                outputDirectory: outDir,
                onStatusUpdate: (msg) => {
                    global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                        Status = msg;
                        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
                        if (Logs.Count > 100) Logs.RemoveAt(0);
                    });
                },
                onSegmentDetected: (result) => {
                    global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                        Logs.Add($"[결과] {result}");
                    });
                },
                onProgressChanged: (p) => {
                    global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                        Progress = p * 100;
                        
                        if (p > 0.01)
                        {
                            var elapsed = DateTime.Now - m_transcriptionStartTime;
                            var totalEstimated = TimeSpan.FromTicks((long)(elapsed.Ticks / p));
                            var remaining = totalEstimated - elapsed;
                            
                            if (remaining.TotalSeconds > 0)
                            {
                                EstimatedTimeRemaining = $"남은 시간: 약 {(int)remaining.TotalMinutes}분 {(int)remaining.TotalSeconds % 60}초";
                            }
                        }
                    });
                }
            );

            Status = "변환 완료!";
            EstimatedTimeRemaining = "완료";
            Logs.Add("[시스템] 변환 작업이 성공적으로 마무리되었습니다.");
        }
        catch (Exception ex)
        {
            Status = "오류 발생";
            Logs.Add($"[오류] {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            IsProcessing = false;
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
