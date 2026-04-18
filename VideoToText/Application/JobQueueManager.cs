using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoToText.Core;

namespace VideoToText.Application
{
    /// <summary>
    /// [설명]: 변환 작업 대기열을 관리하고 자율적으로 실행하는 백그라운드 매니저입니다.
    /// </summary>
    public class JobQueueManager : IJobQueueManager
    {
        private readonly List<TranscriptionJobDTO> m_jobs = new List<TranscriptionJobDTO>();
        private readonly Func<VideoToTextApp> m_appProvider;
        private CancellationTokenSource? m_cts;
        private bool m_isProcessing;
        private bool m_isPaused;
        private readonly SemaphoreSlim m_signal = new SemaphoreSlim(0);

        public IReadOnlyList<TranscriptionJobDTO> Jobs => m_jobs.AsReadOnly();
        public bool IsProcessing => m_isProcessing;
        public bool IsPaused
        {
            get => m_isPaused;
            set
            {
                if (m_isPaused != value)
                {
                    m_isPaused = value;
                    if (!value) m_signal.Release(); // 재개 시 대기 상태를 강제로 깨움
                }
            }
        }

        public event Action<TranscriptionJobDTO>? JobAdded;
        public event Action<TranscriptionJobDTO>? JobUpdated;
        public event Action<TranscriptionJobDTO>? JobRemoved;
        public event Action<bool>? ProcessingStateChanged;
        public event Action<TranscriptionJobDTO, TranscriptionResultDTO>? SegmentDetected;

        public JobQueueManager(Func<VideoToTextApp> appProvider)
        {
            m_appProvider = appProvider ?? throw new ArgumentNullException(nameof(appProvider));
        }

        public void AddJob(TranscriptionJobDTO job)
        {
            lock (m_jobs)
            {
                m_jobs.Add(job);
            }
            m_signal.Release(); // 작업 추가 시 대기 해제
            JobAdded?.Invoke(job);
        }

        public void RemoveJob(Guid jobId)
        {
            TranscriptionJobDTO? job;
            lock (m_jobs)
            {
                job = m_jobs.FirstOrDefault(j => j.Id == jobId);
                if (job != null) m_jobs.Remove(job);
            }

            // 삭제될 때 신호를 줘서 다시 검사하도록 유도할 수 있지만 굳이 안 줘도 루프는 깨지지 않음
            if (job != null)
            {
                JobRemoved?.Invoke(job);
            }
        }

        public void ClearJobs()
        {
            lock (m_jobs)
            {
                m_jobs.Clear();
            }
        }

        public void UpdateJob(TranscriptionJobDTO updatedJob)
        {
            lock (m_jobs)
            {
                var existingJob = m_jobs.FirstOrDefault(j => j.Id == updatedJob.Id);
                if (existingJob != null)
                {
                    existingJob.Status = updatedJob.Status;
                    existingJob.ScheduledTime = updatedJob.ScheduledTime;
                }
            }
            m_signal.Release(); // 상태 갱신 시 대기 해제 (예약이 지금으로 바뀌었을 수도 있음)
            JobUpdated?.Invoke(updatedJob);
        }

        public void PauseProcessing()
        {
            IsPaused = true;
            ProcessingStateChanged?.Invoke(false);
        }

        public void ResumeProcessing()
        {
            IsPaused = false;
            ProcessingStateChanged?.Invoke(true);
        }

        public void StopProcessing()
        {
            m_cts?.Cancel();
        }

        public async Task StartBackgroundWorkerAsync(CancellationToken cancellationToken = default)
        {
            if (m_isProcessing) return;

            m_isProcessing = true;
            ProcessingStateChanged?.Invoke(!m_isPaused);
            m_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                while (!m_cts.Token.IsCancellationRequested)
                {
                    if (m_isPaused)
                    {
                        await Task.Delay(1000, m_cts.Token);
                        continue;
                    }

                    TranscriptionJobDTO? nextJob = null;
                    TimeSpan? delayUntilNextJob = null;

                    lock (m_jobs)
                    {
                        // 1. 현재 실행 가능한 작업 탐색 (Pending 또는 예약시간이 지난 Scheduled)
                        nextJob = m_jobs.FirstOrDefault(j => 
                            j.Status == JobStatus.Pending || 
                            (j.Status == JobStatus.Scheduled && j.ScheduledTime.HasValue && j.ScheduledTime.Value <= DateTime.Now));

                        if (nextJob == null)
                        {
                            // 2. 예약된 작업 중 가장 가까운 작업의 대기 시간 계산
                            var upcomingJob = m_jobs.Where(j => j.Status == JobStatus.Scheduled && j.ScheduledTime.HasValue && j.ScheduledTime.Value > DateTime.Now)
                                .OrderBy(j => j.ScheduledTime)
                                .FirstOrDefault();

                            if (upcomingJob != null)
                            {
                                delayUntilNextJob = upcomingJob.ScheduledTime!.Value - DateTime.Now;
                            }
                        }
                    }

                    if (nextJob != null)
                    {
                        await ExecuteJobInternal(nextJob, m_cts.Token);
                    }
                    else
                    {
                        // 3. 작업 대기 로직 (다음 예약 작업이 있다면 그 시간만큼, 없다면 무한 대기)
                        int waitMs = delayUntilNextJob.HasValue ? (int)Math.Max(0, delayUntilNextJob.Value.TotalMilliseconds) : Timeout.Infinite;
                        
                        try
                        {
                            // 세마포어 신호(작업추가, 상태변경, 재개)가 오거나 시간이 다 될 때까지 대기
                            await m_signal.WaitAsync(waitMs, m_cts.Token);
                        }
                        catch (OperationCanceledException) { }
                    }
                }
            }
            finally
            {
                m_isProcessing = false;
                ProcessingStateChanged?.Invoke(false);
            }
        }

        private async Task ExecuteJobInternal(TranscriptionJobDTO job, CancellationToken token)
        {
            try
            {
                job.Status = JobStatus.Running;
                job.StartTime = DateTime.Now;
                job.StatusMessage = "오디오 추출 및 인식 중...";
                JobUpdated?.Invoke(job);

                // 지연 초기화된 앱 인스턴스 획득
                var app = m_appProvider();

                await app.RunAsync(
                    job.VideoPath,
                    outputDirectory: job.OutputPath,
                    onStatusUpdate: (msg) => 
                    {
                        job.StatusMessage = msg;
                        JobUpdated?.Invoke(job);
                    },
                    onSegmentDetected: (result) => 
                    {
                        SegmentDetected?.Invoke(job, result);
                    },
                    onProgressChanged: (p) => 
                    {
                        job.Progress = p * 100;
                        JobUpdated?.Invoke(job);
                    },
                    cancellationToken: token
                );

                job.Status = JobStatus.Completed;
                job.EndTime = DateTime.Now;
                job.StatusMessage = "완료됨";
                job.Progress = 100;
            }
            catch (OperationCanceledException)
            {
                // 사용자가 멈추거나 취소했을 때, 중도 취소된 작업도 나중에 재개 가능하도록 Cancelled 대신 일단 Pending으로 반환
                // (일반적인 큐 개념. 단, 강제 Cancel이면 Cancelled)
                job.Status = JobStatus.Cancelled;
                job.StatusMessage = "취소됨";
            }
            catch (Exception ex)
            {
                job.Status = JobStatus.Failed;
                job.StatusMessage = $"에러: {ex.Message}";
            }
            finally
            {
                JobUpdated?.Invoke(job);
            }
        }
    }
}
