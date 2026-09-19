using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VideoToText.Avalonia.Services;

namespace VideoToText.Checks;

internal static class MicrophoneRecorderChecks
{
    internal static async Task RunAsync()
    {
        const string stderr = "[AVFoundation indev @ 0x1] AVFoundation video devices:\n" +
                              "[AVFoundation indev @ 0x1] [0] FaceTime HD Camera\n" +
                              "[AVFoundation indev @ 0x1] AVFoundation audio devices:\n" +
                              "[AVFoundation indev @ 0x1] [0] MacBook Microphone\n" +
                              "[AVFoundation indev @ 0x1] [1] USB Audio";

        IReadOnlyList<AudioInputDevice> devices = FFmpegMicrophoneRecorder.ParseAudioDevices(stderr);
        Assert(devices.Count == 3 && devices[0].Index == null, "기본 마이크 항목 누락");
        Assert(devices[1].Index == 0 && devices[1].Name == "MacBook Microphone", "장치 0 파싱 실패");
        Assert(devices[2].Index == 1 && devices[2].Name == "USB Audio", "장치 1 파싱 실패");

        string defaultArgs = string.Join(' ', FFmpegMicrophoneRecorder.BuildRecordingArguments(devices[0], "/tmp/a.part"));
        string selectedArgs = string.Join(' ', FFmpegMicrophoneRecorder.BuildRecordingArguments(devices[2], "/tmp/b.part"));
        Assert(defaultArgs.Contains("-i :default") && defaultArgs.Contains("-c:a aac") && defaultArgs.Contains("-b:a 128k") && defaultArgs.Contains("-f ipod"), "기본 장치 인자 오류");
        Assert(selectedArgs.Contains("-audio_device_index 1") && selectedArgs.Contains("-i :none"), "선택 장치 인자 오류");

        string directory = Path.Combine(Path.GetTempPath(), $"VideoTexterChecks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string existingPath = Path.Combine(directory, "녹음_20260919_120000.m4a");
            File.WriteAllText(existingPath, "existing");
            string uniquePath = FFmpegMicrophoneRecorder.GetUniqueRecordingPath(directory, new DateTime(2026, 9, 19, 12, 0, 0));
            Assert(File.Exists(existingPath), "기존 녹음 파일이 보존되지 않았습니다.");
            Assert(Path.GetFileName(uniquePath) == "녹음_20260919_120000_2.m4a", "고유 녹음 파일명이 _2를 선택하지 않았습니다.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await CheckCancelledDeviceListingStopsProcessAsync();
    }

    private static async Task CheckCancelledDeviceListingStopsProcessAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;

        string directory = Path.Combine(Path.GetTempPath(), $"VideoTexterChecks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string pidPath = Path.Combine(directory, "ffmpeg.pid");
        string scriptPath = Path.Combine(directory, "fake-ffmpeg");
        File.WriteAllText(scriptPath, $"#!/bin/sh\necho $$ > \"{pidPath}\"\nsleep 30\n");
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            await using var recorder = new FFmpegMicrophoneRecorder(scriptPath);
            using var cancellation = new CancellationTokenSource();
            Task listing = recorder.ListAudioInputDevicesAsync(cancellation.Token);
            for (int i = 0; i < 20 && !File.Exists(pidPath); i++) await Task.Delay(50);
            Assert(File.Exists(pidPath), "취소 검사 프로세스가 시작되지 않았습니다.");

            cancellation.Cancel();
            try
            {
                await listing;
            }
            catch (OperationCanceledException)
            {
            }

            int pid = int.Parse(File.ReadAllText(pidPath));
            try
            {
                using Process process = Process.GetProcessById(pid);
                Assert(process.WaitForExit(1000), "취소된 장치 조회 프로세스가 남았습니다.");
            }
            catch (ArgumentException)
            {
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new AssertException(message);
    }
}
