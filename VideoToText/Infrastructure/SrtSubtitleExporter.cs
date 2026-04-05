using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using VideoToText.Core;

#region 내부 로직 구현
namespace VideoToText.Infrastructure
{
    /// <summary>
    /// [설명]: 표준 SRT(SubRip) 자막 파일 포맷으로 데이터를 저장하는 구현체입니다.
    /// </summary>
    public class SrtSubtitleExporter : ISubtitleExportService
    {
        public async Task ExportAsync(IEnumerable<TranscriptionResultDTO> results, string outputPath)
        {
            try
            {
                var sb = new StringBuilder();
                int index = 1;

                foreach (var segment in results)
                {
                    // 1. 인덱스
                    sb.AppendLine(index.ToString());

                    // 2. 타임스탬프 (SRT 규격: hh:mm:ss,fff)
                    string startTime = FormatTime(segment.Start);
                    string endTime = FormatTime(segment.End);
                    sb.AppendLine($"{startTime} --> {endTime}");

                    // 3. 내용 및 구분줄
                    sb.AppendLine(segment.Text.Trim());
                    sb.AppendLine();

                    index++;
                }

                // UTF-8로 저장
                await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
                Console.WriteLine($"[자막 저장 완료]: {Path.GetFileName(outputPath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[자막 저장 오류]: {ex.Message}");
            }
        }

        /// <summary>
        /// [설명]: TimeSpan을 SRT 표준 타임스탬프 형식으로 변환합니다.
        /// </summary>
        private string FormatTime(TimeSpan time)
        {
            // SRT 규격: hh:mm:ss,fff (밀리초 구분자가 쉼표임에 주의)
            return string.Format("{0:D2}:{1:D2}:{2:D2},{3:D3}",
                time.Hours,
                time.Minutes,
                time.Seconds,
                time.Milliseconds);
        }
    }
}
#endregion
