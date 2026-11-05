"""Real PlayMode and independent CLI evidence for the fixed-step PhysicsSandbox."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

from unity_physics import DATA, MANAGED, PROJECT, ROOT, RUNNER


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def read(path):
    return json.loads(path.read_bytes())


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def tools_build():
    subprocess.run(["dotnet", "restore", str(RUNNER), "--locked-mode"], cwd=ROOT, check=True)
    subprocess.run(["dotnet", "build", str(RUNNER), "-c", "Release", "--no-restore"], cwd=ROOT, check=True)
    return RUNNER / "bin/Release/net10.0/AiNative.Examples.Runner.dll"


def play(project, editor, output, reload):
    output.mkdir()
    settings = project / "ProjectSettings/EditorSettings.asset"
    text = settings.read_text(encoding="utf-8")
    for key, value in (("m_EnterPlayModeOptionsEnabled", "1"), ("m_EnterPlayModeOptions", str(int(reload == "off")))):
        text, count = re.subn(rf"(?m)^  {key}: \d+$", f"  {key}: {value}", text)
        require(count == 1, f"Missing setting {key}")
    settings.write_text(text, encoding="utf-8")
    env = {**os.environ, "AINATIVE_DOMAIN_RELOAD": reload, "AINATIVE_PHYSICS_EVIDENCE": str(output)}
    subprocess.run([editor, "-projectPath", str(project), "-runTests", "-testPlatform", "PlayMode",
                    "-testResults", str(output / "play.xml"), "-logFile", str(output / "editor.log")],
                   cwd=ROOT, env=env, check=True, timeout=360)
    tests = ET.parse(output / "play.xml").findall(".//test-case")
    required = {"UniformSteps", "CatchupAndRestoreBothBoundaries", "FractionalPauseAndFailedReplacement", "PausedInputAndDebt",
                "CompletedStepCannotRecursivelyPumpOrReplace", "FileSaveAndPausedRestore", "GameViewPointerQueuesInputWhilePaused"}
    require(required <= {case.get("name") for case in tests} and all(case.get("result") == "Passed" for case in tests),
            f"Incomplete PlayMode tests: {output}")
    float_cases = [case for case in tests if case.get("name", "").startswith("CommandFloatHasTheCliBitsAndSpelling(")]
    require(len(float_cases) == 4, "Missing Mono binary32 parsing/formatting cases")
    for tick in (90, 91):
        png = output / f"restored-{tick}.png"
        require(png.read_bytes()[:8] == b"\x89PNG\r\n\x1a\n" and png.stat().st_size > 100, f"Missing capture at {tick}")
    cycle_xml = output / "exit.xml"
    subprocess.run([editor, "-projectPath", str(project), "-runTests", "-testPlatform", "EditMode",
                    "-testFilter", "WorldLifetimeTests", "-testResults", str(cycle_xml),
                    "-logFile", str(output / "exit.log")], cwd=ROOT, env=env, check=True, timeout=300)
    cycles = ET.parse(cycle_xml).findall(".//test-case")
    require(len(cycles) == 1 and cycles[0].get("result") == "Passed", f"Failed to verify Play exit: {output}")
    return sorted(case.get("name") for case in tests + cycles)


def pause_commands():
    operations = [{"op": "advance"} for _ in range(4)]
    for command in ({"type": "Impulse", "id": "2", "impulse": [1, 0, 0]}, {"type": "Remove", "id": "999"},
                    {"type": "Impulse", "id": "2", "impulse": [0, 1, 0]}):
        operations.append({"op": "submit", "command": command})
    operations.extend({"op": "advance"} for _ in range(6))
    return {"formatVersion": 2, "operations": operations}


def check_suffix(full, resumed, tick, boundary):
    original = full["checkpoints"][boundary - 1]
    initial = resumed["initialCheckpoint"]
    for key in ("runtime", "state", "stateHash"):
        require(original[key] == initial[key], f"Restore point differs at Tick {tick}: {key}")
    suffix = full["checkpoints"][boundary:]
    require(len(suffix) == len(resumed["checkpoints"]), f"Incomplete suffix at {tick}")
    for i, (expected, actual) in enumerate(zip(suffix, resumed["checkpoints"])):
        require({**expected, "operationIndex": i} == actual, f"Continuation differs from uninterrupted run at {tick} + op {i}")


def verify(output):
    output.mkdir(parents=True, exist_ok=False)
    host = tools_build()
    scene = output / "physics.scenario.json"
    commands = output / "physics.commands.json"
    shutil.copyfile(DATA / scene.name, scene)
    shutil.copyfile(DATA / commands.name, commands)
    pause = output / "pause.commands.json"
    write(pause, pause_commands())
    ops = read(commands)["operations"]
    advances = [i + 1 for i, op in enumerate(ops) if op["op"] == "advance"]
    boundaries = {tick: advances[tick - 1] for tick in (90, 91)}
    for tick, pos in boundaries.items():
        for prefix, vector in (("before", ops[:pos]), ("after", ops[pos:])):
            write(output / f"{prefix}-{tick}.json", {"formatVersion": 2, "operations": vector})
    summary = {"scene": scene.name, "seed": 42, "workerThreads": 2, "tests": {}, "comparisons": {}, "negative": {},
               "inputs": {file.name: hashlib.sha256(file.read_bytes()).hexdigest() for file in (scene, commands, pause)}}

    def cli(label, args, code, exit_code=0):
        proc = subprocess.run(["dotnet", str(host), *map(str, args)], cwd=ROOT, capture_output=True, timeout=120)
        (output / (label + ".stdout.json")).write_bytes(proc.stdout)
        (output / (label + ".stderr.txt")).write_bytes(proc.stderr)
        payload = json.loads(proc.stdout)
        with (output / "commands.jsonl").open("a", encoding="utf-8") as index:
            index.write(json.dumps({"command":proc.args, "exitCode":proc.returncode, "code":payload["code"]}) + "\n")
        require(proc.returncode == exit_code and payload["code"] == code, f"{label}: {payload['code']} ({payload['location']})")
        return payload

    baseline = output / "cli.replay.json"
    base_args = ["run", "--scenario", scene, "--seed", "42", "--commands"]
    cli("baseline", [*base_args, commands, "--replay-out", baseline], "RunCompleted")
    pause_record = output / "cli-pause.replay.json"
    cli("baseline-pause", [*base_args, pause, "--replay-out", pause_record], "RunCompleted")
    full = read(baseline)
    require(len(full["checkpoints"]) == 244, "Expected 240 ticks and four submits")
    for tick, pos in boundaries.items():
        snapshot = output / f"cli-{tick}.snapshot.json"
        resumed = output / f"cli-from-{tick}.replay.json"
        cli(f"save-{tick}", [*base_args, output / f"before-{tick}.json", "--snapshot-out", snapshot], "RunCompleted")
        cli(f"resume-{tick}", ["run", "--snapshot", snapshot, "--commands", output / f"after-{tick}.json",
                               "--replay-out", resumed], "RunCompleted")
        check_suffix(full, read(resumed), tick, pos)

    inventory = json.loads(subprocess.check_output(["unity", "editors", "--installed", "--format", "json"]))
    version = (PROJECT / "ProjectSettings/ProjectVersion.txt").read_text().splitlines()[0].split()[1]
    editor = next(x["location"] for x in inventory["data"]["items"] if x["version"] == version)
    summary["editor"] = version
    with tempfile.TemporaryDirectory(prefix="ainative-sandbox-") as tmp:
        project = Path(tmp) / "PhysicsSandbox"
        for directory in ("Assets", "Packages", "ProjectSettings"):
            shutil.copytree(PROJECT / directory, project / directory)
        for reload in ("on", "off"):
            report = output / ("reload-" + reload)
            summary["tests"][reload] = play(project, editor, report, reload)
            unity_full = read(report / "catchup.replay.json")
            for mode in ("uniform", "catchup", "pause", "from-90", "from-91"):
                actual = report / (mode + ".replay.json")
                expected = baseline if mode in ("uniform", "catchup") else pause_record if mode == "pause" else output / f"cli-{mode}.replay.json"
                result = cli(f"compare-{reload}-{mode}", ["compare", "--expected", expected, "--actual", actual], "HostTracesMatched")
                count = len(read(expected)["checkpoints"]) + 1
                require(result["comparedCheckpoints"] == count, f"Incomplete {mode} comparison")
                summary["comparisons"][f"{reload}-{mode}"] = count
            for tick, pos in boundaries.items():
                check_suffix(unity_full, read(report / f"from-{tick}.replay.json"), tick, pos)
            uniform = read(report / "uniform.frames.json")
            catchup = read(report / "catchup.frames.json")
            require(all(frame["after"] - frame["before"] == 1 for frame in uniform) and
                    any(frame["after"] - frame["before"] == 4 for frame in catchup), "Frame scheduling was not exercised")
            for mode in ("uniform", "catchup", "pause", "from-90", "from-91"):
                frames = read(report / (mode + ".frames.json"))
                require(all(f["renderToTick"] == f["after"] and f["renderToTick"] - f["renderFromTick"] in (0, 1)
                            and 0 <= f["renderFraction"] <= 1 for f in frames), f"Unexpected rendered interval: {mode}")
            pause_frames = read(report / "pause.frames.json")
            require([f["after"] for f in pause_frames] == [4, 4, 4, 4, 8, 10, 10] and
                    [f["submitted"] for f in pause_frames if f["paused"]] == [[1], [2], [3]], "Wrong paused input schedule")
            components = unity_full["initialSnapshot"]["environment"]["components"]
            for name in MANAGED:
                plugin = project / "Assets/Plugins/AiNative" / (name + ".dll")
                require(hashlib.sha256(plugin.read_bytes()).hexdigest() == components[name], f"Wrong loaded {name}")
            for name in ("ainative_jolt.dll", "ainative_jolt.build"):
                require(components[name] == full["initialSnapshot"]["environment"]["components"][name], f"Native identity differs: {name}")
            changed = read(report / "pause.replay.json")
            refusal = changed["checkpoints"][7]
            require(refusal["runtime"]["tick"] == "5" and refusal["result"]["inputs"][1]["error"] == "UnknownBody", "Bad pause vector")
            refusal["result"]["inputs"][1]["error"] = "StaticBody"
            damaged = report / "changed-error.json"
            write(damaged, changed)
            result = cli("negative-" + reload, ["compare", "--expected", pause_record, "--actual", damaged], "CheckpointMismatch", 3)
            diff = result["divergence"][0]
            require(result["location"]["operationIndex"] == 7 and diff["path"] == "$.result.inputs[1].error", "Wrong first divergence")
            summary["negative"][reload] = {"operationIndex": 7, "tick": 5, "difference": diff}
    summary["unityEnvironment"] = unity_full["initialSnapshot"]["environment"]
    summary["cliEnvironment"] = full["initialSnapshot"]["environment"]
    write(output / "summary.json", summary)
    print(f"All PlayMode runs, restore continuations and ten CLI comparisons matched. Evidence: {output}")
