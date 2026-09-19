using System;
using System.IO;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using VideoToText.Core;

namespace VideoToText.Infrastructure
{
    public class FFmpegAudioExtractor : IAudioExtractor
    {
        public async Task<bool> ExtractAudioAsync(string videoFilePath, string outputAudioPath, TimeSpan? startTime = null, TimeSpan? duration = null)
        {
            if (!File.Exists(videoFilePath)) throw new FileNotFoundException("원본 동영상 파일을 찾을 수 없습니다.", videoFilePath);
            if (File.Exists(outputAudioPath)) File.Delete(outputAudioPath);

            string? dir = Path.GetDirectoryName(outputAudioPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string ssOption = startTime.HasValue ? $"-ss {startTime.Value:hh\\:mm\\:ss\\.fff}" : "";
            string tOption = duration.HasValue ? $"-t {duration.Value:hh\\:mm\\:ss\\.fff}" : "";

            string arguments = $"{ssOption} -i \"{videoFilePath}\" {tOption} -ar 16000 -ac 1 -c:a pcm_s16le \"{outputAudioPath}\"";

            var conversion = await FFmpeg.Conversions.New().Start(arguments);
            return File.Exists(outputAudioPath);
        }
    }
}
