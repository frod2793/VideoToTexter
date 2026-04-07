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
    /// [설명]: 인덱스나 타임스탬프 없이 순수하게 텍스트 내용만 파일로 저장하는 구현체입니다.
    /// </summary>
    public class PlainTextExporter : ISubtitleExportService
    {
        public async Task ExportAsync(IEnumerable<TranscriptionResultDTO> results, string outputPath)
        {
            try
            {
                var sb = new StringBuilder();

                foreach (var segment in results)
                {
                    // 텍스트 내용만 추가
                    sb.AppendLine(segment.Text.Trim());
                }

                await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
                Console.WriteLine($"[텍스트 저장 완료]: {Path.GetFileName(outputPath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[텍스트 저장 오류]: {ex.Message}");
            }
        }
    }
}
#endregion
