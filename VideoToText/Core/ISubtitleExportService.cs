using System.Collections.Generic;
using System.Threading.Tasks;

#region 자막 내보내기 인터페이스
namespace VideoToText.Core
{
    /// <summary>
    /// [설명]: 인식된 텍스트 데이터를 특정 자막 포맷 파일로 저장하는 기능을 정의합니다.
    /// </summary>
    public interface ISubtitleExportService
    {
        /// <summary>
        /// [설명]: 변환 결과 리스트를 받아 파일로 저장합니다.
        /// </summary>
        /// <param name="results">변환 결과 데이터 리스트</param>
        /// <param name="outputPath">저장할 파일 경로</param>
        Task ExportAsync(IEnumerable<TranscriptionResultDTO> results, string outputPath);
    }
}
#endregion
