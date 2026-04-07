using System.Collections.Generic;
using System.Threading;

#region STT 엔진 인터페이스
namespace VideoToText.Core
{
    /// <summary>
    /// [설명]: 음성 데이터를 텍스트로 변환하는 기능을 정의합니다.
    /// </summary>
    public interface ITranscriptionService
    {
        /// <summary>
        /// [설명]: 오디오 파일에서 텍스트를 추출하여 스트리밍 형식으로 반환합니다.
        /// </summary>
        /// <param name="audioPath">오디오 파일 경로</param>
        /// <param name="cancellationToken">작업 취소 토큰</param>
        /// <returns>변환 결과 세그먼트의 비동기 열거자</returns>
        IAsyncEnumerable<TranscriptionResultDTO> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default);
    }
}
#endregion
