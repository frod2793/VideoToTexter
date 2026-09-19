# 비디오 텍스터 (Video Texter) — 프로젝트 진행 상황

> **최종 업데이트**: 2026-09-19
> **버전**: 1.0.0  
> **플랫폼**: macOS (Apple Silicon / arm64)

---

## 1. 증거 상태 기준

| 레이블 | 의미 |
|------|------|
| **완료** | implementation and current automatic check passed |
| **미증명** | implemented but representative runtime/manual evidence absent |
| **미착수** | implementation not started |
| **not_run** | verification was not executed in this checkout |

## 2. 현재 상태

| 항목 | 상태 | 현재 근거 |
|------|------|------|
| Core·Avalonia·Checks 빌드 | **완료** | 2026-09-19 `dotnet build` 3회 모두 경고 0, 오류 0 |
| 큐 Start/Pause·순차 실행·등록 스냅샷 | **완료** | `VideoToText.Checks` 9, 11, 14, 15번을 포함해 17/17 PASS |
| 모델 다운로드·primary/legacy 해석 | **완료** | Checks 17/17 PASS: `.part` 정리, 길이 검증, 기존 모델 보존 포함 |
| SRT/TXT 충돌 보존·실패 전파 | **완료** | Checks 1~8을 포함해 17/17 PASS |
| Korean Whisper 실제 미디어 전사 | **not_run** | 대표 한국어 미디어로 Whisper 결과를 실행하지 않음 |
| Avalonia 수동 UI 동작 | **not_run** | 실제 앱 창 조작·상태 표시를 검증하지 않음 |
| Homebrew 없는 독립 실행 | **not_run** | 배포 앱을 Homebrew 없는 환경에서 실행하지 않음 |
| Developer ID 서명 | **미증명** | 구현/서명 산출물의 현재 검증 근거 없음 |
| notarization(공증) | **not_run** | Apple 공증 제출·티켓 검증을 실행하지 않음 |
| 기본/선택 마이크 5초 녹음 | **not_run** | 실제 장치·권한이 필요한 녹음, M4A 재생, 대기열 등록을 실행하지 않음 |
| 권한 거부·녹음 중 장치 분리 | **not_run** | 실제 macOS 권한 및 장치 상태를 검증하지 않음 |
| 한국어 음성 품질 | **미증명** | 대표 한국어 음성으로 전사 품질을 확인하지 않음 |

## 3. 자동 검증 기록

- `dotnet run --project VideoToText.Checks/VideoToText.Checks.csproj`: 성공 17건, 실패 0건, SKIP 0건.
- `dotnet build` 3회(`VideoToText`, `VideoToText.Avalonia`, `VideoToText.Checks`): 각 경고 0개, 오류 0개.
- `git diff --check`: 문서와 현재 작업트리 diff에 공백 오류 없음.
- `dotnet run --project VideoToText.Checks/VideoToText.Checks.csproj --no-build -- --require-ffmpeg`: 22/22 PASS, FAIL 0, SKIP 0.

## 4. 음성 입력 구현 범위

- 기존 영상과 MP3/M4A/WAV 가져오기를 지원한다.
- 시스템 기본 또는 선택 마이크를 FFmpeg `avfoundation`으로 녹음하고, 사용자 선택 출력 폴더에 AAC 128k `녹음_yyyyMMdd_HHmmss.m4a`를 기존 파일 보존 방식으로 확정한다.
- 정상 중지 뒤 확정 파일만 기존 대기열에 등록하며 자동 변환은 시작하지 않는다. macOS 번들은 `NSMicrophoneUsageDescription`을 포함한다.
- 실시간 변환, 파형, 편집, 노이즈 제거는 미지원이다.
