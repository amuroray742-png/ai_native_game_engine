"""Verify the HTTP/TCP client against a separate loopback server in Editor and Windows IL2CPP."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

from unity_packages import copy_verification_project, local_packages, verification_workspace
from unity_verification import (
    ROOT, check_log, fingerprint_project, read_test_report, read_unity_version, run_command, write_json,
)

PROJECT = ROOT / "examples/unity/NetworkDemo"
PACKAGES = ("com.ainative.unitycore", "com.ainative.unitycore.network")
BUILDER = "AiNative.NetworkDemo.Editor.NetworkPlayerBuilder."


def verify(output):
    cli = shutil.which("unity")
    if not cli:
        raise RuntimeError("Unity CLI is required")
    if set(local_packages(PROJECT)) != set(PACKAGES):
        raise RuntimeError("NetworkDemo must reference the Core and Network local source packages")
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    workspace = verification_workspace(output)
    source = fingerprint_project(ROOT, PROJECT, PACKAGES)
    project = copy_verification_project(PROJECT, workspace)
    if source != fingerprint_project(workspace, project, PACKAGES):
        raise RuntimeError("Network verification snapshot differs from source")
    write_json(output / "sources.json", source)
    version = (project / "ProjectSettings/ProjectVersion.txt").read_text().splitlines()[0].split(": ")[1]
    fixture = None
    try:
        fixture = subprocess.Popen([sys.executable, str(Path(__file__).with_name("network_fixture.py"))],
                                   stdout=subprocess.PIPE, text=True)
        ports = json.loads(fixture.stdout.readline())
        os.environ.update(AINATIVE_NETWORK_HTTP=str(ports["http"]), AINATIVE_NETWORK_TCP=str(ports["tcp"]))
        suite = "AiNative.NetworkDemo.Tests.NetworkTests"
        tests = []
        for reload in ("On", "Off"):
            label = "reload-" + reload
            method = BUILDER + "Reload" + reload
            log = output / (label + ".log")
            run_command(output, label, [cli, "run", project, "--timeout", "800", "--no-tail", "--format", "json",
                        "--log-file", log, "--", "-executeMethod", method])
            check_log(log)
            read_unity_version(log, version)
            for mode, test_filter, minimum in (("PlayMode", suite, 34),
                    ("EditMode", "AiNative.UnityCore.Network.Tests.NetworkPlayCycleTests", 1)):
                label = mode + "-" + reload
                xml = output / (label + ".xml")
                log = output / (label + ".log")
                run_command(output, label, [cli, "test", project, "--mode", mode, "--filter", test_filter,
                            "--output", xml, "--timeout", "800", "--format", "json", "--", "-logFile", log])
                tests.append(read_test_report(xml, test_filter, minimum))
                expected = ("InvalidOperationException: expected TCP listener test failure",) if mode == "PlayMode" else ()
                check_log(log, expected)
                read_unity_version(log, version)
        if any(tests[i]["tests"] != tests[i + 2]["tests"] for i in (0, 1)):
            raise RuntimeError("Editor test sets differ between Domain Reload settings")

        exe = workspace / "NetworkPlayer/NetworkDemo.exe"
        os.environ["AINATIVE_NETWORK_PLAYER"] = str(exe)
        build_log = output / "build.log"
        run_command(output, "build", [cli, "run", project, "--timeout", "1800", "--no-tail", "--format", "json",
                    "--log-file", build_log, "--", "-executeMethod", BUILDER + "Build"], 1900)
        check_log(build_log)
        read_unity_version(build_log, version)
        if not (exe.parent / "GameAssembly.dll").is_file():
            raise RuntimeError("Missing IL2CPP Player artifact")
        os.environ["AINATIVE_NETWORK_SMOKE"] = "1"
        log = output / "player.log"
        run_command(output, "player", [exe, "-batchmode", "-nographics", "-logFile", log], 60)
        check_log(log)
        if "NETWORK_SMOKE_PASSED IL2CPP" not in log.read_text(encoding="utf-8", errors="replace"):
            raise RuntimeError("Player did not finish network checks")
        if source != fingerprint_project(ROOT, PROJECT, PACKAGES):
            raise RuntimeError("Source changed during verification, run a fresh snapshot")
        write_json(output / "summary.json", dict(unity=version, editor=tests, player="Windows x64 IL2CPP passed", ports=ports))
    finally:
        if fixture is not None:
            fixture.terminate()
            fixture.wait(timeout=10)
            fixture.stdout.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    verify(parser.parse_args().output)
