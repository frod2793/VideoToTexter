using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using VideoToText.Application;
using VideoToText.Avalonia.ViewModels;
using VideoToText.Core;
using VideoToText.Infrastructure;
using Xabe.FFmpeg;

namespace VideoToText.Avalonia;

/// <summary>
/// 애플리케이션의 서비스 등록과 초기화를 담당하는 정적 설정 클래스.
/// FFmpeg 경로 설정의 유일한 진입점이며, 모든 서비스의 생명주기를 관리한다.
/// </summary>
public static class ServiceConfigurator
{
    private static IServiceProvider? m_serviceProvider;

    public static IServiceProvider ServiceProvider =>
        m_serviceProvider ?? throw new InvalidOperationException("Services not configured.");

    /// <summary>
    /// 서비스 컨테이너를 구성하고 초기화한다.
    /// 이 메서드는 앱 시작 시 단 한 번만 호출되어야 한다.
    /// </summary>
    public static void ConfigureServices()
    {
        // ── FFmpeg 경로 설정 (유일한 초기화 지점) ──
        SetupFFmpegPath();

        var services = new ServiceCollection();

        // ── 인프라 계층 ──
        services.AddTransient<IAudioExtractor, FFmpegAudioExtractor>();
        services.AddSingleton<ModelDownloader>();

        // ── 자막 내보내기 ──
        services.AddTransient<ISubtitleExportService, SrtSubtitleExporter>();
        services.AddTransient<ISubtitleExportService, PlainTextExporter>();

        // ── ViewModel ──
        services.AddTransient<MainWindowViewModel>(sp =>
        {
            var vm = new MainWindowViewModel();
            vm.Downloader = sp.GetRequiredService<ModelDownloader>();
            return vm;
        });

        m_serviceProvider = services.BuildServiceProvider();
    }

    public static T GetService<T>() where T : class
    {
        return ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// 실행 파일 기준 ffmpeg 폴더를 탐색하여 Xabe.FFmpeg에 경로를 등록한다.
    /// </summary>
    private static void SetupFFmpegPath()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string ffmpegDir = Path.Combine(baseDir, "ffmpeg");

            if (Directory.Exists(ffmpegDir))
            {
                // ffmpeg.exe가 직접 있는 경우
                if (File.Exists(Path.Combine(ffmpegDir, "ffmpeg.exe")))
                {
                    FFmpeg.SetExecutablesPath(ffmpegDir);
                    Console.WriteLine($"[FFmpeg] 경로 설정 완료: {ffmpegDir}");
                    return;
                }

                // 하위 폴더 검색 (bin 등)
                var found = Directory.GetFiles(ffmpegDir, "ffmpeg.exe", SearchOption.AllDirectories);
                if (found.Length > 0)
                {
                    string dir = Path.GetDirectoryName(found[0])!;
                    FFmpeg.SetExecutablesPath(dir);
                    Console.WriteLine($"[FFmpeg] 경로 설정 완료: {dir}");
                    return;
                }
            }

            Console.WriteLine($"[FFmpeg] 경고: ffmpeg.exe를 찾을 수 없습니다. 경로: {ffmpegDir}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FFmpeg] 초기화 오류: {ex.Message}");
        }
    }
}
