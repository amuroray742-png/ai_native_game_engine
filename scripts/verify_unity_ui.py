"""Isolated UIDemo SBP build and dual Domain Reload Editor acceptance.

Every invocation uses a new evidence directory and a private source copy.
Screen captures are a separate visible-Editor check; see the UIDemo README.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET

from unity_packages import copy_verification_project, local_packages, verification_workspace

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "examples/unity/UIDemo"
PACKAGES = ("com.ainative.unitycore", "com.ainative.unitycore.assets", "com.ainative.unitycore.ui",
            "com.ainative.unitycore.network")
UI_FILTER = "AiNative.UnityCore.UI.Tests"
DEMO_FILTER = "AiNative.UIDemo.Tests.UIDemoTests"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


EXPECTED_EXCEPTIONS = {
    "ui-play": (
        "InvalidOperationException: forced suspend failure",
        "Rethrow as AggregateException: One or more errors occurred. (forced suspend failure)",
        "Rethrow as AggregateException: One or more errors occurred. (One or more errors occurred. (forced suspend failure))",
    ),
    "demo": (
        "InvalidOperationException: Demo requested one cleanup failure; retry Clear.",
        "Rethrow as AggregateException: One or more errors occurred. (Demo requested one cleanup failure; retry Clear.)",
        "Rethrow as UIOperationException: Window cleanup failed.",
        "Rethrow as AggregateException: One or more errors occurred. (Window cleanup failed.)",
        "Rethrow as UIOperationException: UI clear failed; retry before replacing model dependencies.",
        # Automatic toast close also injects a real eviction cleanup fault.
        "InvalidOperationException: Demo requested one cleanup failure; retry Clear.",
        "Rethrow as AggregateException: One or more errors occurred. (Demo requested one cleanup failure; retry Clear.)",
        "Rethrow as UIOperationException: Window cleanup failed.",
        "Rethrow as AggregateException: One or more errors occurred. (Window cleanup failed.)",
        "Rethrow as AggregateException: One or more errors occurred. (One or more errors occurred. (Window cleanup failed.))",
        "Rethrow as UIOperationException: Window close failed.",
    ),
}


def check_log(path, expected_exceptions=()):
    text = path.read_text(encoding="utf-8", errors="replace")
    patterns = (r"JobTempAlloc", r"TLS Allocator ALLOC_TEMP", r"\berror CS\d+\b",
                r"Scripts have compiler errors", r"Compilation failed", r"Assertion failed",
                r"Unhandled Exception", r"Aborting batchmode due to failure", r"UnityEngine.Debug:LogError",
                r"^Error:")
    for pattern in patterns:
        if re.search(pattern, text, re.IGNORECASE | re.MULTILINE):
            raise RuntimeError(f"Editor log contains {pattern}: {path}")
    allowed = list(expected_exceptions)
    for line in text.splitlines():
        line = line.strip()
        if re.match(r"^(?:Rethrow as )?[\w.+]*Exception:", line):
            if line not in allowed:
                raise RuntimeError(f"Unexpected runtime exception: {line}: {path}")
            allowed.remove(line)  # Each listed occurrence permits exactly one matching line.
    # Allowances apply only to the specific suite, whose XML must also pass LogAssert.
    # Licensing validation / Mono shutdown notices are retained, not blanket errors.


def read_test_report(path, test_filter, minimum=1):
    root = ET.parse(path).getroot()
    cases = root.findall(".//test-case")
    if root.tag != "test-run" or root.get("result") != "Passed" or len(cases) < minimum:
        raise RuntimeError(f"Failed or empty test report: {path}")
    for case in cases:
        name = case.get("fullname", "")
        if case.get("result") != "Passed" or not name.startswith(test_filter + "."):
            raise RuntimeError(f"Failed, skipped or unexpected test {name}: {path}")
    counts = {key: int(root.get(key, "-1")) for key in ("total", "passed", "failed", "skipped")}
    if counts != dict(total=len(cases), passed=len(cases), failed=0, skipped=0):
        raise RuntimeError(f"Inconsistent test counts: {path}: {counts}")
    return dict(name=path.stem, result="Passed", **counts, xmlSha256=digest(path),
                tests=sorted(case.get("fullname") for case in cases))


def read_unity_version(path, expected):
    text = path.read_text(encoding="utf-8", errors="replace")
    match = re.search(r"Initialize engine version:\s*(\S+)", text)
    if not match or match.group(1) != expected:
        raise RuntimeError(f"Actual Unity version missing or differs from {expected}: {path}")
    return match.group(1)


def run_command(output, label, command, timeout=900):
    command = list(map(str, command))
    print(label, flush=True)
    timed_out = False
    try:
        result = subprocess.run(command, cwd=ROOT, capture_output=True, timeout=timeout)
        stdout, stderr, code = result.stdout, result.stderr, result.returncode
    except subprocess.TimeoutExpired as error:
        stdout, stderr, code = error.stdout or b"", error.stderr or b"", None
        timed_out = True
    (output / (label + ".stdout.json")).write_bytes(stdout)
    (output / (label + ".stderr.txt")).write_bytes(stderr)
    with (output / "commands.jsonl").open("a", encoding="utf-8") as stream:
        stream.write(json.dumps(dict(label=label, command=command, exitCode=code, timedOut=timed_out)) + "\n")
    if timed_out or code != 0:
        raise RuntimeError(f"{label} failed (exit={code}, timeout={timed_out}); evidence: {output}")


def fingerprint(root, project):
    paths = []
    for name in PACKAGES:
        paths.extend((root / "packages" / name).rglob("*"))
    for directory in ("Assets", "Packages", "ProjectSettings"):
        paths.extend((project / directory).rglob("*"))
    return {path.relative_to(root).as_posix(): digest(path) for path in sorted(paths)
            if path.is_file() and "StreamingAssets" not in path.parts}


def verify(output):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    summary = dict(status="Running", platform="Windows Editor", tests=[],
                   unverified=["Performance", "Device safe area", "Player/IL2CPP", "Remote CI"],
                   screenshots=dict(status="Pending visible Editor verification"))
    try:
        cli = shutil.which("unity")
        if not cli:
            raise RuntimeError("Unity CLI is required")
        run_command(output, "cli-version", [cli, "--version"], 30)
        summary["unityCli"] = (output / "cli-version.stdout.json").read_text(encoding="utf-8").strip()
        expected_unity = (PROJECT / "ProjectSettings/ProjectVersion.txt").read_text(encoding="utf-8").splitlines()[0].split(": ")[1]
        summary["requestedUnity"] = expected_unity
        if set(local_packages(PROJECT)) != set(PACKAGES):
            raise RuntimeError("UIDemo must reference the expected local source packages")
        workspace = verification_workspace(output)
        project = copy_verification_project(PROJECT, workspace)
        summary["workspace"] = str(workspace)
        summary["project"] = str(project)
        before = fingerprint(ROOT, PROJECT)
        if before != fingerprint(workspace, project):
            raise RuntimeError("Source copy fingerprint mismatch")
        write_json(output / "sources.json", before)
        write_json(output / "scripts.json", {name: digest(ROOT / "scripts" / name) for name in
                   ("verify_unity_ui.py", "test_verify_unity_ui.py", "unity_packages.py")})
        # Never let an existing development deployment satisfy the Bundle tests.
        content = project / "Assets/StreamingAssets/AiNativeAssets"
        if content.exists():
            shutil.move(str(content), str(output / "copied-development-content"))

        def method(label, name):
            run_command(output, label, [cli, "run", project, "--timeout", "800", "--log-file",
                        output / (label + ".log"), "--no-tail", "--format", "json", "--",
                        "-executeMethod", "AiNative.UIDemo.Editor.UIDemoBuilder." + name])
            check_log(output / (label + ".log"))
            summary["unity"] = read_unity_version(output / (label + ".log"), expected_unity)

        method("create", "Create")
        method("build-sbp", "BuildContentSbp")
        catalog = json.loads((content / "catalog.json").read_bytes())
        expected = {"catalog.json", *(bundle["Name"] for bundle in catalog["Bundles"])}
        actual = {path.name for path in content.iterdir() if path.is_file() and path.suffix != ".meta"}
        if actual != expected or not catalog["Bundles"]:
            raise RuntimeError("Missing or unexpected deployed Bundle files")
        deployed = {name: digest(content / name) for name in sorted(expected)}
        builds = list((project / "BuildArtifacts/UIContent").glob("**/catalog.json"))
        if len(builds) != 1 or any(not (builds[0].parent / name).is_file() or
                                  digest(builds[0].parent / name) != value for name, value in deployed.items()):
            raise RuntimeError("Build and deployment hashes differ")
        for name in expected:
            (output / "content").mkdir(exist_ok=True)
            shutil.copy2(content / name, output / "content" / name)
        summary["content"] = deployed
        # Record generated fixture/settings separately: never present them as byte-identical source.
        write_json(output / "generated-sources.json", fingerprint(workspace, project))
        for reload in ("On", "Off"):
            method("reload-" + reload.lower(), "Reload" + reload)
            for mode, suite, test_filter, minimum in (("EditMode", "ui-edit", UI_FILTER, 25),
                                                     ("PlayMode", "ui-play", UI_FILTER, 109),
                                                     ("PlayMode", "demo", DEMO_FILTER, 14)):
                label = suite
                label = reload.lower() + "-" + label
                xml = output / (label + ".xml")
                run_command(output, label, [cli, "test", project, "--mode", mode, "--filter", test_filter,
                            "--output", xml, "--timeout", "800", "--format", "json", "--", "-logFile", output / (label + ".log")])
                check_log(output / (label + ".log"), EXPECTED_EXCEPTIONS.get(suite, ()))
                read_unity_version(output / (label + ".log"), expected_unity)
                summary["tests"].append(read_test_report(xml, test_filter, minimum))
                write_json(output / "progress.json", summary)
        for index in range(3):
            if summary["tests"][index]["tests"] != summary["tests"][index + 3]["tests"]:
                raise RuntimeError("Domain Reload matrices executed different test sets")
        # A source edit while Unity runs must invalidate the evidence rather than silently
        # attributing the previous snapshot's test results to the changed working tree.
        if before != fingerprint(ROOT, PROJECT):
            raise RuntimeError("Working source changed during verification; rerun a fresh snapshot")
        summary["totalExecutions"] = sum(test["total"] for test in summary["tests"])
        summary["status"] = "Passed"
        write_json(output / "summary.json", summary)
        print(json.dumps(dict(status=summary["status"], totalExecutions=summary["totalExecutions"], output=str(output))), flush=True)
        return summary
    except Exception as error:
        summary["status"] = "Failed"
        summary["error"] = str(error)
        write_json(output / "failure.json", summary)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="New evidence directory; existing directories are rejected")
    args = parser.parse_args()
    verify(args.output)
