"""Isolated pre-URP fixture with CURRENT public packages; never creates a fourth example."""
import argparse
import io
import json
from pathlib import Path
import shutil
import subprocess
import zipfile
import xml.etree.ElementTree as ET
from unity_packages import verification_workspace

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = "5227a2b4cf2b65dd5acd77acf91385541ffd753e"


def verify(output):
    output.mkdir(parents=True, exist_ok=False)
    workspace = verification_workspace(output)
    archive = subprocess.check_output(["git", "archive", "--format=zip", FIXTURE,
                                       "examples/unity/AssetsDemo"], cwd=ROOT)
    with zipfile.ZipFile(io.BytesIO(archive)) as files:
        files.extractall(workspace)
    project = workspace / "examples/unity/AssetsDemo"
    for name in ("com.ainative.unitycore", "com.ainative.unitycore.assets"):
        shutil.copytree(ROOT / "packages" / name, workspace / "packages" / name)
    manifest = json.loads((project / "Packages/manifest.json").read_bytes())
    assert not any("render-pipelines" in name for name in manifest["dependencies"])
    (output / "fixture-source.txt").write_text(FIXTURE + "\n", encoding="utf-8")
    summary = {}
    for backend in ("Builtin", "Sbp"):
        log = output / (backend + "-content.log")
        subprocess.run(["unity", "run", str(project), "--timeout", "800", "--log-file", str(log),
                        "--no-tail", "--format", "json", "--", "-executeMethod",
                        "AssetsDemoSetup.BuildContent" + backend], cwd=ROOT, check=True, timeout=900)
        xml = output / (backend + "-play.xml")
        subprocess.run(["unity", "test", str(project), "--mode", "PlayMode", "--filter", "AiNative.UnityCore",
                        "--output", str(xml), "--timeout", "800", "--format", "json", "--", "-logFile",
                        str(output / (backend + "-play.log"))], cwd=ROOT, check=True, timeout=900)
        cases = ET.parse(xml).findall(".//test-case")
        if not cases or any(case.get("result") != "Passed" for case in cases):
            raise RuntimeError(f"Failed Built-in compatibility: {xml}")
        summary[backend] = len(cases)
    lock = json.loads((project / "Packages/packages-lock.json").read_bytes())
    assert not any("render-pipelines" in name for name in lock["dependencies"])
    shutil.copyfile(project / "Packages/packages-lock.json", output / "packages-lock.json")
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    verify(parser.parse_args().output.resolve())
