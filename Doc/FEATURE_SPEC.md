# 비디오 텍스터 (Video Texter) — 기능 명세서

> **문서 버전**: 1.0  
> **작성일**: 2026-04-05  
> **앱 이름**: Video Texter (비디오 텍스터)  
> **번들 ID**: `com.woodenshield.videotexter`

---

## 1. 제품 개요

**비디오 텍스터**는 영상 파일에서 음성을 추출하고, OpenAI Whisper AI 모델을 사용하여 텍스트(자막)로 변환하는 **macOS 네이티브 데스크톱 앱**입니다.

### 핵심 가치
- **독립 실행 런타임**: FFmpeg와 Whisper 네이티브 런타임을 앱 번들에 포함
- **로컬 처리**: 외부 서버 없이 사용자 로컬 장비에서 모든 작업을 완료
- **간편한 UX**: 파일 선택 → 모델 선택 → 변환 시작, 3단계로 완료

---

## 2. 시스템 요구사항

| 항목 | 최소 사양 |
|------|-----------|
| **OS** | macOS 11.0 (Big Sur) 이상 |
| **아키텍처** | Apple Silicon (arm64) |
| **RAM** | 4GB (tiny 모델) / 8GB+ (large 계열 모델 추천) |
| **디스크** | 약 200MB (앱) + 모델 용량 (75MB ~ 3.1GB) |
| **런타임** | .NET 8.0 (Self-Contained, 앱에 내장) |

---

## 3. 기능 상세

### 3.1. 영상 파일 선택

| 항목 | 내용 |
|------|------|
| **설명** | 변환할 영상 파일을 로컬 디스크에서 선택 |
| **지원 형식** | `.mp4`, `.mov`, `.mkv`, `.avi`, `.webm` 등 FFmpeg 지원 모든 형식 |
| **UI 요소** | 텍스트 입력 필드 + `찾기` 버튼 (시스템 파일 다이얼로그) |

### 3.2. 출력 위치 설정

| 항목 | 내용 |
|------|------|
| **설명** | 변환된 자막 파일의 저장 위치를 지정 |
| **기본값** | 원본 영상 파일과 동일한 디렉토리 |
| **UI 요소** | 텍스트 입력 필드 + `변경` 버튼 (폴더 선택 다이얼로그) |

### 3.3. Whisper AI 모델 관리

| 항목 | 내용 |
|------|------|
| **설명** | 음성 인식에 사용할 Whisper GGUF 모델을 선택 및 다운로드 |
| **모델 목록** | 아래 표 참조 |
| **다운로드 소스** | HuggingFace (`ggerganov/whisper.cpp`) |
| **상태 표시** | `[다운로드됨]` / `[미다운로드]` 실시간 표시 |
| **즉시 갱신** | 다운로드 완료 즉시 UI에 상태 반영 |
| **저장 위치** | 새 GGUF 모델은 `LocalApplicationData/VideoTexter/Models`에 저장 |
| **호환성** | 앱 번들의 legacy `Models`는 읽기 전용 폴백이며, 새 다운로드를 그곳에 쓰지 않음 |

#### 지원 모델 목록

| 모델명 | 파일명 | 대략적 크기 | 정확도 | 속도 |
|--------|--------|-------------|--------|------|
| `tiny` | `ggml-tiny.bin` | ~75MB | ★☆☆☆☆ | ★★★★★ |
| `base` | `ggml-base.bin` | ~142MB | ★★☆☆☆ | ★★★★☆ |
| `small` | `ggml-small.bin` | ~466MB | ★★★☆☆ | ★★★☆☆ |
| `medium` | `ggml-medium.bin` | ~1.5GB | ★★★★☆ | ★★☆☆☆ |
| `large-v1` | `ggml-large-v1.bin` | ~3.1GB | ★★★★★ | ★☆☆☆☆ |
| `large-v2` | `ggml-large-v2.bin` | ~3.1GB | ★★★★★ | ★☆☆☆☆ |
| `large-v3` | `ggml-large-v3.bin` | ~3.1GB | ★★★★★ | ★☆☆☆☆ |
| `large-v3-turbo` | `ggml-large-v3-turbo.bin` | ~1.6GB | ★★★★★ | ★★★☆☆ |

### 3.4. 음성-텍스트 변환 (STT)

| 항목 | 내용 |
|------|------|
| **설명** | 선택한 영상에서 오디오를 추출하고, Whisper 모델로 음성을 텍스트로 변환 |
| **오디오 추출** | FFmpeg를 사용하여 영상에서 WAV 오디오 추출 |
| **인식 엔진** | Whisper.net (C# 네이티브 바인딩) |
| **Metal 가속** | macOS Metal 충돌 방지를 위해 `GGML_METAL=0`으로 CPU 기반 추론 |
| **실행 방식** | 작업 등록 시 선택 모델과 출력 위치를 고정하고, `시작` 뒤 대기열에서 한 작업씩 순차 실행 |
| **일시정지** | 현재 작업은 완료시키고 다음 작업 시작 전에 적용 |
| **출력 충돌** | 기존 `.srt` 또는 `.txt`는 보존하며 해당 작업을 실패 상태로 표시 |

### 3.5. 진행률 및 ETA 표시

| 항목 | 내용 |
|------|------|
| **설명** | 변환 작업의 실시간 진행률과 남은 예상 시간을 표시 |
| **진행률** | 프로그레스 바 + 퍼센트 수치 (0.0% ~ 100.0%) |
| **ETA** | `남은 시간: 약 Xm Ys` 형태로 동적 계산/표시 |
| **계산 방식** | 경과 시간 / 현재 진행률 기반 선형 추정 |

### 3.6. 작업 로그

| 항목 | 내용 |
|------|------|
| **설명** | 변환 과정의 상세 로그를 실시간으로 화면에 출력 |
| **로그 형식** | `[HH:mm:ss] 메시지` 타임스탬프 형태 |
| **최대 항목** | 100줄 (초과 시 가장 오래된 항목부터 자동 삭제) |
| **복사 기능** | `로그 복사` 버튼 클릭 시 전체 로그를 시스템 클립보드에 복사 |
| **색상** | 터미널 스타일 (`#00FFD2`, Consolas 모노스페이스 폰트) |

### 3.7. 내보내기 형식

| 형식 | 확장자 | 설명 |
|------|--------|------|
| **SRT 자막** | `.srt` | 타임코드 포함 표준 자막 파일 |
| **Plain Text** | `.txt` | 타임코드 없는 순수 텍스트 |

---

## 4. 아키텍처

### 4.1. 솔루션 구조

```
VideoToText/                        ← 솔루션 루트
├── Doc/                            ← 프로젝트 문서
│   ├── PROJECT_STATUS.md           ← 진행 상황
│   └── FEATURE_SPEC.md             ← 기능 명세서 (본 문서)
│
├── VideoToText/                    ← Core 라이브러리 (.NET Standard 2.1)
│   ├── Core/                       ← 인터페이스 및 DTO
│   │   ├── IAudioExtractor.cs
│   │   ├── ITranscriptionService.cs
│   │   ├── ISubtitleExportService.cs
│   │   └── TranscriptionResultDTO.cs
│   ├── Infrastructure/             ← 구현체
│   │   ├── FFmpegAudioExtractor.cs
│   │   ├── WhisperTranscriptionService.cs
│   │   ├── SrtSubtitleExporter.cs
│   │   └── PlainTextExporter.cs
│   └── Application/                ← 비즈니스 로직
│       ├── JobQueueManager.cs        ← Start/Pause 게이트와 순차 실행
│       └── VideoToTextApp.cs
│
└── VideoToText.Avalonia/           ← 데스크톱 UI 앱 (.NET 8.0 / Avalonia 11)
    ├── ViewModels/
    │   ├── ViewModelBase.cs
    │   └── MainWindowViewModel.cs  ← 핵심 뷰모델 (MVVM)
    ├── Views/
    │   ├── MainWindow.axaml        ← UI 레이아웃 (XAML)
    │   └── MainWindow.axaml.cs     ← 코드비하인드
    ├── Publish/
    │   └── VideoTexter.app/        ← macOS 앱 번들
    │       └── Contents/
    │           ├── Info.plist
    │           └── MacOS/
    │               ├── VideoToText.Avalonia  ← 실행 파일
    │               ├── ffmpeg               ← 내장 FFmpeg
    └── VideoToText.Avalonia.csproj
```

### 4.2. 핵심 설계 패턴

| 패턴 | 적용 대상 | 설명 |
|------|-----------|------|
| **MVVM** | UI 전체 | View ↔ ViewModel 데이터바인딩 |
| **DTO** | `TranscriptionResultDTO` | 계층 간 데이터 전송 객체 |
| **DI (수동)** | `VideoToTextApp` 생성자 | 인터페이스 기반 의존성 주입 |
| **Observer** | `ObservableProperty` | CommunityToolkit MVVM 프로퍼티 변경 알림 |
| **Strategy** | `ISubtitleExportService` | 내보내기 형식 런타임 교체 |

### 4.3. 기술 스택

| 계층 | 기술 | 버전 |
|------|------|------|
| **런타임** | .NET | 8.0 (Self-Contained) |
| **UI 프레임워크** | Avalonia | 11.x |
| **MVVM 툴킷** | CommunityToolkit.Mvvm | 최신 |
| **음성 인식** | Whisper.net | 최신 (GGUF 모델) |
| **오디오 추출** | Xabe.FFmpeg | 최신 |
| **FFmpeg 엔진** | FFmpeg (정적 바이너리) | 앱 번들 내장 |

---

## 5. 빌드 및 배포

### 5.1. 빌드 명령어

```bash
# 프로젝트 루트에서 실행
dotnet publish VideoToText.Avalonia/VideoToText.Avalonia.csproj \
  -c Release -r osx-arm64 \
  --self-contained true \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  -o VideoToText.Avalonia/Publish/osx-arm64-temp
```

### 5.2. 패키징 (수동)

```bash
# 빌드 결과물을 .app 번들에 복사
cp -rf VideoToText.Avalonia/Publish/osx-arm64-temp/* \
       VideoToText.Avalonia/Publish/VideoTexter.app/Contents/MacOS/

# FFmpeg 내장
cp /opt/homebrew/bin/ffmpeg \
   VideoToText.Avalonia/Publish/VideoTexter.app/Contents/MacOS/

# 실행 권한 부여
chmod +x VideoToText.Avalonia/Publish/VideoTexter.app/Contents/MacOS/VideoToText.Avalonia
chmod +x VideoToText.Avalonia/Publish/VideoTexter.app/Contents/MacOS/ffmpeg
```

### 5.3. 앱 번들 Location

```
VideoToText.Avalonia/Publish/VideoTexter.app
```

---

## 6. 음성 파일과 마이크 입력

- 기존 영상 가져오기와 함께 `.mp3`, `.m4a`, `.wav` 음성 파일을 대기열에 추가할 수 있다.
- 마이크는 시스템 기본 장치 또는 사용자가 선택한 장치를 사용하며, 사용자 선택 출력 폴더에 `녹음_yyyyMMdd_HHmmss.m4a` 형식으로 저장한다. 기존 파일은 덮어쓰지 않는다.
- FFmpeg `avfoundation` 입력과 AAC 128k/IPod(M4A) 출력으로 녹음한다. 정상 중지로 확정된 파일만 기존 대기열에 등록하며, 등록만 하고 변환을 자동 시작하지 않는다.
- macOS 번들은 `NSMicrophoneUsageDescription`으로 마이크 사용 목적을 제공한다.

### 미지원 범위

실시간 변환, 파형 표시, 오디오 편집, 노이즈 제거는 지원하지 않는다.
