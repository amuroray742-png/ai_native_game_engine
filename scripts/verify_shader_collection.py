"""Verify shader collection in an isolated AssetsDemo; never change the user's content."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

from unity_packages import copy_verification_project, verification_workspace
from verify_unity_assets import check_native_leaks

ROOT = Path(__file__).resolve().parents[1]


def verify(output):
    output.mkdir(parents=True, exist_ok=False)
    workspace = verification_workspace(output)
    project = copy_verification_project(ROOT / "examples/unity/AssetsDemo", workspace)
    cli = shutil.which("unity")
    if not cli:
        raise RuntimeError("Unity CLI is required")
    sources = {}
    for base in (workspace / "packages", project / "Assets", project / "Packages", project / "ProjectSettings"):
        for path in sorted(base.rglob("*")):
            if path.is_file() and "StreamingAssets" not in path.parts:
                sources[str(path.relative_to(workspace))] = hashlib.sha256(path.read_bytes()).hexdigest()
    (output / "sources.json").write_text(json.dumps(sources, indent=2), encoding="utf-8")

    def run(label, arguments):
        log = output / (label + ".log")
        command = [cli, *map(str, arguments), "--", "-logFile", str(log)]
        print(label, flush=True)
        result = subprocess.run(command, cwd=ROOT, capture_output=True, timeout=1000)
        (output / (label + ".stdout.json")).write_bytes(result.stdout)
        (output / (label + ".stderr.txt")).write_bytes(result.stderr)
        if result.returncode:
            raise RuntimeError(f"{label} failed ({result.returncode}); see {output}")
        check_native_leaks(log)

    def method(label, name):
        # Unity run forwards everything after -- to the Editor.
        log = output / (label + ".log")
        command = [cli, "run", str(project), "--timeout", "900", "--no-tail", "--format", "json",
                   "--", "-executeMethod", name, "-logFile", str(log)]
        result = subprocess.run(command, cwd=ROOT, capture_output=True, timeout=1000)
        (output / (label + ".stdout.json")).write_bytes(result.stdout)
        (output / (label + ".stderr.txt")).write_bytes(result.stderr)
        if result.returncode:
            raise RuntimeError(f"{label} failed; see {output}")
        check_native_leaks(log)

    method("configure", "AssetsDemoSetup.ConfigureBuildSettings")
    method("fixture", "ShaderProbeSetup.Create")
    summary = {}
    for mode in ("On", "Off"):
        method("reload-" + mode, "AssetsDemoSetup.Reload" + mode)
        label = "collection-reload-" + mode
        xml = output / (label + ".xml")
        run(label, ["test", project, "--mode", "EditMode", "--filter", "AssetShader",
                    "--output", xml, "--timeout", "900", "--format", "json"])
        result = ET.parse(xml).getroot()
        actual = [case for case in result.iter("test-case")
                  if case.get("methodname") == "ActualUrpFramesRecordRuntimeKeywordsAndSurvivePlayTransition"]
        if (result.get("result") != "Passed" or int(result.get("failed", 0)) or
                int(result.get("skipped", 0)) or int(result.get("passed", 0)) < 6 or
                len(actual) != 1 or actual[0].get("result") != "Passed"):
            raise RuntimeError("The actual AssetsDemo collection tests must pass, not be skipped")
        summary[mode] = dict(result.attrib)
        shutil.copytree(project / "TestResults/ShaderAutoVerification", output / ("artifacts-" + mode))
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    verify(parser.parse_args().output.resolve())
