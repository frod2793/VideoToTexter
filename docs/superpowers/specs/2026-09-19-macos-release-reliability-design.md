# VideoTexter macOS 배포 신뢰성 개선 설계

**상태:** 사용자 승인 완료
**기준:** `main` (`1bc91a2`)의 미커밋 1~4단계 변경 보존
**목표:** 실패한 패키징을 성공으로 보고하지 않고, 기존 배포본을 보존하며, 현재 앱 동작과 문서를 일치시킨다.

## 범위

이번 작업은 기존 개선 계획의 5단계만 수행한다.

- `build_macos.sh`의 실행 위치 의존, 기존 `Publish` 선삭제, 실패 무시를 제거한다.
- `bundle_ffmpeg.py`가 FFmpeg, ffprobe, `otool`, `install_name_tool` 실패를 즉시 전달하게 한다.
- 새 앱 번들의 필수 실행 파일, 동적 라이브러리 참조, 코드 서명을 검사한 뒤에만 ZIP을 최종 위치로 이동한다.
- `Doc/FEATURE_SPEC.md`, `Doc/ARCHITECTURE.md`, `Doc/PROJECT_STATUS.md`를 현재 큐·모델 저장소·검증 상태와 일치시킨다.
- `VideoToText.Checks/Program.cs.patch`는 실행·참조 근거가 없으면 제거 대상으로 검토한다.

Windows 배포, Apple Developer 서명·공증 자동화, GPU 최적화, 새 테스트 프레임워크는 제외한다. 실제 한국어 음성 품질과 UI 수동 검증은 기존처럼 별도 미증명 게이트로 유지한다.

## 설계

### 1. 안전한 산출물 승격

`build_macos.sh`는 스크립트 자신의 위치를 프로젝트 루트로 사용하고 `set -euo pipefail`로 첫 실패에서 중단한다. 기존 최종 ZIP과 앱 번들은 건드리지 않은 채 고유한 임시 빌드 디렉터리에서 publish·번들링·검증한다. 모든 검증이 통과한 경우에만 최종 산출물 이름으로 이동한다.

임시 디렉터리는 스크립트 종료 시 정리한다. 실패 시 기존 배포본은 그대로 남고 성공 문구와 새 최종 ZIP은 생성되지 않는다.

### 2. FFmpeg 번들링 실패 전파

`bundle_ffmpeg.py`는 출력 앱 번들 경로와 FFmpeg/ffprobe 경로를 명령행 인자로 받는다. 호출자는 `command -v`로 실제 도구 경로를 전달하며 `/opt/homebrew`를 고정하지 않는다.

`subprocess.run(..., check=True)`와 `subprocess.check_output(...)`을 사용해 의존성 조회와 경로 수정 실패를 즉시 반환한다. 시스템 라이브러리는 제외하고, 복사한 비시스템 라이브러리의 참조가 `@executable_path/libs`로 바뀌었는지 다시 검사한다.

### 3. 패키지 검증

ZIP 생성 전 다음을 모두 확인한다.

1. 앱 실행 파일, `ffmpeg`, `ffprobe`, Whisper macOS arm64 런타임이 존재한다.
2. 번들된 FFmpeg/ffprobe를 직접 실행해 버전 명령이 성공한다.
3. 비시스템 동적 라이브러리 참조가 앱 번들 밖의 Homebrew 절대 경로를 가리키지 않는다.
4. `codesign --verify --deep --strict`가 성공한다.
5. ZIP을 별도 임시 위치에 풀어 필수 파일이 유지되는지 확인한다.

ad-hoc 서명은 개발 배포 검증일 뿐 Apple Developer 서명·공증 증거가 아님을 출력과 문서에 명시한다.

### 4. 문서 정합성

문서는 다음 실제 동작을 기준으로 갱신한다.

- 작업은 등록 후 사용자가 시작하며 순차 처리된다.
- 일시정지는 현재 작업 완료 후 다음 작업 시작을 막는다.
- 모델은 `LocalApplicationData/VideoTexter/Models`에 저장하고 앱 번들의 `Models`는 레거시 읽기만 지원한다.
- 모델 다운로드는 `.part`와 Content-Length 검증을 사용한다.
- 자동 Checks 통과, 실제 한국어 Whisper 품질, UI 수동 검증, 독립 환경 실행을 서로 다른 상태로 표시한다.

## 병렬 실행 구조

구현자는 모두 `gpt-5.6-terra`, reasoning `low`를 사용한다.

- 작업 A: `build_macos.sh`와 실패 경로 검증
- 작업 B: `bundle_ffmpeg.py`와 동적 라이브러리 검증
- 작업 C: 세 문서의 현재 동작 반영

A와 B는 인터페이스를 먼저 고정한다: `bundle_ffmpeg.py --app-bundle <path> --ffmpeg <path> --ffprobe <path>`. 이후 서로 다른 파일에서 병렬 작업한다. C는 코드 변경 없이 현재 소스와 검증 결과만 사용한다. 통합 후 한 명의 검수자가 전체 빌드와 문서 표현을 대조한다.

## 완료 기준

- 기존 1~4단계 변경이 보존되고 `VideoToText.Checks` 17개가 계속 통과한다.
- 의도적으로 FFmpeg 경로를 깨뜨린 빌드는 실패하며 기존 최종 ZIP을 보존한다.
- 정상 패키징은 새 앱 번들과 ZIP을 만들고 필수 파일·의존성·ad-hoc 서명 검증을 통과한다.
- 문서에 미실행 검증을 완료로 표시하지 않는다.
- `git diff --check`가 통과하고 커밋·푸시는 별도 승인 전 수행하지 않는다.
