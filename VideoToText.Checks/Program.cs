using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using VideoToText.Application;
using VideoToText.Avalonia.ViewModels;
using VideoToText.Core;
using VideoToText.Infrastructure;

namespace VideoToText.Checks
{
    public class AssertException : Exception
    {
        public AssertException(string message) : base(message) { }
    }

    class FakeAudioExtractor : IAudioExtractor
    {
        private readonly bool m_result;
        public string? LastCreatedPath { get; private set; }

        public FakeAudioExtractor(bool result)
        {
            m_result = result;
        }

        public Task<bool> ExtractAudioAsync(string videoFilePath, string outputAudioPath, TimeSpan? startTime = null, TimeSpan? duration = null)
        {
            LastCreatedPath = outputAudioPath;
            if (m_result)
            {
                string? dir = Path.GetDirectoryName(outputAudioPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(outputAudioPath, "RIFF fake wav header");
            }
            return Task.FromResult(m_result);
        }
    }

    class BlockingAudioExtractor : IAudioExtractor
    {
        private readonly Task m_blockTask;
        public BlockingAudioExtractor(Task blockTask)
        {
            m_blockTask = blockTask;
        }

        public async Task<bool> ExtractAudioAsync(string videoFilePath, string outputAudioPath, TimeSpan? startTime = null, TimeSpan? duration = null)
        {
            string? dir = Path.GetDirectoryName(outputAudioPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(outputAudioPath, "RIFF fake wav header");
            await m_blockTask;
            return true;
        }
    }

    class FakeTranscriptionService : ITranscriptionService
    {
        public async IAsyncEnumerable<TranscriptionResultDTO> TranscribeAsync(
            string audioFilePath,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new TranscriptionResultDTO
            {
                Start = TimeSpan.FromSeconds(0),
                End = TimeSpan.FromSeconds(1),
                Text = "테스트 자막입니다."
            };
        }
    }

    class TrackingExporter : ISubtitleExportService
    {
        public bool ExportCalled { get; private set; }
        private readonly ISubtitleExportService m_inner;

        public TrackingExporter(ISubtitleExportService inner)
        {
            m_inner = inner;
        }

        public Task ExportAsync(IEnumerable<TranscriptionResultDTO> results, string outputPath)
        {
            ExportCalled = true;
            return m_inner.ExportAsync(results, outputPath);
        }
    }

    class FailingExporter : ISubtitleExportService
    {
        public Task ExportAsync(IEnumerable<TranscriptionResultDTO> results, string outputPath)
        {
            throw new IOException("디스크 I/O 오류 시뮬레이션");
        }
    }

    internal class Program
    {
        private static bool s_ffmpegReady;
        private static string? s_ffmpegInitMessage;
        private static string? s_ffmpegExecutable;

        private static void SetupFFmpeg()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new[]
            {
                appDir,
                "/opt/homebrew/bin",
                "/usr/local/bin"
            };

            foreach (var dir in candidates)
            {
                if (File.Exists(Path.Combine(dir, "ffmpeg")) && File.Exists(Path.Combine(dir, "ffprobe")))
                {
                    FFmpeg.SetExecutablesPath(dir);
                    s_ffmpegExecutable = Path.Combine(dir, "ffmpeg");
                    s_ffmpegReady = true;
                    s_ffmpegInitMessage = $"FFmpeg 바이너리 발견: {dir}";
                    return;
                }
            }
            s_ffmpegReady = false;
            s_ffmpegExecutable = null;
            s_ffmpegInitMessage = "시스템 표준 경로(/opt/homebrew/bin, /usr/local/bin, 앱 실행 경로)에서 FFmpeg/FFprobe를 찾을 수 없습니다.";
        }

        private static string CreateMinimalValidWav(string dir, string fileName = "sample.wav", int durationSeconds = 2)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string filePath = Path.Combine(dir, fileName);

            int sampleRate = 16000;
            short bitsPerSample = 16;
            short channels = 1;
            int byteRate = sampleRate * channels * (bitsPerSample / 8);
            short blockAlign = (short)(channels * (bitsPerSample / 8));
            int dataSize = byteRate * durationSeconds;
            int chunkSize = 36 + dataSize;

            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(chunkSize);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));

                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1); // PCM
                bw.Write(channels);
                bw.Write(sampleRate);
                bw.Write(byteRate);
                bw.Write(blockAlign);
                bw.Write(bitsPerSample);

                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataSize);
                byte[] silence = new byte[dataSize];
                bw.Write(silence);
            }
            return filePath;
        }

        private static VideoToTextApp CreateMockApp(Task? blockTask = null)
        {
            IAudioExtractor extractor = blockTask != null ? new BlockingAudioExtractor(blockTask) : new FakeAudioExtractor(true);
            var transcriber = new FakeTranscriptionService();
            var srt = new SrtSubtitleExporter();
            var txt = new PlainTextExporter();
            return new VideoToTextApp(extractor, transcriber, new ISubtitleExportService[] { srt, txt });
        }

        private static VideoToTextApp CreateFailingApp()
        {
            IAudioExtractor extractor = new FakeAudioExtractor(false);
            var transcriber = new FakeTranscriptionService();
            var srt = new SrtSubtitleExporter();
            var txt = new PlainTextExporter();
            return new VideoToTextApp(extractor, transcriber, new ISubtitleExportService[] { srt, txt });
        }

        private static async Task<int> Main(string[] args)
        {
            bool requireFFmpeg = args.Contains("--require-ffmpeg");

            Console.WriteLine("========================================");
            Console.WriteLine("   VideoToText.Checks 검증 실행기");
            Console.WriteLine("========================================");
            SetupFFmpeg();

            if (!s_ffmpegReady)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[환경 미준비] {s_ffmpegInitMessage}");
                Console.WriteLine("[안내] FFmpeg 의존 검사는 SKIP 처리되며, P0 완료로 판정되지 않습니다.");
                Console.ResetColor();
            }

            int passCount = 0;
            int failCount = 0;
            int skipCount = 0;

            // [P0 1단계] 1~4: Exporter 단위 검증 (FFmpeg 불필요)
            await ExecuteCheck("1. SrtSubtitleExporter 저장 실패 예외 전파", CheckSrtExporterPropagatesFailure, () => passCount++, () => failCount++);
            await ExecuteCheck("2. PlainTextExporter 저장 실패 예외 전파", CheckPlainTextExporterPropagatesFailure, () => passCount++, () => failCount++);
            await ExecuteCheck("3. Exporter 기존 파일 덮어쓰기 거부(충돌 방지)", CheckExporterRefusesOverwrite, () => passCount++, () => failCount++);
            await ExecuteCheck("4. Exporter 정상 저장", CheckExporterSuccessfulExport, () => passCount++, () => failCount++);

            // [P0 1단계] 5~8: VideoToTextApp 파이프라인 검증 (FFmpeg 의존)
            await ExecuteCheckWithFFmpeg("5. 오디오 추출 실패(false) 시 VideoToTextApp 즉시 예외 및 export 미호출", CheckAudioExtractFalseFailsImmediately, () => passCount++, () => failCount++, () => skipCount++);
            await ExecuteCheckWithFFmpeg("6. 임시 WAV가 원본 폴더가 아닌 OS 임시 폴더에 생성되고 finally 정리", CheckTempWavInOsTempAndCleanedUp, () => passCount++, () => failCount++, () => skipCount++);
            await ExecuteCheckWithFFmpeg("7. 기존 결과 파일(srt/txt) 존재 시 VideoToTextApp 실패 및 원본 보존", CheckExistingOutputFilePreventsOverwrite, () => passCount++, () => failCount++, () => skipCount++);
            await ExecuteCheckWithFFmpeg("8. 부분 저장 실패 시 진단 메시지에 남은 파일 경로 포함 및 파일 보존", CheckPartialSaveFailureReportsRemainingPath, () => passCount++, () => failCount++, () => skipCount++);

            // [P0 2단계] 9~15: JobQueueManager 상태 및 UI 일치 검증
            await ExecuteCheck("9. 큐 초기 등록 시 자동 실행되지 않고 Pending 유지", CheckInitialQueueDoesNotAutoRun, () => passCount++, () => failCount++);
            await ExecuteCheck("10. 실행 중(Running) 작업은 RemoveJob으로 제거 거부되고, ClearJobs는 Running 작업 보존", CheckRunningJobCannotBeRemovedAndClearPreservesRunning, () => passCount++, () => failCount++);
            await ExecuteCheck("11. Pause 시 실행 중 작업 완료까지 IsProcessing 유지, 다음 작업 미시작, Resume 후 1회 시작", CheckPauseDoesNotStopRunningAndResumeExecutesNextOnce, () => passCount++, () => failCount++);
            await ExecuteCheck("12. Jobs 프로퍼티는 lock 안에서 스냅샷 반환", CheckJobsReturnsSnapshot, () => passCount++, () => failCount++);
            await ExecuteCheck("13. 실패(Failed)·취소(Cancelled)·성공(Completed) 상태 분리", CheckJobStatusSeparation, () => passCount++, () => failCount++);
            await ExecuteCheck("14. 작업 등록 시 확정된 ModelName이 앱 제공자에 정확히 전달", CheckJobModelNamePassedToAppProvider, () => passCount++, () => failCount++);
            await ExecuteCheck("15. Pause 대기 중 불필요한 중복 상태 이벤트 방지 및 Resume 시 Pending 작업 안전한 깨움", CheckPauseNoRedundantEventsAndResumeWakesPending, () => passCount++, () => failCount++);
            // [P1 3단계] 16: 청크 경계 중첩 세그먼트 병합 및 발화 보존 검증
            await ExecuteCheck("16. 청크 경계 중첩 병합(경계 동일 병합, 시간 분리 동일 보존, 분할 불일치 보존, 시간 순서/구간 유효성)", CheckMergeOverlappingSegments, () => passCount++, () => failCount++);
            // [P1 4단계] 17: 모델 다운로드 완결성과 저장 위치 검증
            await ExecuteCheck("17. 모델 다운로드 완결성(정상 응답·스트림 중단·길이 불일치 시 .part 정리 및 기존 모델 보존)과 새 경로 우선·레거시 호환", CheckModelDownloadAndStorageResolution, () => passCount++, () => failCount++);
            await ExecuteCheck("18. 오디오 가져오기 지원 확장자", AudioImportChecks.CheckSupportedMediaExtensions, () => passCount++, () => failCount++);
            await ExecuteCheckWithFFmpeg("19. MP3/M4A WAV 추출", () => AudioImportChecks.CheckMp3AndM4aExtractionAsync(s_ffmpegExecutable!), () => passCount++, () => failCount++, () => skipCount++);
            await ExecuteCheck("20. 마이크 녹음 인자 및 파일 충돌 방지", MicrophoneRecorderChecks.RunAsync, () => passCount++, () => failCount++);
            await ExecuteCheck("21. 확정 녹음 파일 대기열 검증", MicrophoneUiChecks.CheckRecordedFileQueueValidation, () => passCount++, () => failCount++);
            await ExecuteCheck("22. macOS 마이크 권한 설명", MicrophonePermissionChecks.RunAsync, () => passCount++, () => failCount++);

            Console.WriteLine("========================================");
            Console.WriteLine($"검증 결과: 성공 {passCount}건, 실패 {failCount}건, 건너뜀(SKIP) {skipCount}건");
            if (skipCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[주의] FFmpeg 의존 P0 항목이 건너뛰어졌으므로 P0 전체 검증 완료로 인정되지 않습니다.");
                Console.ResetColor();
            }
            Console.WriteLine("========================================");

            if (failCount > 0) return 1;
            if (requireFFmpeg && skipCount > 0) return 2;
            return 0;
        }

        private static async Task ExecuteCheck(string name, Func<Task> test, Action onPass, Action onFail)
        {
            Console.Write($"[검사] {name} ... ");
            try
            {
                await test();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
                Console.ResetColor();
                onPass();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL -> {ex.Message}");
                Console.ResetColor();
                onFail();
            }
        }

        private static async Task ExecuteCheckWithFFmpeg(string name, Func<Task> test, Action onPass, Action onFail, Action onSkip)
        {
            Console.Write($"[검사] {name} ... ");
            if (!s_ffmpegReady)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("SKIP (환경 미준비: 시스템 FFmpeg 부재)");
                Console.ResetColor();
                onSkip();
                return;
            }

            try
            {
                await test();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
                Console.ResetColor();
                onPass();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL -> {ex.Message}");
                Console.ResetColor();
                onFail();
            }
        }

        // --- P0 1단계 검증 메서드 ---

        private static async Task CheckSrtExporterPropagatesFailure()
        {
            var exporter = new SrtSubtitleExporter();
            var sampleResults = new[]
            {
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(0), End = TimeSpan.FromSeconds(2), Text = "안녕" }
            };

            string tempDir = Path.Combine(Path.GetTempPath(), "vtt_test_dir_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                bool threwExpected = false;
                try
                {
                    await exporter.ExportAsync(sampleResults, tempDir);
                }
                catch (Exception ex) when (!(ex is AssertException))
                {
                    threwExpected = true;
                }

                if (!threwExpected)
                {
                    throw new AssertException("저장 실패가 발생했으나 SrtSubtitleExporter가 예외를 상위로 전파하지 않고 삼켰습니다.");
                }
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        private static async Task CheckPlainTextExporterPropagatesFailure()
        {
            var exporter = new PlainTextExporter();
            var sampleResults = new[]
            {
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(0), End = TimeSpan.FromSeconds(2), Text = "안녕" }
            };

            string tempDir = Path.Combine(Path.GetTempPath(), "vtt_test_dir_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                bool threwExpected = false;
                try
                {
                    await exporter.ExportAsync(sampleResults, tempDir);
                }
                catch (Exception ex) when (!(ex is AssertException))
                {
                    threwExpected = true;
                }

                if (!threwExpected)
                {
                    throw new AssertException("저장 실패가 발생했으나 PlainTextExporter가 예외를 상위로 전파하지 않고 삼켰습니다.");
                }
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        private static async Task CheckExporterRefusesOverwrite()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "기존 데이터 원본");
                var srtExporter = new SrtSubtitleExporter();
                var results = new[] { new TranscriptionResultDTO { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "새 데이터" } };

                bool threw = false;
                try
                {
                    await srtExporter.ExportAsync(results, tempFile);
                }
                catch (Exception ex) when (!(ex is AssertException))
                {
                    threw = true;
                }

                if (!threw)
                {
                    throw new AssertException("기존 파일이 존재하는데 덮어쓰기를 허용하여 예외를 발생시키지 않았습니다.");
                }

                string content = File.ReadAllText(tempFile);
                if (content != "기존 데이터 원본")
                {
                    throw new AssertException("기존 파일 내용이 덮어쓰여 훼손되었습니다.");
                }
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        private static async Task CheckExporterSuccessfulExport()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "vtt_success_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string srtPath = Path.Combine(tempDir, "test.srt");
                string txtPath = Path.Combine(tempDir, "test.txt");
                var results = new[]
                {
                    new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Text = "안녕하세요" }
                };

                await new SrtSubtitleExporter().ExportAsync(results, srtPath);
                await new PlainTextExporter().ExportAsync(results, txtPath);

                if (!File.Exists(srtPath) || !File.Exists(txtPath))
                {
                    throw new AssertException("출력 파일이 생성되지 않았습니다.");
                }

                string srtContent = File.ReadAllText(srtPath);
                string txtContent = File.ReadAllText(txtPath);

                if (!srtContent.Contains("00:00:01,000 --> 00:00:02,000") || !srtContent.Contains("안녕하세요"))
                {
                    throw new AssertException("SRT 형식이 규격과 맞지 않습니다.");
                }

                if (!txtContent.Contains("안녕하세요"))
                {
                    throw new AssertException("TXT 형식이 올바르지 않습니다.");
                }
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        private static async Task CheckAudioExtractFalseFailsImmediately()
        {
            if (!s_ffmpegReady) throw new InvalidOperationException("FFmpeg/FFprobe 바이너리를 찾을 수 없어 오디오 추출 실패 검증을 진행할 수 없습니다 (환경 미준비).");

            string workDir = Path.Combine(Path.GetTempPath(), "vtt_extract_fail_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            try
            {
                string testVideo = CreateMinimalValidWav(workDir, "test_input.wav", 2);
                var fakeExtractor = new FakeAudioExtractor(false);
                var fakeTranscriber = new FakeTranscriptionService();
                var srtTracker = new TrackingExporter(new SrtSubtitleExporter());
                var txtTracker = new TrackingExporter(new PlainTextExporter());

                var app = new VideoToTextApp(fakeExtractor, fakeTranscriber, new ISubtitleExportService[] { srtTracker, txtTracker });

                bool threw = false;
                try
                {
                    await app.RunAsync(testVideo, outputDirectory: workDir);
                }
                catch (Exception ex) when (!(ex is AssertException))
                {
                    threw = true;
                }

                if (!threw)
                {
                    throw new AssertException("오디오 추출이 false를 반환했으나 예외가 발생하지 않고 완료 처리되었습니다.");
                }

                if (srtTracker.ExportCalled || txtTracker.ExportCalled)
                {
                    throw new AssertException("추출 실패 시 export가 호출되지 않아야 하지만 호출되었습니다.");
                }
            }
            finally
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
        }

        private static async Task CheckTempWavInOsTempAndCleanedUp()
        {
            if (!s_ffmpegReady) throw new InvalidOperationException("FFmpeg/FFprobe 바이너리를 찾을 수 없어 임시 WAV 위치 검증을 진행할 수 없습니다 (환경 미준비).");

            string videoDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp_video_test_" + Guid.NewGuid().ToString("N"));
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp_out_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(videoDir);
            Directory.CreateDirectory(outDir);

            try
            {
                string testVideo = CreateMinimalValidWav(videoDir, "test_input.wav", 2);
                var fakeExtractor = new FakeAudioExtractor(true);
                var fakeTranscriber = new FakeTranscriptionService();
                var srtExporter = new SrtSubtitleExporter();
                var txtExporter = new PlainTextExporter();

                var app = new VideoToTextApp(fakeExtractor, fakeTranscriber, new ISubtitleExportService[] { srtExporter, txtExporter });

                await app.RunAsync(testVideo, outputDirectory: outDir);

                var tempWavsInVideoDir = Directory.GetFiles(videoDir, "*temp_part*.wav");
                if (tempWavsInVideoDir.Length > 0)
                {
                    throw new AssertException($"원본 영상 폴더에 임시 파일이 생성되었습니다: {string.Join(", ", tempWavsInVideoDir)}");
                }

                if (fakeExtractor.LastCreatedPath == null)
                {
                    throw new AssertException("오디오 추출기가 호출되지 않았습니다.");
                }

                string osTemp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!fakeExtractor.LastCreatedPath.StartsWith(osTemp, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AssertException($"임시 파일이 OS 임시 폴더에 생성되지 않고 다른 폴더({fakeExtractor.LastCreatedPath})에 생성되었습니다.");
                }

                if (File.Exists(fakeExtractor.LastCreatedPath))
                {
                    throw new AssertException($"작업 완료 후 임시 파일이 정리되지 않았습니다: {fakeExtractor.LastCreatedPath}");
                }
            }
            finally
            {
                if (Directory.Exists(videoDir)) Directory.Delete(videoDir, true);
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            }
        }

        private static async Task CheckExistingOutputFilePreventsOverwrite()
        {
            if (!s_ffmpegReady) throw new InvalidOperationException("FFmpeg/FFprobe 바이너리를 찾을 수 없어 기존 출력 보호 검증을 진행할 수 없습니다 (환경 미준비).");

            string workDir = Path.Combine(Path.GetTempPath(), "vtt_overwrite_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            try
            {
                string testVideo = CreateMinimalValidWav(workDir, "sample.wav", 2);
                string videoBaseName = Path.GetFileNameWithoutExtension(testVideo);

                string existingSrt = Path.Combine(workDir, videoBaseName + ".srt");
                File.WriteAllText(existingSrt, "기존 보존 자막 내용");

                var fakeExtractor = new FakeAudioExtractor(true);
                var fakeTranscriber = new FakeTranscriptionService();
                var app = new VideoToTextApp(fakeExtractor, fakeTranscriber, new ISubtitleExportService[] { new SrtSubtitleExporter(), new PlainTextExporter() });

                bool threw = false;
                try
                {
                    await app.RunAsync(testVideo, outputDirectory: workDir);
                }
                catch (IOException)
                {
                    threw = true;
                }
                catch (Exception ex) when (ex.Message.Contains("이미 존재"))
                {
                    threw = true;
                }

                if (!threw)
                {
                    throw new AssertException("기존 결과 파일이 존재하는데 예외를 던지지 않고 덮어썼습니다.");
                }

                if (File.ReadAllText(existingSrt) != "기존 보존 자막 내용")
                {
                    throw new AssertException("기존 결과 파일 내용이 훼손되었습니다.");
                }
            }
            finally
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
        }

        private static async Task CheckPartialSaveFailureReportsRemainingPath()
        {
            if (!s_ffmpegReady) throw new InvalidOperationException("FFmpeg/FFprobe 바이너리를 찾을 수 없어 부분 저장 검증을 진행할 수 없습니다 (환경 미준비).");

            string workDir = Path.Combine(Path.GetTempPath(), "vtt_partial_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            try
            {
                string testVideo = CreateMinimalValidWav(workDir, "partial_test.wav", 2);
                string videoBaseName = Path.GetFileNameWithoutExtension(testVideo);

                var fakeExtractor = new FakeAudioExtractor(true);
                var fakeTranscriber = new FakeTranscriptionService();
                var app = new VideoToTextApp(fakeExtractor, fakeTranscriber, new ISubtitleExportService[] { new SrtSubtitleExporter(), new FailingExporter() });

                string expectedSavedSrt = Path.Combine(workDir, videoBaseName + ".srt");

                bool threw = false;
                string? exceptionMessage = null;
                try
                {
                    await app.RunAsync(testVideo, outputDirectory: workDir);
                }
                catch (Exception ex) when (!(ex is AssertException))
                {
                    threw = true;
                    exceptionMessage = ex.Message;
                }

                if (!threw)
                {
                    throw new AssertException("두 번째 exporter 실패 시 RunAsync에서 예외가 발생하지 않았습니다.");
                }

                if (!File.Exists(expectedSavedSrt))
                {
                    throw new AssertException($"첫 번째 파일이 실제로 저장되지 않았습니다: {expectedSavedSrt}");
                }

                string srtContent = File.ReadAllText(expectedSavedSrt);
                if (string.IsNullOrWhiteSpace(srtContent))
                {
                    throw new AssertException($"첫 번째 파일이 비어 있습니다: {expectedSavedSrt}");
                }

                if (exceptionMessage == null || !exceptionMessage.Contains(expectedSavedSrt))
                {
                    throw new AssertException($"부분 실패 시 저장 완료된 잔여 파일 절대 경로({expectedSavedSrt})가 진단 메시지에 포함되지 않았습니다. 실제 메시지: {exceptionMessage}");
                }
            }
            finally
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
        }

        // --- P0 2단계 검증 메서드 ---

        private static async Task CheckInitialQueueDoesNotAutoRun()
        {
            string workDir = Path.Combine(Path.GetTempPath(), "vtt_q_autorun_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            try
            {
                string fakeVideo = CreateMinimalValidWav(workDir, "test.wav", 1);
                var executed = false;
                var manager = new JobQueueManager(() => {
                    executed = true;
                    return CreateMockApp();
                });

                using var cts = new CancellationTokenSource();
                _ = manager.StartBackgroundWorkerAsync(cts.Token);

                var job = new TranscriptionJobDTO
                {
                    VideoPath = fakeVideo,
                    ModelName = "tiny",
                    Status = JobStatus.Pending
                };
                manager.AddJob(job);

                // 워커 루프가 반응할 시간을 줌
                await Task.Delay(300);

                if (executed || job.Status != JobStatus.Pending)
                {
                    throw new AssertException($"큐 초기 등록 시 자동 실행되지 않아야 하지만 실행되었습니다 (Status={job.Status}, executed={executed})");
                }
            }
            finally
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
        }

        private static async Task CheckRunningJobCannotBeRemovedAndClearPreservesRunning()
        {
            string workDir = Path.Combine(Path.GetTempPath(), "vtt_q_running_preserve_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            try
            {
                string fakeVideo1 = CreateMinimalValidWav(workDir, "test1.wav", 1);
                string fakeVideo2 = CreateMinimalValidWav(workDir, "test2.wav", 1);

                var tcsBlock = new TaskCompletionSource<bool>();
                var manager = new JobQueueManager(() => CreateMockApp(tcsBlock.Task));

                using var cts = new CancellationTokenSource();
                manager.ResumeProcessing();
                _ = manager.StartBackgroundWorkerAsync(cts.Token);

                var job1 = new TranscriptionJobDTO { VideoPath = fakeVideo1, Status = JobStatus.Pending };
                var job2 = new TranscriptionJobDTO { VideoPath = fakeVideo2, Status = JobStatus.Pending };
                manager.AddJob(job1);
                manager.AddJob(job2);

                for (int i = 0; i < 30; i++)
                {
                    if (job1.Status == JobStatus.Running) break;
                    await Task.Delay(50);
                }
                if (job1.Status != JobStatus.Running) throw new AssertException("job1이 Running 상태가 되지 않았습니다.");

                // 1. RemoveJob으로 Running 작업 제거 시도 -> 무시되어야 함
                manager.RemoveJob(job1.Id);
                if (!manager.Jobs.Any(j => j.Id == job1.Id))
                {
                    throw new AssertException("Running 상태인 작업이 RemoveJob으로 제거되었습니다.");
                }

                // 2. ClearJobs 호출 -> Running인 job1은 보존되고 job2만 제거되어야 함
                manager.ClearJobs();
                if (!manager.Jobs.Any(j => j.Id == job1.Id))
                {
                    throw new AssertException("ClearJobs에 의해 Running 상태인 작업이 보존되지 않고 삭제되었습니다.");
                }
                if (manager.Jobs.Any(j => j.Id == job2.Id))
                {
                    throw new AssertException("ClearJobs 후 Pending 상태인 job2가 제거되지 않았습니다.");
                }

                tcsBlock.SetResult(true);
            }
            finally
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
        }

        private static async Task CheckPauseDoesNotStopRunningAndResumeExecutesNextOnce()
        {
            string workDir = Path.Combine(Path.GetTempPath(), "vtt_q_pause_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            try
            {
                string fakeVideo1 = CreateMinimalValidWav(workDir, "test1.wav", 1);
                string fakeVideo2 = CreateMinimalValidWav(workDir, "test2.wav", 1);

                var tcs1 = new TaskCompletionSource<bool>();
                var tcs2 = new TaskCompletionSource<bool>();
                int runCount = 0;

                var manager = new JobQueueManager(() => {
                    runCount++;
                    return runCount == 1 ? CreateMockApp(tcs1.Task) : CreateMockApp(tcs2.Task);
                });

                using var cts = new CancellationTokenSource();
                manager.ResumeProcessing();
                _ = manager.StartBackgroundWorkerAsync(cts.Token);

                var job1 = new TranscriptionJobDTO { VideoPath = fakeVideo1, Status = JobStatus.Pending };
                var job2 = new TranscriptionJobDTO { VideoPath = fakeVideo2, Status = JobStatus.Pending };
                manager.AddJob(job1);
                manager.AddJob(job2);

                for (int i = 0; i < 30; i++)
                {
                    if (job1.Status == JobStatus.Running) break;
                    await Task.Delay(50);
                }
                if (job1.Status != JobStatus.Running) throw new AssertException("job1이 Running 상태가 되지 않았습니다.");

                bool stateChangedToFalseWhileRunning = false;
                manager.ProcessingStateChanged += (isRunning) => {
                    if (!isRunning && job1.Status == JobStatus.Running)
                    {
                        stateChangedToFalseWhileRunning = true;
                    }
                };

                // 작업 1 실행 중 Pause 요청
                manager.PauseProcessing();

                if (stateChangedToFalseWhileRunning)
                {
                    throw new AssertException("작업 실행 중 Pause 호출 즉시 ProcessingStateChanged(false)가 발생했습니다.");
                }

                // 작업 1 완료 허용
                tcs1.SetResult(true);
                for (int i = 0; i < 30; i++)
                {
                    if (job1.Status == JobStatus.Completed) break;
                    await Task.Delay(50);
                }

                await Task.Delay(300);

                // 작업 2는 실행되지 않고 Pending 상태여야 함
                if (job2.Status != JobStatus.Pending)
                {
                    throw new AssertException($"Pause 상태에서 다음 작업(job2)이 시작되었습니다 (Status={job2.Status})");
                }

                // Resume 호출 후 작업 2가 1회 실행되는지 확인
                manager.ResumeProcessing();
                for (int i = 0; i < 30; i++)
                {
                    if (job2.Status == JobStatus.Running) break;
                    await Task.Delay(50);
                }
                if (job2.Status != JobStatus.Running)
                {
                    throw new AssertException("Resume 후 작업 2가 시작되지 않았습니다.");
                }

                tcs2.SetResult(true);
                for (int i = 0; i < 30; i++)
                {
                    if (job2.Status == JobStatus.Completed) break;
                    await Task.Delay(50);
                }

                if (runCount != 2)
                {
                    throw new AssertException($"작업 실행 횟수가 기대치(2)와 다릅니다: {runCount}");
                }
            }
            finally
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
        }

        private static Task CheckJobsReturnsSnapshot()
        {
            var manager = new JobQueueManager(() => CreateMockApp());
            var job = new TranscriptionJobDTO { VideoPath = "test.mp4", Status = JobStatus.Pending };
            manager.AddJob(job);

            var snapshot1 = manager.Jobs;
            int countBefore = snapshot1.Count;

            // 원본 큐에 새 작업 추가
            manager.AddJob(new TranscriptionJobDTO { VideoPath = "extra.mp4", Status = JobStatus.Pending });

            // 스냅샷이어야 하므로 이전에 조회한 snapshot1의 요소 개수는 변하지 않아야 함
            if (snapshot1.Count != countBefore)
            {
                throw new AssertException("Jobs 프로퍼티가 복사본(스냅샷)이 아닌 내부 컬렉션 래퍼를 반환하여 이후 추가된 작업이 기존 스냅샷에 노출되었습니다.");
            }
            return Task.CompletedTask;
        }

        private static async Task CheckJobStatusSeparation()
        {
            string workDir = Path.Combine(Path.GetTempPath(), "vtt_q_status_sep_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            try
            {
                string fakeVideo = CreateMinimalValidWav(workDir, "test.wav", 1);

                // 1. 성공 작업
                var successManager = new JobQueueManager(() => CreateMockApp());
                using var cts1 = new CancellationTokenSource();
                successManager.ResumeProcessing();
                _ = successManager.StartBackgroundWorkerAsync(cts1.Token);

                var successJob = new TranscriptionJobDTO { VideoPath = fakeVideo, Status = JobStatus.Pending };
                successManager.AddJob(successJob);
                for (int i = 0; i < 30; i++)
                {
                    if (successJob.Status == JobStatus.Completed) break;
                    await Task.Delay(50);
                }
                if (successJob.Status != JobStatus.Completed) throw new AssertException($"성공 작업이 Completed가 아닙니다 (Status={successJob.Status})");

                // 2. 실패 작업
                var failManager = new JobQueueManager(() => CreateFailingApp());
                using var cts2 = new CancellationTokenSource();
                failManager.ResumeProcessing();
                _ = failManager.StartBackgroundWorkerAsync(cts2.Token);

                var failJob = new TranscriptionJobDTO { VideoPath = fakeVideo, Status = JobStatus.Pending };
                failManager.AddJob(failJob);
                for (int i = 0; i < 30; i++)
                {
                    if (failJob.Status == JobStatus.Failed) break;
                    await Task.Delay(50);
                }
                if (failJob.Status != JobStatus.Failed) throw new AssertException($"실패 작업이 Failed가 아닙니다 (Status={failJob.Status})");

                // 상태 분리 확인
                if (successJob.Status == failJob.Status)
                {
                    throw new AssertException("성공 작업과 실패 작업의 상태가 분리되지 않았습니다.");
                }
            }
            finally
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
        }

        private static async Task CheckJobModelNamePassedToAppProvider()
        {
            string workDir = Path.Combine(Path.GetTempPath(), "vtt_q_model_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            try
            {
                string fakeVideo = CreateMinimalValidWav(workDir, "model_test.wav", 1);
                string? capturedModel = null;
                var manager = new JobQueueManager((job) => {
                    capturedModel = job.ModelName;
                    return CreateMockApp();
                });

                using var cts = new CancellationTokenSource();
                manager.ResumeProcessing();
                _ = manager.StartBackgroundWorkerAsync(cts.Token);

                var jobDto = new TranscriptionJobDTO
                {
                    VideoPath = fakeVideo,
                    ModelName = "ggml-medium.bin",
                    Status = JobStatus.Pending
                };
                manager.AddJob(jobDto);

                for (int i = 0; i < 30; i++)
                {
                    if (jobDto.Status == JobStatus.Completed) break;
                    await Task.Delay(50);
                }

                if (capturedModel != "ggml-medium.bin")
                {
                    throw new AssertException($"Job의 ModelName('ggml-medium.bin')이 앱 제공자에 전달되지 않았습니다 (실제 전달된 값: {capturedModel})");
                }
            }
            finally
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
        }

        private static async Task CheckPauseNoRedundantEventsAndResumeWakesPending()
        {
            string workDir = Path.Combine(Path.GetTempPath(), "vtt_q_events_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            try
            {
                string fakeVideo = CreateMinimalValidWav(workDir, "test.wav", 1);
                var tcs = new TaskCompletionSource<bool>();
                var manager = new JobQueueManager(() => CreateMockApp(tcs.Task));

                int trueEventCount = 0;
                int falseEventCount = 0;
                manager.ProcessingStateChanged += (isRunning) =>
                {
                    if (isRunning) trueEventCount++;
                    else falseEventCount++;
                };

                using var cts = new CancellationTokenSource();
                // 1. 초기 큐(paused 상태) 시작
                _ = manager.StartBackgroundWorkerAsync(cts.Token);

                // 2. 일시정지 상태에서 작업 추가
                var job = new TranscriptionJobDTO { VideoPath = fakeVideo, Status = JobStatus.Pending };
                manager.AddJob(job);

                // 잠시 대기: paused 상태에서 반복적인 이벤트가 발생하지 않아야 함
                await Task.Delay(250);

                if (trueEventCount > 0 || falseEventCount > 0)
                {
                    throw new AssertException($"paused 대기 상태에서 불필요한 ProcessingStateChanged 이벤트가 발생했습니다 (true: {trueEventCount}, false: {falseEventCount})");
                }
                if (job.Status != JobStatus.Pending)
                {
                    throw new AssertException($"paused 대기 상태에서 작업이 자동 시작되었습니다 (Status: {job.Status})");
                }

                // 3. Resume 호출 -> m_signal.WaitAsync(Timeout.Infinite)에서 깨어나 작업이 실행되어야 함
                manager.ResumeProcessing();

                for (int i = 0; i < 30; i++)
                {
                    if (job.Status == JobStatus.Running) break;
                    await Task.Delay(50);
                }

                if (job.Status != JobStatus.Running)
                {
                    throw new AssertException("Resume 호출 후 대기 중이던 Pending 작업이 깨어나 실행되지 않았습니다.");
                }

                if (trueEventCount != 1)
                {
                    throw new AssertException($"작업 시작 시 ProcessingStateChanged(true)가 1회가 아닌 {trueEventCount}회 발생했습니다.");
                }

                // 4. 작업 완료 허용
                tcs.SetResult(true);

                for (int i = 0; i < 30; i++)
                {
                    if (job.Status == JobStatus.Completed) break;
                    await Task.Delay(50);
                }

                // 작업 완료 후 잠시 대기하여 대기 상태로 전환 확인
                await Task.Delay(100);

                if (falseEventCount != 1)
                {
                    throw new AssertException($"작업 완료 후 대기 전환 시 ProcessingStateChanged(false)가 1회가 아닌 {falseEventCount}회 발생했습니다.");
                }
            }
            finally
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
        }

        private static Task CheckMergeOverlappingSegments()
        {
            // 1. 경계 동일 (Boundary Identical Overlap): 인접 청크 경계에서 시간 구간이 겹치고 공백 정규화 후 텍스트가 같은 경우 -> 1개로 병합
            var boundaryOverlap = new List<TranscriptionResultDTO>
            {
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(1195), End = TimeSpan.FromSeconds(1202), Text = "안녕하세요 반갑습니다" },
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(1197), End = TimeSpan.FromSeconds(1205), Text = "  안녕하세요   반갑습니다  " }
            };

            var mergedBoundary = VideoToTextApp.MergeOverlappingSegments(boundaryOverlap);
            if (mergedBoundary.Count != 1)
            {
                throw new AssertException($"경계 동일 중첩 세그먼트가 1개로 병합되지 않았습니다 (결과 개수: {mergedBoundary.Count})");
            }
            if (mergedBoundary[0].Start != TimeSpan.FromSeconds(1195) || mergedBoundary[0].End != TimeSpan.FromSeconds(1205))
            {
                throw new AssertException($"경계 병합 시간 구간이 일치하지 않습니다: {mergedBoundary[0].Start} ~ {mergedBoundary[0].End} (기대치: 1195s ~ 1205s)");
            }

            // 2. 시간 분리 동일 (Time-separated Identical Text): 같은 문장이지만 시간상 분리된 경우 -> 모두 보존 (자연 반복 발화)
            var timeSeparated = new List<TranscriptionResultDTO>
            {
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(60), End = TimeSpan.FromSeconds(65), Text = "네 맞습니다" },
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(300), End = TimeSpan.FromSeconds(305), Text = "네 맞습니다" }
            };

            var mergedTimeSeparated = VideoToTextApp.MergeOverlappingSegments(timeSeparated);
            if (mergedTimeSeparated.Count != 2)
            {
                throw new AssertException($"시간상 분리된 동일 문장이 보존되지 않고 삭제되었습니다 (결과 개수: {mergedTimeSeparated.Count})");
            }

            // 3. 분할 불일치 보존 (Segmentation Difference Preservation): 경계에서 음성 분할이 달라 텍스트가 다른 경우 -> 병합하지 않고 원본 보존
            var splitMismatch = new List<TranscriptionResultDTO>
            {
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(1195), End = TimeSpan.FromSeconds(1205), Text = "오늘 날씨가 매우 좋습니다" },
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(1195), End = TimeSpan.FromSeconds(1200), Text = "오늘 날씨가" },
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(1200), End = TimeSpan.FromSeconds(1205), Text = "매우 좋습니다" }
            };

            var mergedSplitMismatch = VideoToTextApp.MergeOverlappingSegments(splitMismatch);
            if (mergedSplitMismatch.Count != 3)
            {
                throw new AssertException($"분할 차이 세그먼트가 보존되지 않았습니다 (결과 개수: {mergedSplitMismatch.Count})");
            }

            // 4. 복합 데이터에서 시작<=종료 및 시작 시간 오름차순 보장
            var complexInput = new List<TranscriptionResultDTO>
            {
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(500), End = TimeSpan.FromSeconds(490), Text = "역전된 시간" }, // 비정상 시작>종료
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(300), End = TimeSpan.FromSeconds(310), Text = "중간 발화" },
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(15), Text = "초반 발화" },
                new TranscriptionResultDTO { Start = TimeSpan.FromSeconds(305), End = TimeSpan.FromSeconds(312), Text = " 중간 발화 " } // 300~310과 중첩 병합 대상
            };

            var mergedComplex = VideoToTextApp.MergeOverlappingSegments(complexInput);
            if (mergedComplex.Count != 3)
            {
                throw new AssertException($"복합 입력 병합 결과 개수가 예상과 다릅니다 (결과 개수: {mergedComplex.Count}, 기대치: 3)");
            }

            for (int i = 0; i < mergedComplex.Count; i++)
            {
                var s = mergedComplex[i];
                if (s.Start > s.End)
                {
                    throw new AssertException($"세그먼트 시작 시간이 종료 시간보다 큽니다: Start={s.Start}, End={s.End}");
                }
                if (i > 0 && mergedComplex[i - 1].Start > s.Start)
                {
                    throw new AssertException($"세그먼트 시작 시간 순서가 오름차순이 아닙니다: [{i-1}]={mergedComplex[i-1].Start} > [{i}]={s.Start}");
                }
            }

            return Task.CompletedTask;
        }

        private static async Task CheckModelDownloadAndStorageResolution()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "vtt_model_check_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. 경로 우선순위 및 레거시 fallback 검증
                string primaryDir = Path.Combine(tempDir, "Primary");
                string legacyDir = Path.Combine(tempDir, "Legacy");
                Directory.CreateDirectory(primaryDir);
                Directory.CreateDirectory(legacyDir);

                // 1-1. 레거시에만 파일이 존재할 때 -> 레거시 경로 fallback
                string legacyFile = Path.Combine(legacyDir, "ggml-base.bin");
                File.WriteAllText(legacyFile, "legacy_base_data");
                string? resolvedLegacy = MainWindowViewModel.ResolveModelPath("ggml-base.bin", primaryDir, legacyDir);
                if (resolvedLegacy != legacyFile)
                {
                    throw new AssertException($"레거시 폴더의 모델 파일이 fallback으로 해석되지 않았습니다. 결과: {resolvedLegacy}, 기대치: {legacyFile}");
                }

                // 1-2. 새 경로와 레거시 경로 양쪽에 모두 존재할 때 -> 새 경로(Primary) 우선
                string primaryFile = Path.Combine(primaryDir, "ggml-base.bin");
                File.WriteAllText(primaryFile, "primary_base_data");
                string? resolvedPrimary = MainWindowViewModel.ResolveModelPath("ggml-base.bin", primaryDir, legacyDir);
                if (resolvedPrimary != primaryFile)
                {
                    throw new AssertException($"새 경로(Primary) 우선순위가 지켜지지 않았습니다. 결과: {resolvedPrimary}, 기대치: {primaryFile}");
                }

                // 1-3. 어디에도 존재하지 않을 때 -> null 반환
                string? resolvedNull = MainWindowViewModel.ResolveModelPath("ggml-nonexistent.bin", primaryDir, legacyDir);
                if (resolvedNull != null)
                {
                    throw new AssertException($"존재하지 않는 모델에 대해 null이 아닌 경로가 반환되었습니다: {resolvedNull}");
                }

                // 2. Local HttpListener 기반 다운로드 완결성 검증 (외부 네트워크 미사용)
                var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                tcp.Start();
                int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                tcp.Stop();

                string prefix = $"http://127.0.0.1:{port}/";
                using var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();

                var serverTask = Task.Run(async () =>
                {
                    // Request 1: /normal (Content-Length 100, 100바이트 정상 전송)
                    var ctx1 = await listener.GetContextAsync();
                    byte[] data1 = Encoding.UTF8.GetBytes(new string('A', 100));
                    ctx1.Response.ContentLength64 = data1.Length;
                    await ctx1.Response.OutputStream.WriteAsync(data1, 0, data1.Length);
                    ctx1.Response.Close();

                    // Request 2: /abort (Content-Length 100, 40바이트만 전송 후 스트림 중단)
                    var ctx2 = await listener.GetContextAsync();
                    byte[] data2 = Encoding.UTF8.GetBytes(new string('B', 40));
                    ctx2.Response.ContentLength64 = 100;
                    await ctx2.Response.OutputStream.WriteAsync(data2, 0, data2.Length);
                    ctx2.Response.Abort();

                    // Request 3: /mismatch (Content-Length 100, 80바이트 전송 후 정상 close)
                    var ctx3 = await listener.GetContextAsync();
                    byte[] data3 = Encoding.UTF8.GetBytes(new string('C', 80));
                    ctx3.Response.ContentLength64 = 100;
                    await ctx3.Response.OutputStream.WriteAsync(data3, 0, data3.Length);
                    ctx3.Response.Close();
                });

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

                // 2-1. 정상 다운로드 검증
                string destNormal = Path.Combine(primaryDir, "ggml-normal.bin");
                await MainWindowViewModel.DownloadModelFileAsync(httpClient, prefix + "normal", destNormal);

                if (!File.Exists(destNormal))
                {
                    throw new AssertException("정상 다운로드 완료 후 최종 모델 파일이 생성되지 않았습니다.");
                }
                if (new FileInfo(destNormal).Length != 100)
                {
                    throw new AssertException($"최종 모델 파일 크기가 일치하지 않습니다: {new FileInfo(destNormal).Length}");
                }
                if (File.Exists(destNormal + ".part"))
                {
                    throw new AssertException("다운로드 완료 후 .part 임시 파일이 남아 있습니다.");
                }

                // 2-2. 스트림 중단 시 .part 정리 및 기존 모델 보존 검증
                string destAbort = Path.Combine(primaryDir, "ggml-abort.bin");
                File.WriteAllText(destAbort, "pre_existing_valid_model");

                bool abortFailed = false;
                try
                {
                    await MainWindowViewModel.DownloadModelFileAsync(httpClient, prefix + "abort", destAbort);
                }
                catch (Exception)
                {
                    abortFailed = true;
                }

                if (!abortFailed)
                {
                    throw new AssertException("스트림 중단이 발생했으나 예외가 발생하지 않았습니다.");
                }
                if (File.Exists(destAbort + ".part"))
                {
                    throw new AssertException("스트림 중단 실패 후 .part 임시 파일이 정리되지 않았습니다.");
                }
                if (!File.Exists(destAbort) || File.ReadAllText(destAbort) != "pre_existing_valid_model")
                {
                    throw new AssertException("스트림 중단 실패 시 기존 정상 모델 파일이 훼손되거나 보존되지 않았습니다.");
                }

                // 2-3. Content-Length 불일치 시 .part 정리 및 기존 모델 보존 검증
                string destMismatch = Path.Combine(primaryDir, "ggml-mismatch.bin");
                File.WriteAllText(destMismatch, "pre_existing_mismatch_model");

                bool mismatchFailed = false;
                try
                {
                    await MainWindowViewModel.DownloadModelFileAsync(httpClient, prefix + "mismatch", destMismatch);
                }
                catch (IOException)
                {
                    mismatchFailed = true;
                }
                catch (Exception)
                {
                    mismatchFailed = true;
                }

                if (!mismatchFailed)
                {
                    throw new AssertException("Content-Length 불일치가 발생했으나 예외가 발생하지 않았습니다.");
                }
                if (File.Exists(destMismatch + ".part"))
                {
                    throw new AssertException("Content-Length 불일치 실패 후 .part 임시 파일이 정리되지 않았습니다.");
                }
                if (!File.Exists(destMismatch) || File.ReadAllText(destMismatch) != "pre_existing_mismatch_model")
                {
                    throw new AssertException("Content-Length 불일치 실패 시 기존 정상 모델 파일이 훼손되거나 보존되지 않았습니다.");
                }

                await serverTask;
                listener.Stop();

                // 3. RefreshAvailableModels SelectedModel 유지 및 무효 시 자동선택 규칙 검증
                var vm = new MainWindowViewModel();

                // 3-1. 선택값이 무효일 때 -> 첫 번째 다운로드된 모델로 자동 설정
                vm.SelectedModel = "invalid_model.bin";
                vm.RefreshAvailableModels(primaryDir, legacyDir);
                if (vm.SelectedModel != "ggml-base.bin" && vm.SelectedModel != "ggml-normal.bin")
                {
                    throw new AssertException($"선택값이 무효일 때 다운로드된 첫 번째 모델로 자동 설정되지 않았습니다. 현재: {vm.SelectedModel}");
                }

                // 3-2. 기존 SelectedModel이 ResolveModelPath로 유효할 때 -> 새 모델이 추가되어도 기존 선택값 유지
                vm.SelectedModel = "ggml-base.bin";
                File.WriteAllText(Path.Combine(primaryDir, "ggml-medium.bin"), "medium_model_data");
                vm.RefreshAvailableModels(primaryDir, legacyDir);
                if (vm.SelectedModel != "ggml-base.bin")
                {
                    throw new AssertException($"기존 SelectedModel이 ResolveModelPath로 유효함에도 불구하고 다른 모델로 자동 변경되었습니다. 현재: {vm.SelectedModel}, 기대치: ggml-base.bin");
                }
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }
    }
}
