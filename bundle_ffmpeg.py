from argparse import ArgumentParser
from collections import deque
from pathlib import Path
import re
import shutil
import subprocess


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


def get_dependencies(path: Path) -> list[str]:
    output = subprocess.check_output(["otool", "-L", str(path)], text=True)
    dependencies = []
    for line in output.splitlines()[1:]:
        match = re.match(r"(.*) \(compatibility.*", line.strip())
        if match:
            dependency = match.group(1).strip()
            if not dependency.startswith(("/usr/lib/", "/System/Library/")):
                dependencies.append(dependency)
    return dependencies


def require_dependency(binary_path: Path, dependency: str) -> Path:
    source = Path(dependency).expanduser()
    if not source.is_absolute() or not source.is_file():
        raise FileNotFoundError(
            f"의존성 파일을 찾을 수 없습니다: {binary_path} -> {dependency}"
        )
    return source.resolve()


def rewrite_paths(binary_path: Path) -> None:
    for dependency in get_dependencies(binary_path):
        replacement = f"@executable_path/libs/{Path(dependency).name}"
        subprocess.run(
            ["install_name_tool", "-change", dependency, replacement, str(binary_path)],
            check=True,
        )


def copy_binary(source: Path, macos_dir: Path) -> Path:
    destination = macos_dir / source.name
    shutil.copy2(source, destination)
    destination.chmod(0o755)
    return destination


def copy_dependencies(binaries: list[Path], libs_dir: Path) -> list[Path]:
    queue = deque(
        (binary, dependency)
        for binary in binaries
        for dependency in get_dependencies(binary)
    )
    expanded_sources: set[Path] = set()
    libraries: list[Path] = []
    names: dict[str, Path] = {}

    while queue:
        parent, dependency = queue.popleft()
        library = require_dependency(parent, dependency)
        library_name = Path(dependency).name

        existing_source = names.get(library_name)
        if existing_source is not None and existing_source != library:
            raise RuntimeError(
                f"동일한 라이브러리 이름이 충돌합니다: {existing_source} / {library}"
            )

        if existing_source is None:
            destination_library = libs_dir / library_name
            shutil.copy2(library, destination_library)
            destination_library.chmod(0o644)
            subprocess.run(
                [
                    "install_name_tool",
                    "-id",
                    f"@executable_path/libs/{library_name}",
                    str(destination_library),
                ],
                check=True,
            )
            names[library_name] = library
            libraries.append(destination_library)

        if library not in expanded_sources:
            expanded_sources.add(library)
            queue.extend((library, child) for child in get_dependencies(library))

    return libraries


def main() -> None:
    args = parse_args()
    app_bundle = args.app_bundle.expanduser().resolve()
    macos_dir = app_bundle / "Contents" / "MacOS"
    if not macos_dir.is_dir():
        raise FileNotFoundError(f"앱 번들의 MacOS 디렉터리를 찾을 수 없습니다: {macos_dir}")

    libs_dir = macos_dir / "libs"
    libs_dir.mkdir(exist_ok=True)
    binaries = [require_file(args.ffmpeg), require_file(args.ffprobe)]
    copied_binaries = [copy_binary(binary, macos_dir) for binary in binaries]
    copied_libraries = copy_dependencies(binaries, libs_dir)

    for binary in copied_binaries + copied_libraries:
        rewrite_paths(binary)
    print("FFmpeg 번들링 완료")


if __name__ == "__main__":
    main()
