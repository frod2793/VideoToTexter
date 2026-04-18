using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VideoToText.Core
{
    /// <summary>
    /// [설명]: 변환 작업 대기열을 관리하는 서비스 인터페이스입니다.
    /// </summary>
    public interface IJobQueueManager
    {
        IReadOnlyList<TranscriptionJobDTO> Jobs { get; }
        bool IsProcessing { get; }
        bool IsPaused { get; set; }
        
        event Action<TranscriptionJobDTO>? JobAdded;
        event Action<TranscriptionJobDTO>? JobUpdated;
        event Action<TranscriptionJobDTO>? JobRemoved;
        event Action<bool>? ProcessingStateChanged;
        event Action<TranscriptionJobDTO, TranscriptionResultDTO>? SegmentDetected;

        void AddJob(TranscriptionJobDTO job);
        void RemoveJob(Guid jobId);
        void ClearJobs();
        void UpdateJob(TranscriptionJobDTO updatedJob);

        Task StartBackgroundWorkerAsync(CancellationToken cancellationToken = default);
        void PauseProcessing();
        void ResumeProcessing();
        void StopProcessing();
    }
}
