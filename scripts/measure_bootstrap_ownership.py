"""Measure the fixed A5 ownership case in a new Windows Unity verification project."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import time
import xml.etree.ElementTree as ET
from zipfile import ZipFile

from unity_packages import verification_workspace


ROOT = Path(__file__).resolve().parents[1]
BASE = "70fc6a7f3fa6578a98cecef4c920e7d9971cb5f6"
RED = "afe41cc108b7be912cf6d3fd64e2e1394ff5b832"
PROJECT = "examples/unity/AssetsDemo"
BOOTSTRAP = PROJECT + "/Assets/AssetsDemoBootstrap.cs"
TEST = "AiNative.UnityCore.AssetsDemoTests.BootstrapOwnershipTests.RejectedBootstrapLeavesRunningCoreAlive"


def measure(output):
    if os.name != "nt":
        raise RuntimeError("This case builds a Windows Mono Player.")
    output.mkdir(parents=True, exist_ok=False)
    workspace = verification_workspace(output)
    project = workspace / PROJECT
    cli_version = subprocess.check_output(["unity", "--version"], text=True).strip()
    report = {"baseCommit": BASE, "redBootstrapCommit": RED, "test": TEST,
              "os": platform.platform(), "unityCli": cli_version, "steps": {}}

    def run(label, args):
        command = ["unity", *map(str, args)]
        start = time.perf_counter()
        with (output / (label + ".stdout")).open("wb") as stdout:
            proc = subprocess.run(command, cwd=ROOT, stdout=stdout, stderr=subprocess.STDOUT, timeout=960)
        report["steps"][label] = {"command": command, "exitCode": proc.returncode,
                                  "wallSeconds": round(time.perf_counter() - start, 3)}
        return proc.returncode

    def test(label, expected):
        xml = output / (label + ".xml")
        code = run(label, ["test", project, "--mode", "EditMode", "--filter", TEST,
                           "--output", xml, "--timeout", "900", "--json", "--",
                           "-logFile", output / (label + ".log")])
        cases = ET.parse(xml).findall(".//test-case")
        if len(cases) != 1 or cases[0].get("fullname") != TEST or cases[0].get("result") != expected:
            raise RuntimeError(f"Unexpected {label} test result: {xml}")
        if expected == "Failed":
            message = cases[0].findtext("./failure/message", "")
            if code == 0 or not re.fullmatch(r"\s*Expected: Running\s+But was:\s+Stopped\s*", message):
                raise RuntimeError(f"Unexpected red failure: exit={code}, message={message!r}")
        elif code != 0:
            raise RuntimeError(f"Green command failed: exit={code}")
        report["steps"][label]["caseSeconds"] = float(cases[0].get("duration"))

    repro_start = time.perf_counter()
    archive = output / "inputs.zip"
    subprocess.run(["git", "archive", "--format=zip", "-o", str(archive), BASE, PROJECT,
                    "packages/com.ainative.unitycore", "packages/com.ainative.unitycore.assets"],
                   cwd=ROOT, check=True)
    with ZipFile(archive) as inputs:
        inputs.extractall(workspace)
    bootstrap = workspace / BOOTSTRAP
    fixed = bootstrap.read_bytes()
    bootstrap.write_bytes(subprocess.check_output(["git", "show", RED + ":" + BOOTSTRAP], cwd=ROOT))
    test("red", "Failed")
    report["reproductionSeconds"] = round(time.perf_counter() - repro_start, 3)
    bootstrap.write_bytes(fixed)
    test("green", "Passed")

    # Prepare SBP content before timing the Player build.
    for label, method in (("renderer", "ExampleUrpSetup.Forward"), ("fixture", "ShaderProbeSetup.Create"),
                          ("content", "AssetsDemoSetup.BuildContentSbp"), ("build", "AssetsDemoSetup.BuildMonoStandard")):
        if run(label, ["run", project, "--timeout", "900", "--log-file", output / (label + ".log"),
                       "--no-tail", "--json", "--", "-executeMethod", method]):
            raise RuntimeError(f"{label} failed; see {output}")
    player = workspace / "TestResults/assets-players/Mono/AssetsDemo.exe"
    report["playerSha256"] = hashlib.sha256(player.read_bytes()).hexdigest()
    report["editor"] = (project / "ProjectSettings/ProjectVersion.txt").read_text().splitlines()[0]
    (output / "summary.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="New evidence directory on a local drive")
    measure(parser.parse_args().output.resolve())
