# 비디오 텍스터 (Video Texter) — 프로젝트 진행 상황

> **최종 업데이트**: 2026-04-05  
> **버전**: 1.0.0  
> **플랫폼**: macOS (Apple Silicon / arm64)

---

## 1. 전체 진행 현황

| 항목 | 상태 | 비고 |
|------|------|------|
| **Core 라이브러리 (VideoToText)** | ✅ 완료 | 클린 아키텍처 기반 |
| **Avalonia UI 앱 (VideoToText.Avalonia)** | ✅ 완료 | MVVM 패턴 |
| **Whisper 모델 통합** | ✅ 완료 | tiny ~ large-v3-turbo (8종) |
| **모델 다운로드 시스템** | ✅ 완료 | HuggingFace 직접 다운로드 |
| **FFmpeg 내장화** | ✅ 완료 | 앱 번들 내 포함 (Standalone) |
| **macOS 앱 번들 패키징** | ✅ 완료 | `VideoTexter.app` |
| **SRT 자막 내보내기** | ✅ 완료 | `.srt` 형식 |
| **Plain Text 내보내기** | ✅ 완료 | `.txt` 형식 |
| **작업 완료 예상 시간 표시** | ✅ 완료 | 실시간 ETA |
| **로그 복사 기능** | ✅ 완료 | 클립보드 복사 |
| **모델 상태 실시간 업데이트** | ✅ 완료 | 다운로드 후 즉시 반영 |
| **앱 서명 / 공증** | ⬜ 미진행 | Apple Developer 계정 필요 |
| **Windows 빌드** | ⬜ 보류 | macOS 우선 완성 |

---

## 2. 최근 작업 이력 (Changelog)

### v1.0.0 (2026-04-05)
- ✅ 로그 복사 기능 추가 (`로그 복사` 버튼)
- ✅ 모델 다운로드 완료 시 UI 즉시 갱신 (`NotifyPropertyChangedFor` 적용)
- ✅ FFmpeg 엔진을 앱 번들 내부에 내장 (외부 의존성 제거)
- ✅ 앱 실행 불가 이슈 해결 (아이콘 리소스 참조 제거, Info.plist 복원)
- ✅ 모델 리스트 고도화 (tiny ~ large-v3-turbo, 다운로드 상태 표시)

### v0.9.0 (2026-04-01 ~ 04-02)
- ✅ Avalonia 기반 macOS 데스크톱 앱 초기 구현
- ✅ Whisper.net 기반 음성 인식(STT) 파이프라인 구축
- ✅ FFmpeg 기반 오디오 추출 엔진 구현
- ✅ 작업 완료 예상 시간(ETA) 표시 기능 추가

---

## 3. 알려진 이슈 및 향후 계획

| 우선순위 | 항목 | 설명 |
|----------|------|------|
| 높음 | 앱 서명 | 타인에게 배포 시 Apple Developer 계정으로 서명/공증 필요 |
| 중간 | 빌드 자동화 | `.csproj`에 FFmpeg 복사 스크립트(`AfterBuild`) 포함 |
| 중간 | Windows 빌드 | `win-x64` 타겟 빌드 및 패키징 |
| 낮음 | 다국어 지원 | Whisper 언어 매개변수 선택 UI |
| 낮음 | 드래그 앤 드롭 | 영상 파일 끌어놓기 지원 |
