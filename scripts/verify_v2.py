"""Real-process v2 acceptance. Every command, stream, input and artifact is retained."""
import argparse
import base64
import copy
import ctypes
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
    return path


def require(condition, message):
    if not condition:
        raise AssertionError(message)


class Evidence:
    def __init__(self, host, output):
        self.host, self.output, self.calls = host, output, []

    def invoke(self, name, *arguments, exit_code=0, code=None, host=None):
        command = ["dotnet", str(host or self.host), *map(str, arguments)]
        entry = {"name": name, "command": command, "expectedExitCode": exit_code}
        self.calls.append(entry)
        result = subprocess.run(command, cwd=self.output, capture_output=True, timeout=60)
        (self.output / (name + ".stdout.json")).write_bytes(result.stdout)
        (self.output / (name + ".stderr.txt")).write_bytes(result.stderr)
        entry["exitCode"] = result.returncode
        require(result.returncode == exit_code, f"{name}: exit {result.returncode}: {result.stderr.decode('utf-8', errors='replace')}")
        payload = json.loads(result.stdout)
        entry["code"] = payload["code"]
        require(payload["formatVersion"] == 2, name + ": wrong output version")
        if code:
            require(payload["code"] == code, f"{name}: {payload['code']} != {code}")
        require(bool(result.stderr) == (exit_code != 0), name + ": diagnostic channel")
        return payload

    def run(self, name, scene, commands, snapshot=None, replay=True, seed=42):
        saved, record = self.output / (name + ".snapshot.json"), self.output / (name + ".replay.json")
        arguments = ["run", "--snapshot", snapshot] if snapshot else ["run", "--scenario", scene, "--seed", str(seed)]
        arguments += ["--commands", commands, "--snapshot-out", saved]
        if replay:
            arguments += ["--replay-out", record]
        return self.invoke(name, *arguments), saved, record


def native_probe(library, snapshot):
    """Child process: all truncation offsets must fail promptly and release candidate worlds."""
    native = ctypes.CDLL(str(library))
    native.aj_restore.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_void_p)]
    native.aj_restore.restype = ctypes.c_int
    native.aj_destroy.argtypes = [ctypes.c_void_p]
    native.aj_world_count.restype = ctypes.c_uint32
    state = read(snapshot)["state"]["physics"]
    data = base64.b64decode(state["data"])
    buffer = ctypes.create_string_buffer(data)
    for length in range(1, len(data)):
        candidate = ctypes.c_void_p()
        require(native.aj_restore(buffer, length, ctypes.byref(candidate)) == -1, f"truncation {length} accepted")
        require(not candidate.value and native.aj_world_count() == 0, f"resource leak at {length}")
    # Every single-byte mutation is either rejected cleanly or yields a disposable complete world.
    for offset in range(len(data)):
        changed = bytearray(data)
        changed[offset] ^= 0xFF
        candidate = ctypes.c_void_p()
        status = native.aj_restore(ctypes.create_string_buffer(bytes(changed)), len(changed), ctypes.byref(candidate))
        if status == 0:
            native.aj_destroy(candidate)
        require(native.aj_world_count() == 0, f"mutation leak at {offset}")
    print(json.dumps({"truncationOffsets": len(data) - 1, "mutationOffsets": len(data), "worlds": 0}))


def validate(host, output):
    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(host, output)
    try:
        inputs = output / "inputs"
        shutil.copytree(ROOT / "examples/data", inputs)
        scene, commands = inputs / "grid.scenario.json", inputs / "grid.commands.json"
        evidence.invoke("games", "games", code="GamesListed")
        for game in ("grid-game", "physics-sandbox"):
            evidence.invoke("describe-" + game, "describe", "--game", game, code="GameDescribed")
        # Phase-two legacy default-demo case now uses an explicit registered example.
        evidence.run("legacy-demo-explicit", scene, commands)
        fixed, saved, record = evidence.run("fixed", scene, commands)
        require(fixed["observation"]["winner"] == "A" and fixed["accepted"] == 6 and fixed["rejected"] == 1, "fixed gameplay vector")
        # Independent gameplay vectors, not a second execution used as its own oracle.
        tick_states = [cp for cp in fixed["checkpoints"] if cp["result"]["kind"] == "tick"]
        vectors = [(0, 1, "A", 2, 4, 4), (1, 1, "A", 2, 4, 4), (3, 1, "A", 0, 4, 2),
                   (3, 1, "A", 0, 4, 2), (4, 2, "B", 2, 4, 2), (5, 2, "B", 1, 2, 2),
                   (6, 3, "A", 2, 2, 2), (7, 3, "A", 1, 2, 0)]
        require(len(tick_states) == len(vectors), "fixed tick count")
        for tick, (checkpoint, vector) in enumerate(zip(tick_states, vectors), 1):
            processed, turn, side, points, health_a, health_b = vector
            state = checkpoint["state"]
            require(checkpoint["runtime"]["tick"] == str(tick) and checkpoint["runtime"]["processed"] == str(processed)
                    and state["turnIndex"] == str(turn) and state["activeSide"] == side and state["actionPoints"] == points
                    and [unit["health"] for unit in state["units"]] == [health_a, health_b], f"fixed tick {tick}")
        for repeat in range(1, 10):
            repeated, _, _ = evidence.run(f"repeat-{repeat}", scene, commands)
            require(repeated["checkpoints"] == fixed["checkpoints"], "repeated execution differs")
        evidence.invoke("matched-replay", "replay", "--input", record, code="ReplayMatched")
        evidence.invoke("inspect", "inspect", "--snapshot", saved, code="SnapshotInspected")
        continuous, _, _ = evidence.run("continuous", scene, commands)
        operations = read(commands)["operations"]
        boundaries = [0] + [index + 1 for index, operation in enumerate(operations) if operation["op"] == "advance"]
        # Retain the seven historical boundary cases; extra v2 empty/multi-input ticks are covered too.
        for number, boundary in enumerate(boundaries):
            prefix = write(inputs / f"grid-prefix-{number}.json", {"formatVersion": 2, "operations": operations[:boundary]})
            suffix = write(inputs / f"grid-suffix-{number}.json", {"formatVersion": 2, "operations": operations[boundary:]})
            _, checkpoint, _ = evidence.run(f"boundary-{number}-save", scene, prefix)
            resumed, _, resumed_record = evidence.run(f"boundary-{number}-resume", scene, suffix, snapshot=checkpoint)
            require(resumed["finalCheckpoint"]["stateHash"] == continuous["finalCheckpoint"]["stateHash"], "save/resume differs")
            evidence.invoke(f"boundary-{number}-replay", "replay", "--input", resumed_record, code="ReplayMatched")
        rejection_ops = [{"op": "submit", "command": {"type": "Move", "side": "A", "unitId": -1, "direction": "Right"}}, {"op": "advance"}]
        rejected, _, rejection_record = evidence.run("rejections", scene, write(inputs / "rejections.json", {"formatVersion": 2, "operations": rejection_ops}))
        require(rejected["rejected"] == 1, "business rejection missing")
        evidence.invoke("rejections-replay", "replay", "--input", rejection_record, code="ReplayMatched")
        baseline = read(record)
        deviated = copy.deepcopy(baseline)
        first_event = next(cp for cp in deviated["checkpoints"] if cp["result"].get("events"))
        first_event["result"]["events"] = []
        event_record = write(inputs / "events.json", deviated)
        divergence = evidence.invoke("event-divergence", "replay", "--input", event_record, exit_code=3, code="CheckpointMismatch")
        require(divergence["location"]["operationIndex"] == first_event["operationIndex"], "not first divergence")
        altered = copy.deepcopy(baseline)
        altered["commands"]["operations"][1]["command"]["side"] = "A"
        command_record = write(inputs / "changed-command.json", altered)
        evidence.invoke("command-divergence", "replay", "--input", command_record, exit_code=3, code="CheckpointMismatch")
        damaged = copy.deepcopy(baseline)
        damaged["checkpoints"][0]["stateHash"] = "0" * 64
        damaged_record = write(inputs / "damaged.json", damaged)
        evidence.invoke("damaged", "replay", "--input", damaged_record, exit_code=2)
        other = copy.deepcopy(baseline)
        other["initialSnapshot"]["environment"]["runtime"] = "other-source"
        other_record = write(inputs / "other-source.json", other)
        evidence.invoke("compare-other-source", "compare", "--expected", record, "--actual", other_record, code="HostTracesMatched")
        evidence.invoke("replay-other-source", "replay", "--input", other_record, exit_code=2, code="EnvironmentMismatch")
        evidence.invoke("compare-events", "compare", "--expected", record, "--actual", event_record, exit_code=3, code="CheckpointMismatch")
        evidence.invoke("compare-inputs", "compare", "--expected", record, "--actual", command_record, exit_code=2, code="ComparisonInputMismatch")
        evidence.invoke("compare-damaged", "compare", "--expected", record, "--actual", damaged_record, exit_code=2)
        old = write(inputs / "v1.json", {"snapshotFormatVersion": 1})
        evidence.invoke("v1-rejected", "inspect", "--snapshot", old, exit_code=2, code="UnsupportedVersion")
        pscene, pcommands = inputs / "physics.scenario.json", inputs / "physics.commands.json"
        physics, psaved, precord = evidence.run("physics", pscene, pcommands)
        require(physics["finalCheckpoint"]["runtime"]["tick"] == "240", "physics tick vector")
        require(any(event["type"] == "ContactStarted" for cp in physics["checkpoints"] for event in cp["result"].get("events", [])), "missing real collision evidence")
        evidence.invoke("physics-replay", "replay", "--input", precord, code="ReplayMatched")
        physics2, _, precord2 = evidence.run("physics-repeat", pscene, pcommands)
        require(physics2["checkpoints"] == physics["checkpoints"], "physics independent processes differ")
        evidence.invoke("physics-compare", "compare", "--expected", precord, "--actual", precord2, code="HostTracesMatched")
        pops = read(pcommands)["operations"]
        advance_positions = [index + 1 for index, operation in enumerate(pops) if operation["op"] == "advance"]
        for tick in (0, 1, 30, 90, 91, 240):
            boundary = 0 if tick == 0 else advance_positions[tick - 1]
            prefix = write(inputs / f"physics-prefix-{tick}.json", {"formatVersion": 2, "operations": pops[:boundary]})
            suffix = write(inputs / f"physics-suffix-{tick}.json", {"formatVersion": 2, "operations": pops[boundary:]})
            _, checkpoint, _ = evidence.run(f"physics-{tick}-save", pscene, prefix)
            resumed, _, resumed_record = evidence.run(f"physics-{tick}-resume", pscene, suffix, snapshot=checkpoint)
            require(resumed["finalCheckpoint"]["stateHash"] == physics["finalCheckpoint"]["stateHash"], "physics continuation differs")
            evidence.invoke(f"physics-{tick}-replay", "replay", "--input", resumed_record, code="ReplayMatched")
        # A separate process copy proves optional native loading and actual native version rejection.
        isolated = output / "isolated-runner"
        shutil.copytree(host.parent, isolated, ignore=shutil.ignore_patterns("ainative_jolt.dll", "libainative_jolt.so"))
        isolated_host = isolated / host.name
        evidence.invoke("grid-without-native", "run", "--scenario", scene, "--seed", "42", "--commands", commands, host=isolated_host)
        evidence.invoke("compare-without-native", "compare", "--expected", precord, "--actual", precord2, host=isolated_host)
        native_name = "ainative_jolt.dll" if platform.system() == "Windows" else "libainative_jolt.so"
        library = host.parent / native_name
        altered_library = library.read_bytes().replace(b"jolt-5.6.0;e77f175;abi=2", b"jolt-0.0.0;e77f175;abi=2")
        require(altered_library != library.read_bytes(), "native identity vector missing")
        (isolated / native_name).write_bytes(altered_library)
        wrong_version = evidence.invoke("wrong-native-version", "run", "--scenario", pscene, "--seed", "42", "--commands", inputs / "empty.commands.json", exit_code=1, code="NativeLibraryIncompatible", host=isolated_host)
        require("NativeLibraryVersionMismatch" in wrong_version["message"], "wrong native version was not diagnosed")
        probe = subprocess.run([sys.executable, str(Path(__file__).resolve()), "--native-probe", str(library), str(psaved)], capture_output=True, timeout=60)
        (output / "native-probe.stdout.json").write_bytes(probe.stdout)
        (output / "native-probe.stderr.txt").write_bytes(probe.stderr)
        require(probe.returncode == 0, "native corruption probe: " + probe.stderr.decode("utf-8", errors="replace"))
        write(output / "environment.json", {"platform": platform.platform(), "machine": platform.machine(),
            "dotnet": subprocess.check_output(["dotnet", "--info"], text=True, encoding="utf-8"), "native": read(host.parent / "native-build.json"),
            "gridEnvironment": read(saved)["environment"], "physicsEnvironment": read(psaved)["environment"]})
        print(f"Passed {len(evidence.calls)} real CLI processes plus isolated native corruption probe")
    finally:
        write(output / "process-index.json", evidence.calls)


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "--native-probe":
        native_probe(Path(sys.argv[2]), Path(sys.argv[3]))
    else:
        parser = argparse.ArgumentParser()
        parser.add_argument("--host", type=Path, default=ROOT / "examples/AiNative.Examples.Runner/bin/Debug/net10.0/AiNative.Examples.Runner.dll")
        parser.add_argument("--output", type=Path, default=ROOT / "TestResults/v2")
        options = parser.parse_args()
        validate(options.host.resolve(), options.output.resolve())
