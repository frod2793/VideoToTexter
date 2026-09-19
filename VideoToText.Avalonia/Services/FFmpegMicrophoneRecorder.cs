using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoToText.Avalonia.Services;

public sealed class AudioInputDevice
{
    public int? Index { get; }
    public string Name { get; }

    public AudioInputDevice(int? index, string name)
    {
        Index = index;
        Name = name;
    }

    public override string ToString() => Name;
}

public sealed class FFmpegMicrophoneRecorder : IAsyncDisposable
{
    private static readonly Regex s_devicePattern = new(@"\[(\d+)\]\s+(.+)$", RegexOptions.Compiled);

    private readonly string m_ffmpegPath;
    private Process? m_process;
    private Process? m_deviceListProcess;
    private Task<string>? m_stderrTask;
    private string? m_tempPath;
    private string? m_finalPath;

    public FFmpegMicrophoneRecorder(string ffmpegPath)
    {
        if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("FFmpeg 실행 파일을 찾을 수 없습니다.", ffmpegPath);
        m_ffmpegPath = ffmpegPath;
    }

    public bool IsRecording => m_process is { HasExited: false };

    internal static IReadOnlyList<AudioInputDevice> ParseAudioDevices(string stderr)
    {
        var devices = new List<AudioInputDevice> { new(null, "시스템 기본 마이크") };
        bool inAudioSection = false;

        foreach (string line in stderr.Split('\n'))
        {
            if (line.Contains("AVFoundation audio devices:", StringComparison.Ordinal))
            {
                inAudioSection = true;
                continue;
            }

            if (inAudioSection && line.Contains("AVFoundation", StringComparison.Ordinal) && line.Contains("devices:", StringComparison.Ordinal)) break;
            if (!inAudioSection) continue;

            Match match = s_devicePattern.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                devices.Add(new AudioInputDevice(index, match.Groups[2].Value));
            }
        }

        return devices;
    }

    internal static IReadOnlyList<string> BuildRecordingArguments(AudioInputDevice device, string tempPath)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "warning", "-f", "avfoundation" };
        if (device.Index is int index)
        {
            arguments.Add("-audio_device_index");
            arguments.Add(index.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("-i");
        arguments.Add(device.Index is null ? ":default" : ":none");
        arguments.Add("-vn");
        arguments.Add("-c:a");
        arguments.Add("aac");
        arguments.Add("-b:a");
        arguments.Add("128k");
        arguments.Add("-f");
        arguments.Add("ipod");
        arguments.Add(tempPath);
        return arguments;
    }

    internal static string GetUniqueRecordingPath(string directory, DateTime now)
    {
        string stem = $"녹음_{now:yyyyMMdd_HHmmss}";
        for (int suffix = 1; ; suffix++)
        {
            string path = Path.Combine(directory, suffix == 1 ? $"{stem}.m4a" : $"{stem}_{suffix}.m4a");
            if (!File.Exists(path)) return path;
        }
    }

    public async Task<IReadOnlyList<AudioInputDevice>> ListAudioInputDevicesAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(m_ffmpegPath) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("avfoundation");
        startInfo.ArgumentList.Add("-list_devices");
        startInfo.ArgumentList.Add("true");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("");

        Process? process = null;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg 장치 조회를 시작하지 못했습니다.");
            m_deviceListProcess = process;
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string stderr = await stderrTask;
            IReadOnlyList<AudioInputDevice> devices = ParseAudioDevices(stderr);
            if (devices.Count == 1) throw new InvalidOperationException($"오디오 입력 장치를 찾지 못했습니다. {stderr}");
            return devices;
        }
        catch
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }
        finally
        {
            process?.Dispose();
            if (ReferenceEquals(m_deviceListProcess, process)) m_deviceListProcess = null;
        }
    }

    public Task StartAsync(AudioInputDevice device, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(outputDirectory)) throw new DirectoryNotFoundException($"녹음 저장 폴더를 찾을 수 없습니다: {outputDirectory}");
        if (m_process is not null) throw new InvalidOperationException("이전 녹음 프로세스가 정리되지 않았습니다.");

        string finalPath = GetUniqueRecordingPath(outputDirectory, DateTime.Now);
        string tempPath = finalPath + ".part";
        if (File.Exists(tempPath)) throw new IOException($"임시 녹음 파일이 이미 있습니다: {tempPath}");

        var startInfo = new ProcessStartInfo(m_ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in BuildRecordingArguments(device, tempPath)) startInfo.ArgumentList.Add(argument);

        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg 녹음을 시작하지 못했습니다.");
        m_process = process;
        m_stderrTask = process.StandardError.ReadToEndAsync();
        m_tempPath = tempPath;
        m_finalPath = finalPath;
        return Task.CompletedTask;
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        Process process = m_process ?? throw new InvalidOperationException("진행 중인 녹음이 없습니다.");
        string tempPath = m_tempPath ?? throw new InvalidOperationException("임시 녹음 경로가 없습니다.");
        string finalPath = m_finalPath ?? throw new InvalidOperationException("최종 녹음 경로가 없습니다.");

        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q");
                await process.StandardInput.FlushAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    throw new TimeoutException("FFmpeg 녹음 종료가 5초 안에 완료되지 않았습니다.");
                }
            }

            string stderr = m_stderrTask is null ? string.Empty : await m_stderrTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg 녹음이 실패했습니다 (exit {process.ExitCode}): {stderr}");
            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0) throw new IOException($"녹음 임시 파일이 없거나 비어 있습니다: {tempPath}");
            File.Move(tempPath, finalPath);
            return finalPath;
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            finally
            {
                DeleteTemporaryFile(tempPath);
            }
            throw;
        }
        finally
        {
            process.Dispose();
            m_process = null;
            m_stderrTask = null;
            m_tempPath = null;
            m_finalPath = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (m_deviceListProcess is { HasExited: false } deviceListProcess)
        {
            deviceListProcess.Kill(entireProcessTree: true);
            await deviceListProcess.WaitForExitAsync();
        }
        if (m_process is not null) await StopAsync();
    }

    private static void DeleteTemporaryFile(string tempPath)
    {
        if (!File.Exists(tempPath)) return;
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception exception)
        {
            throw new IOException($"임시 녹음 파일 삭제 실패: {tempPath}", exception);
        }
    }
}
