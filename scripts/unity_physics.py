"""Prepare the Windows Editor PhysicsSandbox project and verify it against the v2 CLI."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

from unity_grid import ROOT, sync_package


PROJECT = ROOT / "examples/unity/PhysicsSandbox"
RUNNER = ROOT / "examples/AiNative.Examples.Runner"
DATA = ROOT / "examples/data"
MANAGED = (
    "AiNative.Core", "AiNative.Hosting", "AiNative.Physics", "AiNative.Physics.Jolt",
    "AiNative.PhysicsSandbox", "AiNative.PhysicsSandbox.Hosting", "System.Text.Json",
    "System.Text.Encodings.Web", "System.IO.Pipelines", "Microsoft.Bcl.AsyncInterfaces",
    "System.Runtime.CompilerServices.Unsafe",
)


def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if not destination.exists() or destination.read_bytes() != source.read_bytes():
        shutil.copyfile(source, destination)


def prepare():
    native = ROOT / "artifacts/native/win-x64"
    library = native / "ainative_jolt.dll"
    if not library.is_file():
        raise RuntimeError("Build the Windows x64 bridge with python scripts/build_native.py first.")
    manifest = native / "native-build.json"
    if hashlib.sha256(library.read_bytes()).hexdigest() != json.loads(manifest.read_bytes())["binaryHash"]:
        raise RuntimeError("Native library differs from its build manifest.")

    plugins = PROJECT / "Assets/Plugins"
    allowed = {name + ".dll" for name in MANAGED} | {library.name}
    extra = {p.name for p in plugins.rglob("*.dll")} - allowed
    if extra:
        raise ValueError(f"Unexpected PhysicsSandbox plugins: {sorted(extra)}")
    with tempfile.TemporaryDirectory() as staging:
        published = Path(staging)
        for project in ("examples/AiNative.PhysicsSandbox.Hosting", "src/AiNative.Physics.Jolt"):
            subprocess.run(["dotnet", "restore", project, "--locked-mode"], cwd=ROOT, check=True)
            subprocess.run(["dotnet", "publish", project, "-c", "Release", "--no-restore", "-o", published],
                           cwd=ROOT, check=True)
        for name in MANAGED:
            copy(published / (name + ".dll"), plugins / "AiNative" / (name + ".dll"))
    copy(library, plugins / "x86_64" / library.name)
    assets = PROJECT / "Assets/StreamingAssets"
    copy(manifest, assets / manifest.name)
    for name in ("physics.scenario.json", "physics.commands.json"):
        copy(DATA / name, assets / name)
    sync_package(ROOT / "packages/com.ainative.unity", PROJECT)
    sync_package(ROOT / "packages/com.ainative.unitycore", PROJECT)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "verify"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    prepare()
    if args.action == "verify":
        if args.output is None:
            parser.error("verify requires a new --output directory")
        from verify_unity_physics import verify
        verify(args.output.resolve())


if __name__ == "__main__":
    main()
