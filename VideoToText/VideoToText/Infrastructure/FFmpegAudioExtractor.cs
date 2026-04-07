using System;
using System.IO;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using VideoToText.Core;

#region 내부 로직 구현
namespace VideoToText.Infrastructure
{
    /// <summary>
    /// [설명]: FFmpeg을 사용하여 동영상에서 오디오를 추출하는 구현 클래스입니다.
    /// [확장]: 특정 시작 시간과 추출 길이를 지원하여 대용량 영상을 분할 처리할 수 있습니다.
    /// </summary>
    public class FFmpegAudioExtractor : IAudioExtractor
    {
        public async Task<bool> ExtractAudioAsync(string videoFilePath, string outputAudioPath, TimeSpan? startTime = null, TimeSpan? duration = null)
        {
            try
            {
                if (!File.Exists(videoFilePath)) throw new FileNotFoundException("원본 동영상 파일을 찾을 수 없습니다.");
                if (File.Exists(outputAudioPath)) File.Delete(outputAudioPath);

                // 구간 추출 옵션 구성
                string ssOption = startTime.HasValue ? $"-ss {startTime.Value:hh\\:mm\\:ss\\.fff}" : "";
                string tOption = duration.HasValue ? $"-t {duration.Value:hh\\:mm\\:ss\\.fff}" : "";

                // FFmpeg 인수: -ss를 -i 앞으로 이동하여 Fast Seek 적용 (속도 대폭 향상)
                string arguments = $"{ssOption} -i \"{videoFilePath}\" {tOption} -ar 16000 -ac 1 -c:a pcm_s16le \"{outputAudioPath}\"";
                
                var conversion = await FFmpeg.Conversions.New().Start(arguments);
                return File.Exists(outputAudioPath);
            }
            catch (Exception ex)
            {
                // [수석 개발자 조언]: 상세 정보를 담아 로깅해야 원인 파악이 빠릅니다.
                string detail = $"[FFmpeg 오디오 추출 오류]\n" +
                               $"- 파일: {videoFilePath}\n" +
                               $"- 메시지: {ex.Message}\n" +
                               $"- 탐색된 바이너리 경로: {FFmpeg.ExecutablesPath}\n" +
                               $"- 해결 방법: 'ffmpeg' 폴더 내에 ffmpeg.exe 및 ffprobe.exe가 존재하는지 확인하세요.";
                
                Console.WriteLine(detail);
                throw new InvalidOperationException(detail, ex);
            }
        }
    }
}
#endregion
