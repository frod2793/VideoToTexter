import os
import shutil
import subprocess
import re

# 설정
APP_NAME = "VideoTexter"
PROJECT_ROOT = os.getcwd()
APP_BUNDLE = os.path.join(PROJECT_ROOT, f"VideoToText.Avalonia/Publish/{APP_NAME}.app")
MACOS_DIR = os.path.join(APP_BUNDLE, "Contents/MacOS")
LIBS_DIR = os.path.join(MACOS_DIR, "libs")

def get_dependencies(path):
    """otool -L을 실행하여 의존성 목록을 가져옵니다."""
    try:
        output = subprocess.check_output(["otool", "-L", path]).decode("utf-8")
        deps = []
        for line in output.split("\n")[1:]:
            line = line.strip()
            if not line: continue
            match = re.match(r"(.*) \(compatibility.*", line)
            if match:
                dep_path = match.group(1).strip()
                # 시스템 라이브러리(/usr/lib, /System)는 제외
                if not dep_path.startswith("/usr/lib") and not dep_path.startswith("/System"):
                    deps.append(dep_path)
        return deps
    except Exception as e:
        print(f"Error getting deps for {path}: {e}")
        return []

def fix_paths(binary_path):
    """바이너리 내의 라이브러리 참조 경로를 수정합니다."""
    deps = get_dependencies(binary_path)
    for dep in deps:
        lib_name = os.path.basename(dep)
        new_path = f"@executable_path/libs/{lib_name}"
        subprocess.call(["install_name_tool", "-change", dep, new_path, binary_path])

def bundle_binary(src_path):
    """바이너리를 복사하고 의존성을 추적하여 복사합니다."""
    if not os.path.exists(src_path):
        print(f"Warning: Source not found: {src_path}")
        return

    bin_name = os.path.basename(src_path)
    target_path = os.path.join(MACOS_DIR, bin_name)
    print(f"Bundling {bin_name}...")
    shutil.copy2(src_path, target_path)
    os.chmod(target_path, 0o755)

    # 의존성 처리 (재귀)
    process_queue = get_dependencies(src_path)
    processed_libs = set()

    while process_queue:
        dep = process_queue.pop(0)
        lib_name = os.path.basename(dep)
        if lib_name in processed_libs: continue
        
        lib_src = dep
        # 심볼릭 링크 처리 (homebrew 특성)
        if not os.path.exists(lib_src):
            print(f"Trying to find real path for {lib_src}")
            # 추가적인 경로 탐색 로직 (필요시)
            
        lib_dest = os.path.join(LIBS_DIR, lib_name)
        print(f"  Copying lib: {lib_name}")
        shutil.copy2(lib_src, lib_dest)
        os.chmod(lib_dest, 0o644)
        
        # 라이브러리 자체의 id 수정
        subprocess.call(["install_name_tool", "-id", f"@executable_path/libs/{lib_name}", lib_dest])
        
        processed_libs.add(lib_name)
        # 이 라이브러리의 의존성도 추가
        new_deps = get_dependencies(lib_src)
        process_queue.extend(new_deps)

    # 모든 처리가 끝난 후 경로 수정
    fix_paths(target_path)
    for lib_name in processed_libs:
        fix_paths(os.path.join(LIBS_DIR, lib_name))

def main():
    if not os.path.exists(LIBS_DIR):
        os.makedirs(LIBS_DIR)

    # 1. FFmpeg & FFprobe 번들링
    bundle_binary("/opt/homebrew/bin/ffmpeg")
    bundle_binary("/opt/homebrew/bin/ffprobe")

    print("\nBundling complete.")

if __name__ == "__main__":
    main()
