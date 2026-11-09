"""Isolated Windows UnityCore/Assets validation; preserves logs, XML and source hashes."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

from unity_packages import copy_verification_project

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "examples/unity/AssetsDemo"


def verify(output, players):
    output.mkdir(parents=True, exist_ok=False)
    project = copy_verification_project(PROJECT, output / "workspace")
    cli = shutil.which("unity")
    if not cli:
        raise RuntimeError("Install Unity CLI before running verification")
    fingerprints = {}
    for base in (ROOT / "packages/com.ainative.unitycore", ROOT / "packages/com.ainative.unitycore.assets", PROJECT / "Assets"):
        for path in sorted(base.rglob("*")):
            if path.is_file() and "StreamingAssets" not in path.parts:
                fingerprints[str(path.relative_to(ROOT))] = hashlib.sha256(path.read_bytes()).hexdigest()
    (output / "sources.json").write_text(json.dumps(fingerprints, indent=2), encoding="utf-8")

    def run(label, arguments, timeout=900):
        command = [cli, *map(str, arguments)]
        with (output / "commands.jsonl").open("a", encoding="utf-8") as log:
            log.write(json.dumps(command) + "\n")
        print(label, flush=True)
        result = subprocess.run(command, cwd=ROOT, capture_output=True, timeout=timeout)
        (output / (label + ".stdout.json")).write_bytes(result.stdout)
        (output / (label + ".stderr.txt")).write_bytes(result.stderr)
        if result.returncode:
            raise RuntimeError(f"{label} failed ({result.returncode}); inspect preserved logs in {output}")

    def method(label, name):
        run(label, ["run", project, "--timeout", "800", "--log-file", output / (label + ".log"),
                    "--no-tail", "--format", "json", "--", "-executeMethod", name])

    # Build content from source in isolation, even when the development checkout has prior content.
    content = project / "Assets/StreamingAssets/AiNativeAssets"
    if content.exists():
        content.rename(output / "copied-development-content")
    method("content", "AssetsDemoSetup.BuildContent")
    summary = {}
    for reload in ("On", "Off"):
        method("reload-" + reload, "AssetsDemoSetup.Reload" + reload)
        for mode in ("PlayMode", "EditMode"):
            label = mode + "-reload-" + reload
            xml = output / (label + ".xml")
            run(label, ["test", project, "--mode", mode, "--filter", "AiNative.UnityCore",
                        "--output", xml, "--timeout", "800", "--format", "json", "--", "-logFile", output / (label + ".log")])
            cases = ET.parse(xml).findall(".//test-case")
            if not cases or any(case.get("result") != "Passed" for case in cases):
                raise RuntimeError(f"Failed or empty test report: {xml}")
            summary[label] = len(cases)
    if players:
        for backend in ("Mono", "Il2Cpp"):
            method("build-" + backend, "AssetsDemoSetup.Build" + backend)
            executable = output / "workspace/TestResults/assets-players" / backend / "AssetsDemo.exe"
            result_path = output / (backend + "-smoke.json")
            command = [str(executable), "-batchmode", "-assetsSmoke", "-assetsResult", str(result_path), "-logFile", str(output / (backend + "-player.log"))]
            creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
            subprocess.run(command, cwd=executable.parent, check=True, timeout=90, creationflags=creationflags)
            result = json.loads(result_path.read_bytes())
            if not result["success"]:
                raise RuntimeError(str(result))
            summary[backend] = result
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="New evidence directory (never overwritten)")
    parser.add_argument("--players", action="store_true", help="Also build and smoke Windows Mono Development and IL2CPP Players")
    args = parser.parse_args()
    verify(args.output.resolve(), args.players)
