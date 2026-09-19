using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoToText.Core;

namespace VideoToText.Avalonia.ViewModels
{
    /// <summary>
    /// [설명]: 개별 변환 작업(Job)의 상태를 UI에 표시하기 위한 뷰모델입니다.
    /// </summary>
    public partial class JobViewModel : ViewModelBase
    {
        internal readonly TranscriptionJobDTO m_dto;

        [ObservableProperty]
        private string m_statusMessage;

        [ObservableProperty]
        private float m_progress;

        [ObservableProperty]
        private JobStatus m_status;



        public Guid Id => m_dto.Id;
        public string FileName => Path.GetFileName(m_dto.VideoPath);
        public string FullPath => m_dto.VideoPath;
        public string ModelName => m_dto.ModelName;
        public string? ScheduledTimeText => m_dto.ScheduledTime?.ToString("HH:mm:ss");

        public bool IsRunning => Status == JobStatus.Running;
        public bool IsCompleted => Status == JobStatus.Completed;
        public bool IsFailed => Status == JobStatus.Failed;
        public bool IsCancelled => Status == JobStatus.Cancelled;
        public bool IsPending => Status == JobStatus.Pending;
        public bool IsScheduled => Status == JobStatus.Scheduled;

        public JobViewModel(TranscriptionJobDTO dto)
        {
            m_dto = dto ?? throw new ArgumentNullException(nameof(dto));
            m_statusMessage = dto.StatusMessage;
            m_progress = dto.Progress;
            m_status = dto.Status;
        }

        /// <summary>
        /// [설명]: DTO의 최신 상태를 뷰모델 프로퍼티에 명시적으로 동기화합니다.
        /// </summary>
        public void UpdateFromDto()
        {
            StatusMessage = m_dto.StatusMessage;
            Progress = m_dto.Progress;
            Status = m_dto.Status;

            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsCancelled));
            OnPropertyChanged(nameof(IsPending));
            OnPropertyChanged(nameof(ScheduledTimeText));
        }
    }
}
