"""Build and run both simulation examples in isolated Windows Mono and IL2CPP Players."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

from unity_grid import ROOT, prepare as prepare_grid
from unity_physics import prepare as prepare_physics
from unity_packages import copy_verification_project, verification_workspace


EXAMPLES = (
    ("grid", ROOT / "examples/unity/GridGame", "AiNative.Examples.GridGame.Play.Editor.GridPlayerBuild"),
    ("physics", ROOT / "examples/unity/PhysicsSandbox", "AiNative.Examples.PhysicsPlay.Editor.PhysicsPlayerBuild"),
)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run_unity(project, output, label, method, player):
    command = ["unity", "run", str(project), "--timeout", "1800", "--format", "json",
               "--log-file", str(output / (label + ".log")), "--no-tail", "--",
               "-executeMethod", method, "-playerOutput", str(player)]
    result = subprocess.run(command, cwd=ROOT, capture_output=True, timeout=1860)
    (output / (label + ".stdout.json")).write_bytes(result.stdout)
    (output / (label + ".stderr.txt")).write_bytes(result.stderr)
    (output / (label + ".command.json")).write_text(
        json.dumps({"command": command, "exitCode": result.returncode}, indent=2), encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"{label} failed ({result.returncode}); see {output}")


def verify(output, examples=("grid", "physics"), backends=("Mono", "Il2Cpp")):
    output.mkdir(parents=True, exist_ok=False)
    if "grid" in examples:
        prepare_grid()
    if "physics" in examples:
        prepare_physics()
    summary = {"sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
               "examples": {}}
    for name, source, builder in EXAMPLES:
        if name not in examples:
            continue
        report = output / name
        report.mkdir()
        workspace = verification_workspace(report)
        project = copy_verification_project(source, workspace)
        fingerprints = {}
        for base in (source / "Assets", source / "Packages", source / "ProjectSettings"):
            for path in sorted(base.rglob("*")):
                generated_content = "StreamingAssets" in path.parts and "AiNativeAssets" in path.parts
                if path.is_file() and not generated_content:
                    fingerprints[path.relative_to(source).as_posix()] = sha256(path)
        (report / "sources.json").write_text(json.dumps(fingerprints, indent=2), encoding="utf-8")
        content_builder = ("AiNative.Examples.GridGame.Play.Editor.GridUiBuilder.BuildContent" if name == "grid"
                           else "AiNative.Examples.PhysicsPlay.Editor.SandboxContent.BuildContent")
        run_unity(project, report, "content", content_builder, workspace / "unused.exe")
        backend_results = {}
        for backend in backends:
            player = workspace / "players" / backend / (source.name + ".exe")
            run_unity(project, report, "build-" + backend, builder + "." + backend, player)
            result_path = report / (backend + ".json")
            log = report / (backend + ".player.log")
            command = [str(player), "-batchmode", "-force-d3d11", "-playerSmokeResult", str(result_path),
                       "-logFile", str(log)]
            played = subprocess.run(command, cwd=player.parent, timeout=300)
            (report / (backend + ".command.json")).write_text(
                json.dumps({"command": command, "exitCode": played.returncode}, indent=2), encoding="utf-8")
            if played.returncode or not result_path.is_file():
                raise RuntimeError(f"{name} {backend} Player failed ({played.returncode}); see {report}")
            result = json.loads(result_path.read_bytes())
            if not result["success"] or not result_path.with_suffix(".png").is_file():
                raise RuntimeError(f"{name} {backend} acceptance failed: {result}")
            evidence = {"result": result, "screenshotSha256": sha256(result_path.with_suffix(".png"))}
            if name == "physics":
                libraries = list(player.parent.rglob("ainative_jolt.dll"))
                if len(libraries) != 1:
                    raise RuntimeError(f"Expected one deployed Jolt bridge, found {libraries}")
                manifest = project / "Assets/StreamingAssets/native-build.json"
                expected = json.loads(manifest.read_bytes())["binaryHash"]
                if sha256(libraries[0]) != expected:
                    raise RuntimeError("Deployed Jolt bridge differs from the build manifest")
                evidence["nativeBinaryHash"] = expected
                evidence["nativeRelativePath"] = libraries[0].relative_to(player.parent).as_posix()
            backend_results[backend] = evidence
        summary["examples"][name] = {"project": source.relative_to(ROOT).as_posix(),
                                     "sourceFiles": len(fingerprints), "backends": backend_results}
    (output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="New evidence directory")
    parser.add_argument("--examples", choices=("grid", "physics", "both"), default="both")
    parser.add_argument("--backends", choices=("Mono", "Il2Cpp", "both"), default="both")
    arguments = parser.parse_args()
    examples = ("grid", "physics") if arguments.examples == "both" else (arguments.examples,)
    backends = ("Mono", "Il2Cpp") if arguments.backends == "both" else (arguments.backends,)
    verify(arguments.output.resolve(), examples, backends)
