#!/bin/bash

# [설명]: VideoTexter macOS 독립형 앱 빌드 및 자동 번들링 스크립트
# 의존성: dotnet SDK, python3, Homebrew(ffmpeg, ffprobe)

PROJECT_DIR=$(pwd)
PUBLISH_DIR="$PROJECT_DIR/VideoToText.Avalonia/Publish"
APP_NAME="VideoTexter"
APP_BUNDLE="$PUBLISH_DIR/$APP_NAME.app"
MACOS_DIR="$APP_BUNDLE/Contents/MacOS"
RESOURCES_DIR="$APP_BUNDLE/Contents/Resources"

echo "=== [1/8] 클린 빌드 준비 ==="
rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

echo "=== [2/8] .NET 프로젝트 게시 (osx-arm64) ==="
~/.dotnet/dotnet publish VideoToText.Avalonia/VideoToText.Avalonia.csproj \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -p:PublishReadyToRun=true \
    -p:PublishSingleFile=true \
    -o "$PUBLISH_DIR/osx-arm64-temp"

echo "=== [3/8] 앱 번들 구조 생성 ==="
mkdir -p "$MACOS_DIR"
mkdir -p "$RESOURCES_DIR"
cp -rf "$PUBLISH_DIR/osx-arm64-temp/"* "$MACOS_DIR/"
mv "$MACOS_DIR/VideoToText.Avalonia" "$MACOS_DIR/VideoToText.Avalonia" # 이름 유지

# Info.plist 복사 (없으면 기본 생성)
if [ -f "$PROJECT_DIR/VideoToText.Avalonia/Info.plist" ]; then
    cp "$PROJECT_DIR/VideoToText.Avalonia/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
else
    cat > "$APP_BUNDLE/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>VideoToText.Avalonia</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundleIdentifier</key>
    <string>com.videototexter.app</string>
    <key>CFBundleName</key>
    <string>VideoTexter</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
</dict>
</plist>
EOF
fi

echo "=== [4/8] FFmpeg 및 종속 라이브러리 번들링 ==="
python3 bundle_ffmpeg.py

echo "=== [5/8] Whisper 네이티브 라이브러리 (macOS) 임베딩 ==="
# bin 폴더에서 확실한 라이브러리 경로 탐색
NET_VER="net8.0" # 프로젝트 설정에 맞춰 자동 감지 시도 가능
WHISPER_RUNTIMES_DIR="$PROJECT_DIR/VideoToText.Avalonia/bin/Release/$NET_VER/osx-arm64/runtimes"

if [ -d "$WHISPER_RUNTIMES_DIR" ]; then
    mkdir -p "$MACOS_DIR/runtimes"
    cp -rf "$WHISPER_RUNTIMES_DIR/macos-arm64" "$MACOS_DIR/runtimes/"
    cp -rf "$WHISPER_RUNTIMES_DIR/macos-x64" "$MACOS_DIR/runtimes/"
    echo "Whisper runtimes copied successfully."
else
    echo "Warning: Whisper runtimes not found in $WHISPER_RUNTIMES_DIR"
fi

echo "=== [6/8] 불필요한 파일 제거 (클린 배포용) ==="
find "$APP_BUNDLE" -name "*.pdb" -delete
rm -rf "$MACOS_DIR/runtimes/linux-"*
rm -rf "$MACOS_DIR/runtimes/win-"*
rm -rf "$MACOS_DIR/Models"
mkdir -p "$MACOS_DIR/Models"

# 리소스 이동
if [ -f "$MACOS_DIR/ggml-metal.metal" ]; then
    mv "$MACOS_DIR/ggml-metal.metal" "$RESOURCES_DIR/"
fi

echo "=== [7/8] 코드 서명 (Ad-hoc) ==="
# 1. 개별 라이브러리 및 바이너리 서명
find "$MACOS_DIR" -type f \( -name "*.dylib" -o -name "ffmpeg" -o -name "ffprobe" -o -name "VideoToText.Avalonia" \) -exec codesign --force --options runtime --sign - {} \;

# 2. 리소스 내 쉐이더 파일(있을 경우) 서명 시도 (Gatekeeper 호환성)
if [ -f "$RESOURCES_DIR/ggml-metal.metal" ]; then
    codesign --force --sign - "$RESOURCES_DIR/ggml-metal.metal"
fi

# 3. 전체 앱 번들 서명
codesign --force --deep --sign - "$APP_BUNDLE"

echo "=== [8/8] 최종 검증 및 패키징 ==="
codesign -vvv --deep --strict "$APP_BUNDLE"

cd "$PUBLISH_DIR"
rm -f "${APP_NAME}_Standalone_osx-arm64.zip"
zip -r "${APP_NAME}_Standalone_osx-arm64.zip" "${APP_NAME}.app"

echo "========================================"
echo "빌드 완료: $PUBLISH_DIR/${APP_NAME}_Standalone_osx-arm64.zip"
echo "========================================"
