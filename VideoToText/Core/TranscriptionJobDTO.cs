using System;

namespace VideoToText.Core
{
    /// <summary>
    /// [설명]: 변환 작업의 상태를 나타내는 열거형입니다.
    /// </summary>
    public enum JobStatus
    {
        Pending,      // 대기 중
        Scheduled,    // 예약됨
        Running,      // 실행 중
        Completed,    // 완료됨
        Cancelled,    // 취소됨
        Failed        // 실패함
    }

    /// <summary>
    /// [설명]: 개별 변환 작업의 메타데이터와 상태를 관리하는 DTO입니다.
    /// </summary>
    public class TranscriptionJobDTO
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string VideoPath { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public JobStatus Status { get; set; } = JobStatus.Pending;
        public float Progress { get; set; }
        public string StatusMessage { get; set; } = "대기 중";
        public DateTime? ScheduledTime { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }
}
