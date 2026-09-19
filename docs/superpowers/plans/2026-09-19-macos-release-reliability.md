# VideoTexter macOS Release Reliability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 실패한 macOS 패키징을 성공으로 보고하지 않고 기존 배포본을 보존하며, 현재 앱 동작과 문서를 일치시킨다.

**Architecture:** `build_macos.sh`가 고유 임시 디렉터리에서 publish부터 ZIP 검증까지 조율하고, `bundle_ffmpeg.py`는 전달받은 앱 번들에 FFmpeg와 비시스템 dylib를 복사·재작성한다. 모든 검증이 성공한 뒤에만 최종 앱과 ZIP을 승격하며, 문서는 자동·수동·미실행 증거를 분리한다.

**Tech Stack:** Bash 3.2+, Python 3 표준 라이브러리, .NET 8, Avalonia 11.2.3, macOS `otool`/`install_name_tool`/`codesign`, FFmpeg/ffprobe

## Global Constraints

- 기준 체크아웃은 `/Users/woodenshield/Desktop/UNITY/Projects/Personal/VideoToTexter`, 브랜치는 `main`, 기준 커밋은 `1bc91a2`다.
- 현재 미커밋 1~4단계 변경을 보존한다. `git checkout`, `restore`, `reset`, `clean`, 커밋, 푸시는 금지한다.
- 새 패키지와 테스트 프레임워크를 추가하지 않는다. Bash, Python 표준 라이브러리, 설치된 macOS 도구만 사용한다.
- Windows, Apple Developer 서명·공증 자동화, GPU 최적화, 실제 한국어 음성 품질 개선은 범위 밖이다.
- ad-hoc 서명은 개발용 무결성 검사로만 표시하고 공증 완료로 표현하지 않는다.
- 구현자는 모두 `gpt-5.6-terra`, reasoning `low`로 실행한다.
- 작업 A, B, C는 서로 다른 파일만 수정하며 병렬 실행한다. 통합 검증은 세 작업이 모두 끝난 뒤 한 번 수행한다.

## File Map

- Modify: `build_macos.sh` — 사전 점검, 임시 빌드, 패키지 검사, 안전한 승격
- Modify: `bundle_ffmpeg.py` — 전달받은 경로의 FFmpeg/ffprobe와 dylib 번들링
- Modify: `Doc/FEATURE_SPEC.md` — 모델 위치와 실제 배포 구성
- Modify: `Doc/ARCHITECTURE.md` — 큐·변환·모델 다운로드 흐름
- Modify: `Doc/PROJECT_STATUS.md` — 검증된 상태와 미증명 상태 분리
- Delete: `VideoToText.Checks/Program.cs.patch` — 실행·참조되지 않는 중간 패치 파일

## Parallel Dispatch

| Worker | Task | Write scope | Depends on |
|---|---|---|---|
| A | Task 1 | `bundle_ffmpeg.py` only | fixed CLI contract |
| B | Task 2 | `build_macos.sh` only | fixed CLI contract |
| C | Task 3 | `Doc/*.md`, `VideoToText.Checks/Program.cs.patch` only | current source |
| Reviewer | Task 4 | no source edits unless rejecting a worker | A+B+C complete |

Fixed CLI contract shared by A and B:

```text
python3 bundle_ffmpeg.py \
  --app-bundle <absolute VideoTexter.app path> \
  --ffmpeg <absolute ffmpeg path> \
  --ffprobe <absolute ffprobe path>
```

---

### Task 1: Make FFmpeg bundling fail closed

**Files:**
- Modify: `bundle_ffmpeg.py:1-96`

**Interfaces:**
- Consumes: `--app-bundle`, `--ffmpeg`, `--ffprobe` absolute paths
- Produces: `Contents/MacOS/ffmpeg`, `Contents/MacOS/ffprobe`, `Contents/MacOS/libs/*.dylib`; exit `0` only when all copy and rewrite operations succeed

- [ ] **Step 1: Run the pre-change failure probe**

Run:

```bash
python3 - <<'PY'
from pathlib import Path
import bundle_ffmpeg

assert not hasattr(bundle_ffmpeg, "parse_args"), "pre-change probe no longer applies"
assert 'subprocess.call(' in Path('bundle_ffmpeg.py').read_text()
print('RED: CLI contract and checked install_name_tool calls are absent')
PY
```

Expected: prints `RED: CLI contract and checked install_name_tool calls are absent`.

- [ ] **Step 2: Add the exact CLI and path validation**

Replace global project/output path derivation with `argparse`:

```python
from argparse import ArgumentParser
from pathlib import Path

def parse_args():
    parser = ArgumentParser()
    parser.add_argument("--app-bundle", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--ffprobe", type=Path, required=True)
    return parser.parse_args()

def require_file(path: Path) -> Path:
    resolved = path.expanduser().resolve()
    if not resolved.is_file():
        raise FileNotFoundError(f"필수 파일을 찾을 수 없습니다: {resolved}")
    return resolved
```

`main()`은 `app_bundle / "Contents" / "MacOS"`가 존재하지 않으면 실패하고, 두 바이너리는 `require_file`을 통과한 뒤에만 복사한다.

- [ ] **Step 3: Make every external tool failure fatal**

Keep `subprocess.check_output(["otool", "-L", str(path)], text=True)` for dependency reads. Replace every `subprocess.call` with:

```python
subprocess.run(
    ["install_name_tool", "-change", dependency, replacement, str(binary_path)],
    check=True,
)
```

For copied dylibs, set the id with the same checked pattern:

```python
subprocess.run(
    ["install_name_tool", "-id", f"@executable_path/libs/{library.name}", str(destination)],
    check=True,
)
```

Do not catch and downgrade `OSError` or `CalledProcessError`. Only `/usr/lib/` and `/System/Library/` dependencies are excluded. A non-system dependency that is not an existing absolute file raises `FileNotFoundError` with the parent binary and dependency in the message.

- [ ] **Step 4: Preserve recursive dependency traversal without duplicate work**

Use `collections.deque[Path]` and a `set[Path]` of resolved source paths. Copy each dylib once into `Contents/MacOS/libs`; if two different sources have the same basename, raise `RuntimeError` instead of overwriting one.

After copying all libraries, call the checked path-rewrite function for both executables and every copied dylib. Print only one final success line after all operations complete.

- [ ] **Step 5: Run focused GREEN checks**

Run:

```bash
python3 -m py_compile bundle_ffmpeg.py
tmp_dir="$(mktemp -d)"
mkdir -p "$tmp_dir/VideoTexter.app/Contents/MacOS"
python3 bundle_ffmpeg.py --app-bundle "$tmp_dir/VideoTexter.app" --ffmpeg /missing/ffmpeg --ffprobe /missing/ffprobe
test $? -ne 0
```

Expected: syntax check passes; the invocation exits nonzero and names `/missing/ffmpeg`. Do not run a success bundle here; Task 4 owns the single full copy.

- [ ] **Step 6: Record the worker handoff without committing**

Run:

```bash
git diff --check -- bundle_ffmpeg.py
git diff -- bundle_ffmpeg.py
```

Expected: no whitespace errors; diff is limited to `bundle_ffmpeg.py`.

### Task 2: Build and promote release artifacts safely

**Files:**
- Modify: `build_macos.sh:1-110`

**Interfaces:**
- Consumes: Task 1 CLI contract; installed `dotnet`, `python3`, `ffmpeg`, `ffprobe`, `otool`, `install_name_tool`, `codesign`, `zip`, `unzip`
- Produces: `VideoToText.Avalonia/Publish/VideoTexter.app` and `VideoTexter_Standalone_osx-arm64.zip` only after all validation succeeds

- [ ] **Step 1: Verify the unsafe pre-change contract without executing it**

Run:

```bash
python3 - <<'PY'
from pathlib import Path
s = Path('build_macos.sh').read_text()
assert 'set -euo pipefail' not in s
assert 'PROJECT_DIR=$(pwd)' in s
assert 'rm -rf "$PUBLISH_DIR"' in s
print('RED: failure propagation, location independence, and output preservation are absent')
PY
```

Expected: prints the `RED` line. Do not execute the old script because it deletes `Publish` before validation.

- [ ] **Step 2: Add strict mode, location independence, and preflight**

Start the script with:

```bash
#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/VideoToText.Avalonia/Publish"
APP_NAME="VideoTexter"
FINAL_APP="$PUBLISH_DIR/$APP_NAME.app"
FINAL_ZIP="$PUBLISH_DIR/${APP_NAME}_Standalone_osx-arm64.zip"

for tool in dotnet python3 ffmpeg ffprobe otool install_name_tool codesign zip unzip; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "필수 도구를 찾을 수 없습니다: $tool" >&2
        exit 1
    }
done
```

All project paths derive from `SCRIPT_DIR`. Remove `~/.dotnet/dotnet`, `/opt/homebrew/bin`, same-path `mv`, and hard-coded current working directory assumptions.

- [ ] **Step 3: Build only inside a unique work directory**

Create `PUBLISH_DIR`, then:

```bash
WORK_DIR="$(mktemp -d "$PUBLISH_DIR/.build.XXXXXX")"
trap 'rm -rf "$WORK_DIR"' EXIT
WORK_PUBLISH="$WORK_DIR/publish"
WORK_APP="$WORK_DIR/$APP_NAME.app"
WORK_ZIP="$WORK_DIR/${APP_NAME}_Standalone_osx-arm64.zip"
```

Run `dotnet publish` from `SCRIPT_DIR` with the existing Release, `osx-arm64`, self-contained, ReadyToRun, and single-file options, but output to `WORK_PUBLISH`. Build the bundle entirely under `WORK_APP`.

Generate the fallback `Info.plist` with bundle id `com.woodenshield.videotexter`, matching `Doc/FEATURE_SPEC.md`. Do not create an empty `Contents/MacOS/Models` directory because current models live under `LocalApplicationData`.

- [ ] **Step 4: Delegate FFmpeg copying through the fixed CLI**

Invoke exactly:

```bash
python3 "$SCRIPT_DIR/bundle_ffmpeg.py" \
    --app-bundle "$WORK_APP" \
    --ffmpeg "$(command -v ffmpeg)" \
    --ffprobe "$(command -v ffprobe)"
```

Whisper arm64 runtime absence is fatal. Do not copy the macOS x64 runtime into the arm64 package. Copy `ggml-metal.metal` into `Contents/Resources` when publish produced it.

- [ ] **Step 5: Validate before packaging**

Before ZIP creation, require executable app, ffmpeg, ffprobe, and at least one Whisper macOS arm64 native library. Run:

```bash
"$WORK_APP/Contents/MacOS/ffmpeg" -version >/dev/null
"$WORK_APP/Contents/MacOS/ffprobe" -version >/dev/null
```

Run `otool -L` over `ffmpeg`, `ffprobe`, and `Contents/MacOS/libs/*.dylib`; fail if output contains `/opt/homebrew/` or `/usr/local/`.

Sign dylibs and executables first, then the app bundle. Verify with:

```bash
codesign --verify --deep --strict --verbose=2 "$WORK_APP"
```

Print `ad-hoc 서명 검증 완료; Apple Developer 서명 및 공증은 미수행` after verification.

- [ ] **Step 6: Verify the archive, then promote with rollback**

Create `WORK_ZIP` from `WORK_DIR`. Extract it to `WORK_DIR/unpacked` and re-check app executable, ffmpeg, ffprobe, and `codesign --verify` on the extracted app.

Only now promote. Move any existing final app and ZIP to `$WORK_DIR/previous/`, move the new outputs into `PUBLISH_DIR`, and restore the previous outputs if either move fails. Clear the rollback trap only after both new outputs exist. Print the final success message last.

- [ ] **Step 7: Run non-destructive focused checks**

Run:

```bash
bash -n build_macos.sh
python3 - <<'PY'
from pathlib import Path
s = Path('build_macos.sh').read_text()
assert 'set -euo pipefail' in s
assert 'PROJECT_DIR=$(pwd)' not in s
assert 'rm -rf "$PUBLISH_DIR"' not in s
assert 'com.woodenshield.videotexter' in s
assert '--app-bundle "$WORK_APP"' in s
print('PASS: build safety contract')
PY
git diff --check -- build_macos.sh
```

Expected: syntax and contract checks pass. Task 4 owns the single full package build.

- [ ] **Step 8: Record the worker handoff without committing**

Run `git diff -- build_macos.sh` and confirm no other file was modified.

### Task 3: Align documentation and remove the stray patch artifact

**Files:**
- Modify: `Doc/FEATURE_SPEC.md`
- Modify: `Doc/ARCHITECTURE.md`
- Modify: `Doc/PROJECT_STATUS.md`
- Delete: `VideoToText.Checks/Program.cs.patch`

**Interfaces:**
- Consumes: current production source and the verified 17-check result
- Produces: documentation that separates implemented, automatically verified, manually unverified, and out-of-scope states

- [ ] **Step 1: Confirm the patch file is unused before deletion**

Run:

```bash
rg -n 'Program\.cs\.patch' . -g '!VideoToText.Checks/Program.cs.patch' || true
diff -q VideoToText.Checks/Program.cs VideoToText.Checks/Program.cs.patch || true
```

Expected: no repository reference; files differ. Delete only `VideoToText.Checks/Program.cs.patch` with `apply_patch`.

- [ ] **Step 2: Update the feature specification**

In `Doc/FEATURE_SPEC.md`, replace these obsolete claims:

- models bundled under `Contents/MacOS/Models`
- conversion starts directly without the queue/start gate
- every dependency including models is inside the app

State instead:

- FFmpeg and Whisper native runtime are bundled; downloaded GGUF models live under `LocalApplicationData/VideoTexter/Models`
- the app reads legacy bundle models but does not write new models there
- jobs capture model/output at registration, run sequentially after Start, and Pause applies before the next job
- existing `.srt`/`.txt` files are preserved and cause that job to fail visibly

- [ ] **Step 3: Update architecture and status documents**

In `Doc/ARCHITECTURE.md`, add `JobQueueManager` between the view model and `VideoToTextApp`; show `.part` download, byte-length verification, atomic move, primary/legacy model resolution, unique OS-temp WAV, and SRT/TXT exporter failure propagation.

In `Doc/PROJECT_STATUS.md`, use these exact evidence labels:

```text
완료        implementation and current automatic check passed
미증명      implemented but representative runtime/manual evidence absent
미착수      implementation not started
not_run     verification was not executed in this checkout
```

Record Checks `17/17`, three builds with zero warnings/errors, and `git diff --check` as automatic evidence. Mark actual Korean Whisper media, Avalonia manual UI, Homebrew-free independent launch, Developer ID signing, and notarization as `not_run` or `미증명` as applicable.

- [ ] **Step 4: Verify documentation assertions against source**

Run:

```bash
rg -n 'LocalApplicationData|\.part|순차|일시정지|17/17|not_run|미증명|공증' Doc/*.md
rg -n 'Contents/MacOS/Models|모든 의존성.*앱.*포함|패키징.*완료' Doc/*.md
git diff --check -- Doc VideoToText.Checks/Program.cs.patch
```

Expected: first search finds the new facts; second search has no unconditional obsolete claims; diff check passes.

- [ ] **Step 5: Record the worker handoff without committing**

Run `git status --short` and confirm this worker changed only the three docs and removed the patch artifact.

### Task 4: Integrate and independently verify the release path

**Files:**
- Review: all Task 1-3 files
- Verify: existing `VideoToText`, `VideoToText.Avalonia`, `VideoToText.Checks`

**Interfaces:**
- Consumes: outputs from workers A, B, C
- Produces: an evidence report; no source edits unless a worker is rejected with exact file/line evidence

- [ ] **Step 1: Check worker scope and shared contract**

Run:

```bash
git status --short
git diff --check
rg -n -- '--app-bundle|--ffmpeg|--ffprobe' build_macos.sh bundle_ffmpeg.py
bash -n build_macos.sh
python3 -m py_compile bundle_ffmpeg.py
```

Expected: the same three CLI flags exist in caller and callee; syntax and whitespace checks pass.

- [ ] **Step 2: Re-run all application verification**

Run:

```bash
dotnet build VideoToText/VideoToText.csproj --no-restore
dotnet build VideoToText.Avalonia/VideoToText.Avalonia.csproj --no-restore
dotnet build VideoToText.Checks/VideoToText.Checks.csproj --no-restore
dotnet run --project VideoToText.Checks/VideoToText.Checks.csproj --no-restore -- --require-ffmpeg
```

Expected: three builds report zero warnings/errors; Checks reports `성공 17건, 실패 0건, 건너뜀 0건`.

- [ ] **Step 3: Prove a failed build preserves existing output**

Create a disposable copy of the project with `cp -R`, put sentinel contents in its final ZIP and app path, and run its `build_macos.sh` with a restricted `PATH` that cannot find FFmpeg. Expected: nonzero exit, no success message, both sentinel outputs unchanged. Never run this failure injection against the real `Publish` directory.

- [ ] **Step 4: Run the single real package build**

Run from outside the repository root to prove location independence:

```bash
cd /tmp
/Users/woodenshield/Desktop/UNITY/Projects/Personal/VideoToTexter/build_macos.sh
```

Expected: one final app and ZIP are produced only after bundled FFmpeg/ffprobe, dylib references, extracted archive, and ad-hoc signature checks pass.

- [ ] **Step 5: Inspect final artifacts independently**

Run:

```bash
APP="/Users/woodenshield/Desktop/UNITY/Projects/Personal/VideoToTexter/VideoToText.Avalonia/Publish/VideoTexter.app"
ZIP="/Users/woodenshield/Desktop/UNITY/Projects/Personal/VideoToTexter/VideoToText.Avalonia/Publish/VideoTexter_Standalone_osx-arm64.zip"
test -x "$APP/Contents/MacOS/VideoToText.Avalonia"
"$APP/Contents/MacOS/ffmpeg" -version >/dev/null
"$APP/Contents/MacOS/ffprobe" -version >/dev/null
codesign --verify --deep --strict --verbose=2 "$APP"
unzip -t "$ZIP"
```

Expected: every command exits `0`. This proves local packaging integrity, not Homebrew-free launch, UI behavior, actual Whisper transcription quality, Developer ID signing, or notarization.

- [ ] **Step 6: Produce the acceptance report**

Report no more than five items:

1. build/check counts and exit codes
2. failed-build preservation result
3. app/ZIP paths and integrity result
4. documentation alignment result
5. `not_run` gates: Homebrew-free machine, UI manual flow, actual Korean Whisper, Developer ID/notarization

Do not commit or push. Leave the worktree ready for the user's review.
