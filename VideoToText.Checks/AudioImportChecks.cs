using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using VideoToText.Avalonia.ViewModels;
using VideoToText.Infrastructure;
using Xabe.FFmpeg;

namespace VideoToText.Checks;

internal static class AudioImportChecks
{
    internal static Task CheckSupportedMediaExtensions()
    {
        string[] accepted = { "voice.mp3", "VOICE.M4A", "memo.wav", "video.mp4", "video.mkv", "video.mov", "video.avi" };
        foreach (string path in accepted)
        {
            Check(MainWindowViewModel.IsSupportedMediaPath(path), $"지원 파일 거부: {path}");
        }

        Check(!MainWindowViewModel.IsSupportedMediaPath("notes.txt"), "TXT가 미디어로 허용됨");
        Check(!MainWindowViewModel.IsSupportedMediaPath("no-extension"), "확장자 없는 파일이 허용됨");
        return Task.CompletedTask;
    }

    internal static async Task CheckMp3AndM4aExtractionAsync(string ffmpegPath)
    {
        string root = Path.Combine(Path.GetTempPath(), "VideoToTexterChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FFmpeg.SetExecutablesPath(Path.GetDirectoryName(ffmpegPath)!);
            foreach (string extension in new[] { ".mp3", ".m4a" })
            {
                string input = Path.Combine(root, "voice" + extension);
                string output = input + ".wav";
                await GenerateAudioAsync(ffmpegPath, input);
                bool extracted = await new FFmpegAudioExtractor().ExtractAudioAsync(input, output);
                Check(extracted && new FileInfo(output).Length > 44, $"{extension} WAV 추출 실패");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task GenerateAudioAsync(string ffmpegPath, string outputPath)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("sine=frequency=440:duration=1");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg 실행 실패");
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg 오디오 생성 실패: {error}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
