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
            var sb = new StringBuilder();

            foreach (var segment in results)
            {
                // 텍스트 내용만 추가
                sb.AppendLine(segment.Text.Trim());
            }

            // UTF-8로 배타적 생성(기존 파일 덮어쓰기 방지) 저장
            using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(sb.ToString());
            }
            Console.WriteLine($"[텍스트 저장 완료]: {Path.GetFileName(outputPath)}");
        }
    }
}
#endregion
