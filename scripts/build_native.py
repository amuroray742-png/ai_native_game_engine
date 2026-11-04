"""Build the pinned Jolt bridge. No globally installed packages or generated source."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import importlib.util

ROOT = Path(__file__).resolve().parents[1]
JOLT_COMMIT = "e77f175595e64cb44218cc9d9d56fc365ad0e36a"


def run(args, **kwargs):
    subprocess.run([str(arg) for arg in args], check=True, **kwargs)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--jolt-source", type=Path, default=ROOT / "TestResults/native-deps/JoltPhysics")
    args = parser.parse_args()
    if platform.machine().lower() not in ("amd64", "x86_64"):
        raise RuntimeError("This bridge build currently supports x64 only")
    system = platform.system()
    if system not in ("Windows", "Linux"):
        raise RuntimeError("This build currently supports Windows and Linux")
    rid = "win-x64" if system == "Windows" else "linux-x64"
    source = args.jolt_source.resolve()
    if not source.exists():
        source.parent.mkdir(parents=True, exist_ok=True)
        run(["git", "clone", "--depth", "1", "--branch", "v5.6.0",
             "https://github.com/jrouwe/JoltPhysics.git", source])
    commit = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if commit != JOLT_COMMIT:
        raise RuntimeError("Jolt source must match the pinned commit")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Jolt source has local changes")
    environment = os.environ.copy()
    cmake = shutil.which("cmake")
    ninja = shutil.which("ninja")
    if system == "Windows":
        locator = Path(os.environ["ProgramFiles(x86)"]) / "Microsoft Visual Studio/Installer/vswhere.exe"
        install = subprocess.check_output([str(locator), "-latest", "-products", "*", "-requires",
                                          "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], text=True).strip()
        if not install:
            raise RuntimeError("MSVC x64 toolchain is required")
        visual_studio = Path(install)
        cmake = cmake or str(visual_studio / "Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe")
        ninja = ninja or str(visual_studio / "Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe")
        setup = visual_studio / "Common7/Tools/VsDevCmd.bat"
        output = subprocess.check_output(
            f'cmd.exe /d /s /c ""{setup}" -arch=x64 -host_arch=x64 >nul && set"', text=True)
        for line in output.splitlines():
            if "=" in line and not line.startswith("="):
                key, value = line.split("=", 1)
                environment[key] = value
    if not cmake or not ninja:
        raise RuntimeError("CMake and Ninja are required")
    build = ROOT / "artifacts/native-build" / rid
    destination = ROOT / "artifacts/native" / rid
    destination.mkdir(parents=True, exist_ok=True)
    patch_path = ROOT / "native/jolt/prepare_source.py"
    spec = importlib.util.spec_from_file_location("jolt_restore_patch", patch_path)
    patch = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(patch)
    patched_body_manager = build / "patched/BodyManager.cpp"
    patched_hash = patch.prepare(source, patched_body_manager)
    run([cmake, "-S", ROOT / "native/jolt", "-B", build, "-G", "Ninja",
         f"-DCMAKE_MAKE_PROGRAM={ninja}", "-DCMAKE_BUILD_TYPE=Release", f"-DJOLT_SOURCE_DIR={source}",
         f"-DJOLT_BODY_MANAGER={patched_body_manager}", "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"], env=environment)
    run([cmake, "--build", build, "--target", "ainative_jolt", "--parallel", "8"], env=environment)
    name = "ainative_jolt.dll" if system == "Windows" else "libainative_jolt.so"
    shutil.copy2(build / name, destination / name)
    cache = (build / "CMakeCache.txt").read_text(encoding="utf-8")
    settings = {line.split(":", 1)[0]: line.split("=", 1)[1] for line in cache.splitlines()
                if ":BOOL=" in line or line.startswith(("CMAKE_BUILD_TYPE:", "CMAKE_CXX_COMPILER:", "CMAKE_CXX_FLAGS", "CMAKE_SHARED_LINKER_FLAGS"))}
    compile_commands = build / "compile_commands.json"
    shutil.copy2(compile_commands, destination / "compile_commands.json")
    compiler_files = sorted((build / "CMakeFiles").glob("*/CMakeCXXCompiler.cmake"))
    compiler = compiler_files[-1].read_text(encoding="utf-8")
    manifest = {"joltCommit": commit, "rid": rid, "abi": 2, "settings": settings,
                "restorePatchHash": hashlib.sha256(patch_path.read_bytes()).hexdigest(),
                "patchedBodyManagerHash": patched_hash,
                "compilerIdentification": compiler,
                "compileCommandsHash": hashlib.sha256(compile_commands.read_bytes()).hexdigest(),
                "binaryHash": hashlib.sha256((destination / name).read_bytes()).hexdigest()}
    (destination / "native-build.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    shutil.copy2(source / "LICENSE", destination / "Jolt-LICENSE.txt")
    print(f"Native bridge ready: {destination / name}")


if __name__ == "__main__":
    main()
