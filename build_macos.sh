#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/VideoToText.Avalonia/Publish"
APP_NAME="VideoTexter"
FINAL_APP="$PUBLISH_DIR/$APP_NAME.app"
FINAL_ZIP="$PUBLISH_DIR/${APP_NAME}_Standalone_osx-arm64.zip"

for tool in dotnet python3 ffmpeg ffprobe otool install_name_tool codesign zip unzip; do
    command -v "$tool" >/dev/null 2>&1 || { echo "필수 도구를 찾을 수 없습니다: $tool" >&2; exit 1; }
done

mkdir -p "$PUBLISH_DIR"
WORK_DIR="$(mktemp -d "$PUBLISH_DIR/.build.XXXXXX")"
WORK_PUBLISH="$WORK_DIR/publish"
WORK_APP="$WORK_DIR/$APP_NAME.app"
WORK_ZIP="$WORK_DIR/${APP_NAME}_Standalone_osx-arm64.zip"
MACOS_DIR="$WORK_APP/Contents/MacOS"
RESOURCES_DIR="$WORK_APP/Contents/Resources"
PREVIOUS_DIR=""
PROMOTION_STARTED=0

restore_previous() {
    local failed=0
    if [ -e "$PREVIOUS_DIR/$APP_NAME.app" ]; then
        rm -rf "$FINAL_APP" || failed=1
        mv "$PREVIOUS_DIR/$APP_NAME.app" "$FINAL_APP" || failed=1
    fi
    if [ -e "$PREVIOUS_DIR/${APP_NAME}_Standalone_osx-arm64.zip" ]; then
        rm -rf "$FINAL_ZIP" || failed=1
        mv "$PREVIOUS_DIR/${APP_NAME}_Standalone_osx-arm64.zip" "$FINAL_ZIP" || failed=1
    fi
    return "$failed"
}

cleanup_work_dir() {
    local status=$?
    trap - EXIT HUP INT TERM
    if [ "${PROMOTION_STARTED:-0}" -eq 1 ] && [ -n "${PREVIOUS_DIR:-}" ] && [ -d "$PREVIOUS_DIR" ]; then
        if ! restore_previous; then
            echo "기존 산출물 복구에 실패했습니다. 백업 보존 경로: $PREVIOUS_DIR" >&2
            exit "$status"
        fi
    fi
    rm -rf "$WORK_DIR"
    exit "$status"
}

trap cleanup_work_dir EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

echo "=== [1/7] .NET 프로젝트 게시 (osx-arm64) ==="
( cd "$SCRIPT_DIR"; dotnet publish VideoToText.Avalonia/VideoToText.Avalonia.csproj -c Release -r osx-arm64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=true -o "$WORK_PUBLISH" )

echo "=== [2/7] 앱 번들 구조 생성 ==="
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"
cp -R "$WORK_PUBLISH"/. "$MACOS_DIR/"
if [ -f "$SCRIPT_DIR/VideoToText.Avalonia/Info.plist" ]; then
    cp "$SCRIPT_DIR/VideoToText.Avalonia/Info.plist" "$WORK_APP/Contents/Info.plist"
else
    cat > "$WORK_APP/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
    <key>CFBundleExecutable</key><string>VideoToText.Avalonia</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundleIdentifier</key><string>com.woodenshield.videotexter</string>
    <key>CFBundleName</key><string>VideoTexter</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0.0</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSMicrophoneUsageDescription</key><string>음성 녹음 파일을 만들고 텍스트로 변환하기 위해 마이크를 사용합니다.</string>
</dict></plist>
EOF
fi

echo "=== [3/7] FFmpeg 번들링 ==="
python3 "$SCRIPT_DIR/bundle_ffmpeg.py" \
    --app-bundle "$WORK_APP" \
    --ffmpeg "$(command -v ffmpeg)" \
    --ffprobe "$(command -v ffprobe)"

echo "=== [4/7] Whisper arm64 런타임 임베딩 ==="
WHISPER_RUNTIME="$(find "$WORK_PUBLISH" -type d -path '*/runtimes/macos-arm64' -print -quit)"
if [ -z "$WHISPER_RUNTIME" ]; then
    echo "Whisper macOS arm64 런타임을 찾을 수 없습니다." >&2
    exit 1
fi
mkdir -p "$MACOS_DIR/runtimes"
cp -R "$WHISPER_RUNTIME" "$MACOS_DIR/runtimes/"
find "$MACOS_DIR/runtimes" -mindepth 1 -maxdepth 1 ! -name macos-arm64 -exec rm -rf {} +
[ ! -f "$MACOS_DIR/ggml-metal.metal" ] || mv "$MACOS_DIR/ggml-metal.metal" "$RESOURCES_DIR/"
find "$WORK_APP" -name '*.pdb' -delete

echo "=== [5/7] 번들 검증 및 서명 ==="
MAIN_EXECUTABLE="$MACOS_DIR/VideoToText.Avalonia"
for required in "$MAIN_EXECUTABLE" "$MACOS_DIR/ffmpeg" "$MACOS_DIR/ffprobe"; do
    [ -x "$required" ] || { echo "필수 실행 파일이 없습니다: $required" >&2; exit 1; }
done
find "$MACOS_DIR/runtimes/macos-arm64" -type f -name '*.dylib' -print -quit | grep -q . || { echo "Whisper macOS arm64 네이티브 라이브러리가 없습니다." >&2; exit 1; }
while IFS= read -r -d '' binary; do
    otool_output="$(otool -L "$binary")"
    grep -E '/opt/homebrew/|/usr/local/' <<< "$otool_output" >/dev/null && { echo "외부 라이브러리 경로가 남아 있습니다: $binary" >&2; exit 1; }
done < <(find "$MACOS_DIR" -type f \( -name 'ffmpeg' -o -name 'ffprobe' -o -path "$MACOS_DIR/libs/*.dylib" \) -print0)
while IFS= read -r -d '' binary; do
    codesign --force --sign - "$binary"
done < <(find "$MACOS_DIR" -type f \( -name '*.dylib' -o -perm -111 \) -print0)
codesign --force --deep --sign - "$WORK_APP"
codesign --verify --deep --strict --verbose=2 "$WORK_APP"
"$MACOS_DIR/ffmpeg" -version >/dev/null
"$MACOS_DIR/ffprobe" -version >/dev/null
echo "ad-hoc 서명 검증 완료; Apple Developer 서명 및 공증은 미수행"

echo "=== [6/7] 아카이브 검증 ==="
( cd "$WORK_DIR"; zip -qry "$WORK_ZIP" "$APP_NAME.app" )
UNPACKED_DIR="$WORK_DIR/unpacked"
unzip -q "$WORK_ZIP" -d "$UNPACKED_DIR"
UNPACKED_APP="$UNPACKED_DIR/$APP_NAME.app"
for required in "$UNPACKED_APP/Contents/MacOS/VideoToText.Avalonia" "$UNPACKED_APP/Contents/MacOS/ffmpeg" "$UNPACKED_APP/Contents/MacOS/ffprobe"; do
    [ -x "$required" ] || { echo "압축 해제본의 필수 실행 파일이 없습니다: $required" >&2; exit 1; }
done
codesign --verify --deep --strict --verbose=2 "$UNPACKED_APP"

echo "=== [7/7] 검증된 산출물 승격 ==="
PREVIOUS_DIR="$WORK_DIR/previous"
mkdir -p "$PREVIOUS_DIR"
PROMOTION_STARTED=1

if [ -e "$FINAL_APP" ] && ! mv "$FINAL_APP" "$PREVIOUS_DIR/"; then
    echo "기존 앱 산출물 백업에 실패했습니다." >&2
    exit 1
fi
if [ -e "$FINAL_ZIP" ] && ! mv "$FINAL_ZIP" "$PREVIOUS_DIR/"; then
    restore_previous || echo "기존 산출물 복구에 실패했습니다." >&2
    echo "기존 ZIP 산출물 백업에 실패하여 이전 산출물을 복구했습니다." >&2
    exit 1
fi
if ! mv "$WORK_APP" "$FINAL_APP" || ! mv "$WORK_ZIP" "$FINAL_ZIP"; then
    rm -rf "$FINAL_APP" "$FINAL_ZIP"
    restore_previous || echo "기존 산출물 복구에 실패했습니다." >&2
    echo "산출물 승격에 실패하여 이전 산출물을 복구했습니다." >&2
    exit 1
fi
trap - EXIT
rm -rf "$WORK_DIR"
echo "빌드 완료: $FINAL_ZIP"
