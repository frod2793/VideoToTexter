# 비디오 텍스터 (Video Texter) — 아키텍처 문서

> **문서 버전**: 1.0  
> **작성일**: 2026-04-05

---

## 1. 계층 구조 (Layer Architecture)

```mermaid
graph TB
    subgraph UI["VideoToText.Avalonia (Presentation)"]
        View["MainWindow.axaml<br/>(View)"]
        ViewModel["MainWindowViewModel<br/>(ViewModel)"]
        View --> ViewModel
    end

    subgraph App["VideoToText (Core Library)"]
        AppLogic["VideoToTextApp<br/>(Application)"]
        
        subgraph Core["Core (Interfaces & DTO)"]
            IAudio["IAudioExtractor"]
            ISTT["ITranscriptionService"]
            IExport["ISubtitleExportService"]
            DTO["TranscriptionResultDTO"]
        end

        subgraph Infra["Infrastructure (구현체)"]
            FFmpeg["FFmpegAudioExtractor"]
            Whisper["WhisperTranscriptionService"]
            SRT["SrtSubtitleExporter"]
            TXT["PlainTextExporter"]
        end
    end

    ViewModel --> AppLogic
    AppLogic --> IAudio
    AppLogic --> ISTT
    AppLogic --> IExport
    FFmpeg -.->|implements| IAudio
    Whisper -.->|implements| ISTT
    SRT -.->|implements| IExport
    TXT -.->|implements| IExport
```

---

## 2. 데이터 흐름 (Data Flow)

```mermaid
sequenceDiagram
    participant User as 사용자
    participant View as MainWindow (View)
    participant VM as MainWindowViewModel
    participant App as VideoToTextApp
    participant FF as FFmpegAudioExtractor
    participant WH as WhisperTranscriptionService
    participant EX as Exporters (SRT/TXT)

    User->>View: 영상 파일 선택
    View->>VM: VideoPath 바인딩
    User->>View: 변환 시작 클릭
    View->>VM: StartTranscriptionCommand
    VM->>App: RunAsync(videoPath, ...)
    App->>FF: ExtractAudioAsync(videoPath)
    FF-->>App: wavFilePath
    App->>WH: TranscribeAsync(wavFilePath)
    WH-->>App: List<TranscriptionResultDTO>
    App->>EX: ExportAsync(results, outputDir)
    EX-->>App: 파일 저장 완료
    App-->>VM: 콜백 (onProgressChanged, onStatusUpdate)
    VM-->>View: 프로퍼티 변경 알림
    View-->>User: UI 업데이트 (진행률, 로그, 상태)
```

---

## 3. 모델 다운로드 흐름

```mermaid
sequenceDiagram
    participant User as 사용자
    participant VM as MainWindowViewModel
    participant HF as HuggingFace CDN
    participant FS as 로컬 파일시스템

    User->>VM: 모델 선택 + 다운로드 클릭
    VM->>VM: IsDownloading = true
    VM->>HF: HTTP GET (ggml-{model}.bin)
    loop 청크 수신
        HF-->>VM: 8KB 청크 데이터
        VM->>FS: FileStream.WriteAsync()
        VM->>VM: DownloadProgress 갱신
    end
    VM->>FS: 파일 저장 완료
    VM->>VM: RefreshAvailableModels()
    VM->>VM: model.IsDownloaded = true
    Note over VM: NotifyPropertyChangedFor → UI 즉시 갱신
    VM->>VM: IsDownloading = false
```

---

## 4. FFmpeg 탐색 우선순위

앱 실행 시 FFmpeg 바이너리를 탐색하는 우선순위:

```
1. [앱 번들 내부] Contents/MacOS/ffmpeg       ← 최우선 (Standalone)
2. [시스템 설치] /opt/homebrew/bin/ffmpeg      ← 폴백 (개발 환경)
```

---

## 5. 클래스 역할 요약

| 클래스 | 역할 | 의존성 |
|--------|------|--------|
| `MainWindowViewModel` | UI 상태 관리, 커맨드 처리 | `VideoToTextApp` |
| `VideoToTextApp` | 전체 파이프라인 조율 | `IAudioExtractor`, `ITranscriptionService`, `ISubtitleExportService` |
| `FFmpegAudioExtractor` | 영상 → WAV 오디오 추출 | `Xabe.FFmpeg` |
| `WhisperTranscriptionService` | WAV → 텍스트 변환 (STT) | `Whisper.net` |
| `SrtSubtitleExporter` | 결과 → `.srt` 자막 파일 | 순수 C# |
| `PlainTextExporter` | 결과 → `.txt` 텍스트 파일 | 순수 C# |
| `ModelInfo` | 모델 데이터 + UI 바인딩 | `CommunityToolkit.Mvvm` |
| `TranscriptionResultDTO` | 인식 결과 전송 객체 | 없음 (POCO) |
