#!/usr/bin/env python3
"""Run real CLI processes and preserve phase-two acceptance evidence (stdlib only)."""
import argparse
import copy
import json
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
EXAMPLES = ROOT / "docs/contracts/phase-2/examples"


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


class Evidence:
    def __init__(self, host, output):
        self.host = host
        self.output = output
        self.calls = []

    def invoke(self, name, *arguments, exit_code=0, code=None, demo=False):
        command = ["dotnet", str(self.host), *map(str, arguments)]
        entry = {"name": name, "command": command, "expectedExitCode": exit_code}
        self.calls.append(entry)
        try:
            result = subprocess.run(command, cwd=self.output, capture_output=True,
                                    encoding="utf-8", timeout=30)
        except subprocess.TimeoutExpired as error:
            entry["timedOut"] = True
            for stream in ("stdout", "stderr"):
                value = getattr(error, stream) or b""
                if isinstance(value, bytes):
                    value = value.decode("utf-8", errors="replace")
                (self.output / f"{name}.{stream}.txt").write_text(value, encoding="utf-8")
            raise
        entry["exitCode"] = result.returncode
        (self.output / f"{name}.stdout.txt").write_text(result.stdout, encoding="utf-8")
        (self.output / f"{name}.stderr.txt").write_text(result.stderr, encoding="utf-8")
        require(result.returncode == exit_code, f"{name}: exit {result.returncode}, expected {exit_code}")
        if demo:
            require("Demo passed: Winner=A, Turn=3, A.Health=2, B.Health=0, AP=1" in result.stdout, name)
            require(not result.stderr, f"{name}: unexpected stderr")
            return None
        value = json.loads(result.stdout)
        require(result.stdout.endswith("\n"), f"{name}: missing final newline")
        require(value["code"] == code, f"{name}: {value['code']}, expected {code}")
        require(value["status"] == ("Completed" if exit_code == 0 else "Failed"), name)
        require(bool(result.stderr) == (exit_code != 0), f"{name}: stderr classification")
        return value


def compare_checkpoint(expected, actual, label):
    # Operation indices restart at zero when a resumed run begins.
    for field in ("session", "state", "stateHash", "result"):
        require(expected[field] == actual[field], f"{label}: {field}")


def verify(evidence):
    output = evidence.output
    evidence.invoke("legacy", demo=True)
    baseline = evidence.invoke("fixed", "run", "--scenario", EXAMPLES / "scenario.json", "--seed", "42",
                               "--commands", EXAMPLES / "commands.json", "--snapshot-out", "final.snapshot.json",
                               "--replay-out", "recorded.replay.json", code="RunCompleted")
    expected = read(EXAMPLES / "run-result.json")
    for field in expected.keys() - {"message", "artifacts"}:
        require(expected[field] == baseline[field], f"fixed fixture: {field}")
    for index in range(1, 10):
        repeated = evidence.invoke(f"repeat-{index}", "run", "--scenario", EXAMPLES / "scenario.json", "--seed", "42",
                                   "--commands", EXAMPLES / "commands.json", code="RunCompleted")
        for field in baseline.keys() - {"message", "artifacts"}:
            require(repeated[field] == baseline[field], f"repeat {index}: {field}")
    replay = evidence.invoke("matched", "replay", "--input", "recorded.replay.json", code="ReplayMatched")
    require(replay["comparedCheckpoints"] == 16, "matched count")
    require(replay["checkpoints"] == baseline["checkpoints"], "replayed actual checkpoints")
    inspected = evidence.invoke("inspect", "inspect", "--snapshot", "final.snapshot.json", code="SnapshotInspected")
    for field in ("session", "state", "stateHash"):
        require(inspected["initialCheckpoint"][field] == baseline["finalCheckpoint"][field], f"inspect: {field}")

    # The six original demo commands, each followed by one advance (no queued prefix).
    commands = [
        {"type": "Move", "side": "A", "unitId": 1, "direction": "Right"},
        {"type": "Attack", "side": "A", "unitId": 1, "targetId": 2},
        {"type": "EndTurn", "side": "A"},
        {"type": "Attack", "side": "B", "unitId": 2, "targetId": 1},
        {"type": "EndTurn", "side": "B"},
        {"type": "Attack", "side": "A", "unitId": 1, "targetId": 2},
    ]
    operations = [op for command in commands for op in ({"op": "submit", "command": command}, {"op": "advance"})]
    write(output / "demo.commands.json", {"commandFormatVersion": 1, "operations": operations})
    continuous = evidence.invoke("continuous", "run", "--scenario", EXAMPLES / "scenario.json", "--seed", "42",
                                 "--commands", "demo.commands.json", code="RunCompleted")
    require(continuous["finalCheckpoint"]["state"] == baseline["finalCheckpoint"]["state"], "six-step final state")
    for boundary in range(7):
        prefix = f"boundary-{boundary}"
        scenario = output / f"{prefix}.scenario.json"
        shutil.copyfile(EXAMPLES / "scenario.json", scenario)
        write(output / f"{prefix}.first.json", {"commandFormatVersion": 1, "operations": operations[:2 * boundary]})
        write(output / f"{prefix}.rest.json", {"commandFormatVersion": 1, "operations": operations[2 * boundary:]})
        first = evidence.invoke(prefix + "-save", "run", "--scenario", scenario, "--seed", "42",
                                "--commands", f"{prefix}.first.json", "--snapshot-out", f"{prefix}.snapshot.json", code="RunCompleted")
        scenario.unlink()
        resumed = evidence.invoke(prefix + "-resume", "run", "--snapshot", f"{prefix}.snapshot.json",
                                  "--commands", f"{prefix}.rest.json", "--replay-out", f"{prefix}.replay.json", code="RunCompleted")
        expected_boundary = continuous["initialCheckpoint"] if boundary == 0 else continuous["checkpoints"][2 * boundary - 1]
        require(first["checkpoints"] == continuous["checkpoints"][:2 * boundary], f"{prefix}: prefix checkpoints")
        for field in ("session", "state", "stateHash"):
            require(first["finalCheckpoint"][field] == expected_boundary[field], f"{prefix}: saved {field}")
            require(resumed["initialCheckpoint"][field] == expected_boundary[field], f"{prefix}: restored {field}")
        suffix = continuous["checkpoints"][2 * boundary:]
        require(len(suffix) == len(resumed["checkpoints"]), prefix)
        for index, (left, right) in enumerate(zip(suffix, resumed["checkpoints"])):
            compare_checkpoint(left, right, f"{prefix}: checkpoint {index}")
        recorded = evidence.invoke(prefix + "-replay", "replay", "--input", f"{prefix}.replay.json", code="ReplayMatched")
        require(recorded["comparedCheckpoints"] == len(suffix) + 1, prefix)

    rejection_fixture = ROOT / "tests/fixtures/phase-2"
    rejected = evidence.invoke("rejections", "run", "--scenario", rejection_fixture / "rejections.scenario.json",
                               "--seed", "42", "--commands", rejection_fixture / "rejections.commands.json",
                               "--replay-out", "rejections.replay.json", code="RunCompleted")
    points = rejected["checkpoints"]
    require(rejected["accepted"] == 3 and rejected["rejected"] == 3, "rejection totals")
    previous = rejected["initialCheckpoint"]
    errors = []
    for point in points:
        result = point["result"]
        if result.get("outcome") == "Rejected":
            errors.append(result["errorCode"])
            require(result["events"] == [] and point["stateHash"] == previous["stateHash"], "rejection changed state/events")
        if result["kind"] == "empty":
            require(point["session"] == previous["session"] and point["state"] == previous["state"], "empty advance changed state/counters")
        require(int(point["session"]["lastSubmittedSequence"]) - int(point["session"]["completedStepCount"]) == point["session"]["pendingCount"], "queue counts")
        previous = point
    require(errors == ["Blocked", "NoActionPoints", "NotYourTurn"], "business rejection order")
    require(points[6]["session"]["pendingCount"] == 6, "six commands must be queued before advance")
    matched = evidence.invoke("rejections-replay", "replay", "--input", "rejections.replay.json", code="ReplayMatched")
    require(matched["checkpoints"] == points, "rejection replay")

    record = read(output / "recorded.replay.json")
    altered = copy.deepcopy(record)
    altered["checkpoints"][5]["result"]["events"][0]["to"]["x"] = 0
    write(output / "event-divergence.replay.json", altered)
    divergence = evidence.invoke("event-divergence", "replay", "--input", "event-divergence.replay.json",
                                 exit_code=3, code="CheckpointMismatch")
    for field, value in read(EXAMPLES / "divergence-result.json").items():
        if field != "message":
            require(divergence[field] == value, f"divergence fixture: {field}")
    altered = copy.deepcopy(record)
    altered["commandFile"]["operations"][3]["command"]["direction"] = "Up"
    write(output / "command-divergence.replay.json", altered)
    changed = evidence.invoke("command-divergence", "replay", "--input", "command-divergence.replay.json",
                              exit_code=3, code="CheckpointMismatch")
    require(changed["location"]["operationIndex"] == 5 and changed["comparedCheckpoints"] == 6, "first command divergence")
    require(len(changed["checkpoints"]) == 6, "must stop at first divergence")
    altered = copy.deepcopy(record)
    altered["checkpoints"].pop()
    write(output / "damaged.replay.json", altered)
    damaged = evidence.invoke("damaged", "replay", "--input", "damaged.replay.json", exit_code=2, code="InvalidReplay")
    require(damaged["initialCheckpoint"] is None, "damaged record must not execute")
    # These labels exercise evidence comparison only; they do not claim a second runtime executed this file.
    other_source = copy.deepcopy(record)
    other_source["initialSnapshot"]["environment"] = {
        "runtimeVersion": "comparison-test-runtime", "os": "comparison-test-os",
        "architecture": "comparison-test-architecture", "buildHash": "a" * 64,
    }
    write(output / "other-source.replay.json", other_source)
    compared = evidence.invoke("compare-other-source", "compare", "--expected", "recorded.replay.json",
                               "--actual", "other-source.replay.json", code="HostTracesMatched")
    require(compared["comparedCheckpoints"] == len(record["checkpoints"]) + 1, "compare all checkpoints")
    require(compared["initialCheckpoint"] is None and not compared["checkpoints"]
            and compared["accepted"] == 0 and compared["rejected"] == 0, "compare must not claim execution")
    evidence.invoke("replay-other-source", "replay", "--input", "other-source.replay.json",
                    exit_code=2, code="EnvironmentMismatch")
    compared = evidence.invoke("compare-events", "compare", "--expected", "recorded.replay.json",
                               "--actual", "event-divergence.replay.json", exit_code=3, code="CheckpointMismatch")
    require(compared["location"]["operationIndex"] == 5 and compared["comparedCheckpoints"] == 6,
            "compare first event divergence")
    evidence.invoke("compare-inputs", "compare", "--expected", "recorded.replay.json",
                    "--actual", "command-divergence.replay.json", exit_code=2, code="ComparisonInputMismatch")
    evidence.invoke("compare-damaged", "compare", "--expected", "recorded.replay.json",
                    "--actual", "damaged.replay.json", exit_code=2, code="InvalidReplay")
    return record["initialSnapshot"]["environment"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, default=ROOT / "src/AiNative.Headless/bin/Debug/net10.0/AiNative.Headless.dll")
    parser.add_argument("--output", type=Path, required=True, help="new directory; existing evidence is never overwritten")
    args = parser.parse_args()
    host, output = args.host.resolve(), args.output.resolve()
    if not host.is_file():
        parser.error("host assembly is missing; run dotnet build first")
    if output.exists():
        parser.error("output directory already exists; choose a new path")
    output.mkdir(parents=True)
    evidence = Evidence(host, output)
    summary = {"status": "Failed", "host": str(host), "calls": evidence.calls}
    try:
        summary["pythonVersion"] = sys.version.split()[0]
        summary["sdkVersion"] = subprocess.check_output(["dotnet", "--version"], cwd=ROOT, text=True).strip()
        summary["sourceRevision"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        summary["workingTreeChanges"] = subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).splitlines()
        summary["environment"] = verify(evidence)
        summary["status"] = "Passed"
    except (AssertionError, OSError, ValueError, subprocess.SubprocessError, KeyError) as error:
        summary["error"] = str(error)
        print(f"Phase 2 acceptance failed: {error}", file=sys.stderr)
    finally:
        write(output / "summary.json", summary)
    print(f"Phase 2 acceptance: {summary['status']} ({len(evidence.calls)} CLI processes); evidence: {output}")
    return 0 if summary["status"] == "Passed" else 1


if __name__ == "__main__":
    sys.exit(main())
