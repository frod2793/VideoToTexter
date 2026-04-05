using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using VideoToText.Core;

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

                while (currentStartTime.TotalSeconds < totalSeconds)
                {
                    TimeSpan remainingTime = mediaInfo.Duration - currentStartTime;
                    TimeSpan durationToExtract = remainingTime > m_chunkSize ? m_chunkSize : remainingTime;

                    string tempAudioPath = Path.Combine(Path.GetDirectoryName(videoPath)!, $"temp_part_{partIndex}.wav");
                    
                    onStatusUpdate?.Invoke($"[Part {partIndex}] 오디오 추출 중...");
                    bool isExtracted = await m_audioExtractor.ExtractAudioAsync(videoPath, tempAudioPath, currentStartTime, durationToExtract);
                    if (!isExtracted) break;

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

                    if (File.Exists(tempAudioPath)) File.Delete(tempAudioPath);

                    TimeSpan overlap = TimeSpan.FromSeconds(10);
                    currentStartTime += durationToExtract; // 실제 전진
                    
                    if (currentStartTime >= mediaInfo.Duration) break;
                    
                    currentStartTime -= overlap;
                    if (currentStartTime.TotalSeconds < 0) currentStartTime = TimeSpan.Zero;

                    partIndex++;
                }

                onStatusUpdate?.Invoke("결과 파일 저장 중...");
                onProgressChanged?.Invoke(1.0f);

                string baseDir = !string.IsNullOrEmpty(outputDirectory) ? outputDirectory! : Path.GetDirectoryName(videoPath)!;
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(videoPath);

                foreach (var exporter in m_subtitleExporters)
                {
                    string extension = exporter.GetType().Name.Contains("Srt") ? ".srt" : ".txt";
                    string outputPath = Path.Combine(baseDir, fileNameWithoutExt + extension);
                    await exporter.ExportAsync(totalResults, outputPath);
                }

                onStatusUpdate?.Invoke("변환 작업이 완료되었습니다!");
            }
            catch (Exception ex)
            {
                onStatusUpdate?.Invoke($"[에러] {ex.Message}");
                throw;
            }
        }
    }
}
