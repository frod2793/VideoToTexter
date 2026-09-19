using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using VideoToText.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VideoToText.Checks")]

namespace VideoToText.Application
{
    public class VideoToTextApp
    {
        private readonly IAudioExtractor m_audioExtractor;
        private readonly ITranscriptionService m_transcriptionService;
        private readonly IEnumerable<ISubtitleExportService> m_subtitleExporters;
        private readonly TimeSpan m_chunkSize = TimeSpan.FromMinutes(20);

        public VideoToTextApp(
            IAudioExtractor audioExtractor,
            ITranscriptionService transcriptionService,
            IEnumerable<ISubtitleExportService> subtitleExporters)
        {
            m_audioExtractor = audioExtractor ?? throw new ArgumentNullException(nameof(audioExtractor));
            m_transcriptionService = transcriptionService ?? throw new ArgumentNullException(nameof(transcriptionService));
            m_subtitleExporters = subtitleExporters ?? throw new ArgumentNullException(nameof(subtitleExporters));
        }

        public async Task RunAsync(
            string videoPath,
            string? outputDirectory = null,
            Action<string>? onStatusUpdate = null,
            Action<TranscriptionResultDTO>? onSegmentDetected = null,
            Action<float>? onProgressChanged = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!File.Exists(videoPath))
                {
                    throw new FileNotFoundException($"동영상 파일을 찾을 수 없습니다: {videoPath}");
                }

                // 1. 출력 디렉터리 확인/생성 및 기존 결과 파일 덮어쓰기 방지 사전 검사
                string baseDir = !string.IsNullOrEmpty(outputDirectory) ? outputDirectory! : Path.GetDirectoryName(videoPath)!;
                if (!Directory.Exists(baseDir))
                {
                    Directory.CreateDirectory(baseDir);
                }

                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(videoPath);
                var exportPlans = new List<(ISubtitleExportService Exporter, string OutputPath)>();
                foreach (var exporter in m_subtitleExporters)
                {
                    string extension = exporter.GetType().Name.Contains("Srt") ? ".srt" : ".txt";
                    string outputPath = Path.Combine(baseDir, fileNameWithoutExt + extension);
                    exportPlans.Add((exporter, outputPath));
                }

                var existingFiles = exportPlans.Where(p => File.Exists(p.OutputPath)).Select(p => p.OutputPath).ToList();
                if (existingFiles.Count > 0)
                {
                    throw new IOException($"기존 결과 파일이 이미 존재하여 작업을 중단합니다: {string.Join(", ", existingFiles)}");
                }

                onStatusUpdate?.Invoke("미디어 정보 분석 중 (FFmpeg)...");
                IMediaInfo mediaInfo;
                try
                {
                    mediaInfo = await FFmpeg.GetMediaInfo(videoPath);
                }
                catch (Exception ex)
                {
                    throw new Exception($"FFmpeg이 미디어 정보를 읽지 못했습니다. 바이너리 설정 및 실행 권한을 확인하세요.\n상세: {ex.Message}");
                }

                double totalSeconds = mediaInfo.Duration.TotalSeconds;

                onStatusUpdate?.Invoke($"[시작] 총 길이: {mediaInfo.Duration:hh\\:mm\\:ss}");

                var totalResults = new List<TranscriptionResultDTO>();
                TimeSpan currentStartTime = TimeSpan.Zero;
                int partIndex = 1;

                // 2. OS 임시 폴더의 고유 작업 디렉터리 사용 및 finally 정리
                string jobTempDir = Path.Combine(Path.GetTempPath(), "VideoToTexter", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(jobTempDir);

                try
                {
                    while (currentStartTime.TotalSeconds < totalSeconds)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        TimeSpan remainingTime = mediaInfo.Duration - currentStartTime;
                        TimeSpan durationToExtract = remainingTime > m_chunkSize ? m_chunkSize : remainingTime;

                        string tempAudioPath = Path.Combine(jobTempDir, $"temp_part_{partIndex}.wav");

                        onStatusUpdate?.Invoke($"[Part {partIndex}] 오디오 추출 중...");
                        bool isExtracted = await m_audioExtractor.ExtractAudioAsync(videoPath, tempAudioPath, currentStartTime, durationToExtract);
                        if (!isExtracted || !File.Exists(tempAudioPath))
                        {
                            throw new InvalidOperationException($"[Part {partIndex}] 오디오 추출 실패: 미디어에서 오디오 스트림을 추출할 수 없습니다.");
                        }

                        onStatusUpdate?.Invoke($"[Part {partIndex}] 음성 인식 중 (GPU 가속 시도)...");
                        await foreach (var result in m_transcriptionService.TranscribeAsync(tempAudioPath, cancellationToken))
                        {
                            result.Start += currentStartTime;
                            result.End += currentStartTime;

                            float progress = (float)(result.End.TotalSeconds / totalSeconds);
                            onProgressChanged?.Invoke(Math.Min(progress, 0.99f));

                            onSegmentDetected?.Invoke(result);
                            totalResults.Add(result);
                        }

                        if (File.Exists(tempAudioPath))
                        {
                            try { File.Delete(tempAudioPath); } catch { }
                        }

                        TimeSpan overlap = TimeSpan.FromSeconds(10);
                        currentStartTime += durationToExtract; // 실제 전진

                        if (currentStartTime >= mediaInfo.Duration) break;

                        currentStartTime -= overlap;
                        if (currentStartTime.TotalSeconds < 0) currentStartTime = TimeSpan.Zero;

                        partIndex++;
                    }
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(jobTempDir))
                        {
                            Directory.Delete(jobTempDir, recursive: true);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        Console.WriteLine($"[임시 파일 정리 경고]: {cleanupEx.Message}");
                    }
                }

                onStatusUpdate?.Invoke("결과 파일 저장 중...");

                var mergedResults = MergeOverlappingSegments(totalResults);

                // 3. 결과 파일 배타적 저장 및 부분 저장 실패 감지
                var savedFiles = new List<string>();
                try
                {
                    foreach (var (exporter, outputPath) in exportPlans)
                    {
                        await exporter.ExportAsync(mergedResults, outputPath);
                        savedFiles.Add(outputPath);
                    }
                }
                catch (Exception ex)
                {
                    if (savedFiles.Count > 0 && savedFiles.Count < exportPlans.Count)
                    {
                        string remaining = string.Join(", ", savedFiles);
                        throw new IOException($"결과 파일 저장 중 일부만 저장되었습니다 (저장 완료된 잔여 파일: {remaining}). 오류: {ex.Message}", ex);
                    }
                    throw;
                }

                // 4. 저장 완료 뒤에만 100% 통지
                onProgressChanged?.Invoke(1.0f);
                onStatusUpdate?.Invoke("변환 작업이 완료되었습니다!");
            }
            catch (Exception ex)
            {
                onStatusUpdate?.Invoke($"[에러] {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 인접 청크 경계에서 시간 구간이 겹치고 공백 정규화 후 텍스트가 같은 세그먼트를 직전 보존 세그먼트와 하나로 병합합니다.
        /// 시간상 분리된 동일 문장은 자연스러운 반복 발화로 보존합니다.
        /// 모든 출력 세그먼트의 Start &lt;= End 및 Start 시간 오름차순을 보장합니다.
        /// </summary>
        internal static List<TranscriptionResultDTO> MergeOverlappingSegments(IReadOnlyList<TranscriptionResultDTO> segments)
        {
            if (segments == null || segments.Count == 0) return new List<TranscriptionResultDTO>();

            // 1. 구간 유효성 정규화 (Start <= End) 및 Start 오름차순 정렬
            var sorted = segments
                .Select(s => new TranscriptionResultDTO
                {
                    Start = s.Start <= s.End ? s.Start : s.End,
                    End = s.End >= s.Start ? s.End : s.Start,
                    Text = s.Text ?? string.Empty,
                    Probability = s.Probability
                })
                .OrderBy(s => s.Start)
                .ThenBy(s => s.End)
                .ToList();

            var merged = new List<TranscriptionResultDTO>();

            // ponytail: 청크 경계에서 음성 분할이 달라져(예: 한 청크는 한 문장 전체, 다른 청크는 두 문장으로 분할)
            // 텍스트가 일치하지 않는 경우, 휴리스틱 문자열 병합은 누락이나 중복 왜곡을 유발할 수 있으므로 병합하지 않고 원본을 보존합니다.
            // 향후 Whisper의 단어 단위 타임스탬프(word-level timestamps)가 활성화되고 정렬 신뢰도가 확보되는 경우에 한해,
            // 단어 단위 동적 시간 왜곡(DTW) 또는 단어 정렬 기반 병합으로 승격하여 분할 불일치를 정밀 해결합니다.
            foreach (var curr in sorted)
            {
                if (merged.Count == 0)
                {
                    merged.Add(curr);
                    continue;
                }

                var prev = merged[merged.Count - 1];

                // 계획의 "인접 청크 결과로 한정": 직전 보존 항목과 구간 겹침(현재.Start <= 직전.End) + 공백 정규화 동일일 때만 병합
                bool isOverlapping = curr.Start <= prev.End;
                bool isSameText = string.Equals(NormalizeText(prev.Text), NormalizeText(curr.Text), StringComparison.OrdinalIgnoreCase);

                if (isOverlapping && isSameText)
                {
                    prev.Start = prev.Start < curr.Start ? prev.Start : curr.Start;
                    prev.End = prev.End > curr.End ? prev.End : curr.End;
                    prev.Probability = Math.Max(prev.Probability, curr.Probability);
                }
                else
                {
                    merged.Add(curr);
                }
            }

            return merged.OrderBy(s => s.Start).ThenBy(s => s.End).ToList();
        }

        private static string NormalizeText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var parts = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }
    }
}
