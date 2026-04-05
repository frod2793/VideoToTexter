using System;
using System.Threading.Tasks;

#region 오디오 추출 인터페이스
namespace VideoToText.Core
{
    /// <summary>
    /// [설명]: 미디어 파일에서 오디오를 추출하는 기능을 정의합니다.
    /// [확장]: 특정 구간(시작 시간, 길이) 추출 기능을 지원합니다.
    /// </summary>
    public interface IAudioExtractor
    {
        /// <summary>
        /// [설명]: 동영상 파일의 특정 구간에서 오디오를 추출합니다.
        /// </summary>
        /// <param name="videoFilePath">원본 동영상 경로</param>
        /// <param name="outputAudioPath">저장할 오디오 경로</param>
        /// <param name="startTime">시작 시간 (null이면 처음부터)</param>
        /// <param name="duration">추출할 길이 (null이면 끝까지)</param>
        Task<bool> ExtractAudioAsync(string videoFilePath, string outputAudioPath, TimeSpan? startTime = null, TimeSpan? duration = null);
    }
}
#endregion
