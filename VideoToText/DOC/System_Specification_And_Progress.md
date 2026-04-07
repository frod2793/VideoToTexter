# VideoToTexter 시스템 명세 및 개발 현황 보고서

본 문서는 로컬 실행형 비디오/오디오 텍스트 변환 애플리케이션인 **VideoToTexter**의 기술적 사양과 현재까지의 개발 진행 상황, 그리고 유지보수 및 배포 시 주의 사항을 정리한 통합 명세서입니다.

---

## 1. 시스템 사양 (System Specification)

### 1.1 기술 스택 (Tech Stack)
- **프레임워크**: .NET 8.0
- **UI 플랫폼**: Avalonia UI (MVVM Pattern 적용)
- **핵심 엔진**:
  - **음성 인식 (STT)**: [Whisper.net](https://github.com/anaerobic/Whisper.net) (OpenAI Whisper)
  - **오디오 처리**: [Xabe.FFmpeg](https://ffmpeg.xabe.net/)
- **GPU 가속**:
  - **Windows**: CUDA (NVIDIA GPU 가속 지원)
  - **macOS**: Metal (Apple Silicon/Intel Mac 가속 지원)
- **의존성 관리**: 
  - CommunityToolkit.Mvvm (상태 및 명령 관리)
  - Microsoft.Extensions.DependencyInjection (서비스 생명주기 관리)

### 1.2 아키텍처 구조 (Architecture)
애플리케이션은 관심사 분리를 위해 4개의 계층으로 구성되어 있습니다:
1. **Core**: 인터페이스 정의 (`ITranscriptionService`, `IAudioExtractor`) 및 도메인 로직.
2. **Infrastructure**: 외부 라이브러리 연동 구현체 (`WhisperTranscriptionService`, `FFmpegAudioExtractor`, `ModelDownloader` 등).
3. **Application**: 전체 변환 워크플로우를 조율하는 `VideoToTextApp` 클래스.
4. **Presentation**: 사용자 인터페이스를 담당하는 Avalonia 프로젝트 (`Views`, `ViewModels`).

---

## 2. 개발 진행 상황 (Development Progress)

### 2.1 구현 완료된 기능
- [x] **다양한 포맷 지원**: MP4, MKV, MOV, AVI, MP3, WAV 등 비디오/오디오 파일 입력 처리.
- [x] **AI 모델 관리**: 
  - 로컬 모델(`Models/` 폴더) 자동 감지 및 리스트업.
  - 누락된 모델에 대한 **수동/자동 다운로드** 및 실시간 진행률 표시.
- [x] **고성능 변환**: GPU 가속 지원 및 대규모 파일 처리를 위한 20분 단위 청크 분할 처리.
- [x] **사용자 편의 기능**:
  - **남은 시간(ETR) 예측**: 처리 속도 기반 실시간 예상 완료 시간 표시.
  - **로그 시스템**: 실시간 작업 상태 및 결과 텍스트 출력.
  - **출력 경로 설정**: 자막 및 텍스트 파일 저장 위치 지정 가능.
- [x] **자막 내보내기**: SRT(자막) 및 TXT(순수 텍스트) 형식 동시 저장.

### 2.2 최근 작업 사항
- **[2026-04-03] 배포 최적화 및 아키텍처 리팩토링**:
  - **배포 패키지 대폭 축소**: 배포 파일 수 284개→241개, 용량 597MB→361MB (40% 감소).
  - **크로스 플랫폼 런타임 자동 정리**: 빌드 시 Linux/macOS/ARM 네이티브 라이브러리(.so, .dylib) 자동 삭제하여 Windows 전용 패키지 생성.
  - **DI 컨테이너 도입**: `Microsoft.Extensions.DependencyInjection`을 사용한 서비스 등록 및 생명주기 관리(`ServiceConfigurator`).
  - **FFmpeg 경로 자동 설정**: 실행 파일 기준 `ffmpeg/` 폴더를 자동 탐색하여 `Xabe.FFmpeg`에 경로 등록 (`ServiceConfigurator.SetupFFmpegPath()`).
  - **중복 초기화 제거**: FFmpeg 경로 설정을 `ServiceConfigurator` 단일 진입점으로 통합 (ViewModel의 `InitializeFFmpeg()` 제거).
  - **불필요 패키지 제거**: `Whisper.net.Runtime.Metal`(macOS 전용) 삭제.
- **[2026-04-03 이전] 배포 안정화 및 기능 고도화**:
  - **Native Library 배포 최적화**: `whisper.dll` 및 `ggml-*.dll`이 실행 파일 외부에 물리적으로 존재하도록 `.csproj` 설정을 통해 강제 노출.
  - **ETA(예상 완료 시간) 계산 고도화**: 단순 선형 추정에서 처리 속도 기반 실시간 예측으로 개선.
  - **로그 복사 기능**: UI 하단 로그 리스트를 전체 복사할 수 있는 버튼 추가 및 클립보드 연동.

---

## 3. 현안 및 해결 (Known Issues & Fixes)

### 3.1 [해결됨] 애플리케이션 시작 시 크래시 (Startup Crash)
- **현상**: 실행 파일 및 디버그 모드에서 프로그램이 즉시 종료됨.
- **원인**: 네이티브 라이브러리(`whisper.dll`) 경로 로딩 문제 및 XAML 파싱 오류.
- **해결**: `.csproj`에 MSBuild 타겟을 추가하여 네이티브 라이브러리를 실행 파일 루트로 강제 복사.

### 3.2 [해결됨] FFmpeg PATH 미설정 오류
- **현상**: `Cannot find FFmpeg in PATH` 오류로 미디어 정보 분석 및 오디오 추출 실패.
- **원인**: `Xabe.FFmpeg` 라이브러리에 실행 파일 경로가 등록되지 않음.
- **해결**: `ServiceConfigurator`에서 앱 시작 시 `ffmpeg/` 폴더를 자동 탐색하여 `FFmpeg.SetExecutablesPath()` 호출.

### 3.3 [검증 중] Whisper Native Library 인식 오류
- **현상**: `Native Library not found in default paths` 오류로 음성 인식 실패.
- **원인**: `PublishSingleFile` 사용 시 네이티브 DLL이 exe에 번들링되어 Whisper.net이 탐색 불가.
- **해결**: `PublishSingleFile` 제거, `runtimes/win-x64` 및 `runtimes/cuda` 폴더 구조를 유지한 채 비-Windows 런타임만 선택적 삭제.

---

## 4. 주의 사항 및 가이드 (Precautions)

### 4.1 바이너리 배포 및 경로
- **FFmpeg**: 프로젝트 소스의 `VideoToText.Avalonia/ffmpeg/bin/` 폴더에 바이너리가 포함되어 있으며, 빌드 시 자동으로 출력 폴더의 `ffmpeg/` 하위로 복사됩니다.
- **배포 경로**: `Publish/Win64v3/` 폴더 전체를 복사하면 .NET 런타임 설치 없이 실행 가능 (self-contained).
- **배포 파일 구조**:
  ```
  Publish/Win64v3/
  ├── VideoToText.Avalonia.exe  (메인 실행 파일)
  ├── *.dll                     (런타임 및 라이브러리)
  ├── ffmpeg/                   (FFmpeg 바이너리)
  │   ├── ffmpeg.exe
  │   └── ffprobe.exe
  └── runtimes/                 (네이티브 라이브러리)
      ├── win-x64/
      └── cuda/win-x64/
  ```

### 3.2 GPU 가속 요구 사항
- **Windows (CUDA)**: 사용자의 시스템에 최신 NVIDIA 드라이버가 설치되어 있어야 합니다.
- **Mac (Metal)**: 별도의 설정 없이 Metal API를 통해 하드웨어 가속이 작동합니다.

### 3.3 AI 모델 파일
- 모델 파일은 실행 파일 경로 하위의 `Models/` 폴더에 저장됩니다.
- 모델 파일명 형식은 `ggml-[size].bin`을 권장하며, UI의 모델 레지스트리에 등록된 파일은 온라인에서 즉시 다운로드 가능합니다.

### 3.4 런타임 성능
- 저사양 환경에서는 `tiny` 또는 `base` 모델 사용을 권장하며, 고품질 결과가 필요한 경우 `large-v3-turbo` 모델을 권장합니다.
- 긴 영상 처리 시 메모리 점유율을 모니터링하며 청크 단위로 작업이 수행되도록 설계되었습니다.

---

## 5. 향후 과제 (Roadmap)
- [x] ~~VContainer를 활용한~~ DI 컨테이너 도입 (Microsoft.Extensions.DependencyInjection 기반 완료).
- [ ] 다국어 UI(i18n) 지원.
- [ ] 자막 싱크 조정 및 미리보기 기능 추가.
- [ ] 배포 패키지 추가 최적화 (PublishSingleFile + 네이티브 라이브러리 호환성 연구).

---
**문서 버전**: 1.2 (최종 수정일: 2026-04-03)
**담당 업무**: 시스템 사양 명세 및 개발 진척 관리
