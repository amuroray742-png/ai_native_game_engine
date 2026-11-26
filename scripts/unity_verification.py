"""Command execution and evidence checks for Unity example verification."""
import hashlib
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


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


def fingerprint_project(root, project, packages, *, exclude_streaming_assets=False):
    paths = []
    for name in packages:
        paths.extend((root / "packages" / name).rglob("*"))
    for directory in ("Assets", "Packages", "ProjectSettings"):
        paths.extend((project / directory).rglob("*"))
    return {path.relative_to(root).as_posix(): digest(path) for path in sorted(paths)
            if path.is_file() and (not exclude_streaming_assets or "StreamingAssets" not in path.parts)}
