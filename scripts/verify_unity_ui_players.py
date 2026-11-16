"""Build and run isolated Windows x64 UIDemo release Players (Mono and IL2CPP)."""
import argparse
import json
from pathlib import Path
import shutil
import sys
from PIL import Image

from unity_packages import copy_verification_project, local_packages, verification_workspace
from verify_unity_ui import ROOT, PROJECT, PACKAGES, check_log, digest, fingerprint, read_unity_version, run_command, write_json

CHECKS = ["bundle-shared-image", "raycast-page-stack", "modal-input-focus", "cache-session-lru",
          "transition-cancellation", "toast-expiry-input", "clear-failure-retry",
          "scene-persistence-unscaled-transition", "trim-last-owner", "explicit-shutdown-zero-resources"]


def validate_report(path, backend, unity):
    report = json.loads(path.read_bytes())
    if (report.get("success") is not True or report.get("backend") != backend or
            report.get("unity") != unity or report.get("graphics") != "Direct3D11" or
            report.get("checks") != CHECKS or report.get("frames", 0) < 10 or report.get("error")):
        raise RuntimeError(f"Invalid Player acceptance report: {path}: {report}")
    for name in ("home", "modals"):
        screenshot = path.with_suffix(f".{name}.png")
        try:
            with Image.open(screenshot) as image:
                if image.format != "PNG" or image.size != (1280, 720):
                    raise ValueError("Expected 1280x720 PNG")
                image.verify()  # Includes chunk integrity; a PNG header alone is not evidence.
            with Image.open(screenshot) as image:
                image.load()  # Force pixel decoding and reject truncated IDAT data.
                if all(low == high for low, high in image.convert("RGB").getextrema()):
                    raise ValueError("Blank screenshot")
        except (OSError, ValueError, SyntaxError) as error:
            raise RuntimeError(f"Invalid Player screenshot: {screenshot}") from error
    if (report.get("width"), report.get("height")) != (1280, 720):
        raise RuntimeError("Unexpected Player screen size")
    return report


def verify(output):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    summary = dict(status="Running", platform="Windows x64 Player", players=[],
                   scope="Release, Minimal managed stripping, Direct3D11; synthetic uGUI raycasts/clicks; explicit graceful shutdown",
                   unverified=["OS mouse/keyboard input", "Forced process termination", "Other graphics APIs/platforms", "Remote CI"])
    try:
        if sys.platform != "win32":
            raise RuntimeError("Windows is required")
        cli = shutil.which("unity")
        if not cli:
            raise RuntimeError("Unity CLI is required")
        run_command(output, "cli-version", [cli, "--version"], 30)
        version = (PROJECT / "ProjectSettings/ProjectVersion.txt").read_text().splitlines()[0].split(": ")[1]
        if set(local_packages(PROJECT)) != set(PACKAGES):
            raise RuntimeError("Unexpected UIDemo local packages")
        workspace = verification_workspace(output)
        project = copy_verification_project(PROJECT, workspace)
        summary.update(workspace=str(workspace), project=str(project), unity=version)
        before = fingerprint(ROOT, PROJECT)
        if fingerprint(workspace, project) != before:
            raise RuntimeError("Source snapshot mismatch")
        write_json(output / "sources.json", before)
        script_names = ("verify_unity_ui_players.py", "test_verify_unity_ui_players.py", "verify_unity_ui.py", "unity_packages.py")
        scripts = {name: digest(ROOT / "scripts" / name) for name in script_names}
        write_json(output / "scripts.json", scripts)
        content = project / "Assets/StreamingAssets/AiNativeAssets"
        if content.exists():
            shutil.move(str(content), str(output / "copied-development-content"))

        def method(label, name):
            log = output / (label + ".log")
            run_command(output, label, [cli, "run", project, "--timeout", "1800", "--log-file", log,
                        "--no-tail", "--format", "json", "--", "-executeMethod", "AiNative.UIDemo.Editor.UIDemoBuilder." + name], 1900)
            check_log(log)
            read_unity_version(log, version)

        method("create", "Create")
        method("build-sbp", "BuildContentSbp")
        catalog = json.loads((content / "catalog.json").read_bytes())
        names = {"catalog.json", *(bundle["Name"] for bundle in catalog["Bundles"])}
        if len(catalog["Bundles"]) != 9 or {p.name for p in content.iterdir() if p.is_file() and p.suffix != ".meta"} != names:
            raise RuntimeError("Unexpected UI content deployment")
        built = list((project / "BuildArtifacts/UIContent").glob("**/catalog.json"))
        hashes = {name: digest(content / name) for name in sorted(names)}
        if len(built) != 1 or any(digest(built[0].parent / name) != value for name, value in hashes.items()):
            raise RuntimeError("SBP build differs from deployment")
        shutil.copytree(content, output / "content", ignore=shutil.ignore_patterns("*.meta"))
        summary["content"] = hashes
        for backend in ("Mono", "Il2Cpp"):
            method("build-" + backend, "Build" + backend)
            folder = workspace / "TestResults/ui-players" / backend
            executable = folder / "UIDemo.exe"
            deployed = folder / "UIDemo_Data/StreamingAssets/AiNativeAssets"
            if {p.name for p in deployed.iterdir()} != names or any(digest(deployed / name) != value for name, value in hashes.items()):
                raise RuntimeError("Player embedded content mismatch")
            # Backend identity is checked both on disk and in the executing runtime.
            if backend == "Il2Cpp":
                if not (folder / "GameAssembly.dll").is_file() or not (folder / "UIDemo_Data/il2cpp_data/Metadata/global-metadata.dat").is_file():
                    raise RuntimeError("Missing IL2CPP native artifacts")
            elif not (folder / "MonoBleedingEdge").is_dir():
                raise RuntimeError("Missing Mono runtime")
            manifest = {p.relative_to(folder).as_posix(): digest(p) for p in sorted(folder.rglob("*")) if p.is_file()}
            write_json(output / (backend + "-player-files.json"), manifest)
            result = output / (backend + "-result.json")
            log = output / (backend + "-player.log")
            # Graphical Player is necessary for end-of-frame screenshots. No batch/nographics.
            run_command(output, "run-" + backend, [executable, "-force-d3d11", "-screen-fullscreen", "0",
                        "-screen-width", "1280", "-screen-height", "720", "-uiSmoke", "-uiResult", result, "-logFile", log], 120)
            check_log(log)
            read_unity_version(log, version)
            report = validate_report(result, backend, version)
            summary["players"].append(dict(backend=backend, report=report, resultSha256=digest(result),
                                           filesSha256=digest(output / (backend + "-player-files.json")),
                                           screenshots={name: digest(result.with_suffix(f".{name}.png")) for name in ("home", "modals")}))
            write_json(output / "progress.json", summary)
        if fingerprint(ROOT, PROJECT) != before or any(digest(ROOT / "scripts" / name) != value for name, value in scripts.items()):
            raise RuntimeError("Source or verification scripts changed during acceptance")
        write_json(output / "generated-sources.json", fingerprint(workspace, project))
        summary["status"] = "Passed"
        write_json(output / "summary.json", summary)
        print(json.dumps(summary), flush=True)
    except Exception as error:
        summary.update(status="Failed", error=str(error))
        write_json(output / "failure.json", summary)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="New evidence directory")
    verify(parser.parse_args().output)
