using System;

#region 데이터 모델 (DTO)
namespace VideoToText.Core
{
    /// <summary>
    /// [설명]: 음성 인식 결과를 담는 데이터 전송 객체입니다.
    /// </summary>
    public class TranscriptionResultDTO
    {
        public string Text { get; set; } = string.Empty;
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public float Probability { get; set; }

        public override string ToString()
        {
            return $"[{Start:hh\\:mm\\:ss} --> {End:hh\\:mm\\:ss}] {Text}";
        }
    }
}
#endregion
