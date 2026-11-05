"""Prepare the GridGame Editor project or verify its records against the headless CLI."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "examples/unity/GridGame"
DATA = ROOT / "examples/data"
ADAPTER = ROOT / "examples/AiNative.GridGame.Hosting"
RUNNER = ROOT / "examples/AiNative.Examples.Runner"

# Unity's Mono profile provides the remaining framework/facade assemblies.
PLUGINS = (
    "AiNative.Core", "AiNative.Hosting", "AiNative.GridGame", "AiNative.GridGame.Hosting",
    "System.Text.Json", "System.Text.Encodings.Web", "System.IO.Pipelines", "Microsoft.Bcl.AsyncInterfaces",
    "System.Runtime.CompilerServices.Unsafe",
)


def prepare():
    subprocess.run(["dotnet", "restore", str(ADAPTER), "--locked-mode"], cwd=ROOT, check=True)
    target = PROJECT / "Assets/Editor/Plugins"
    target.mkdir(parents=True, exist_ok=True)
    expected = {name + ".dll" for name in PLUGINS}
    extra = {path.name for path in target.glob("*.dll")} - expected
    if extra:
        raise ValueError(f"Unexpected Editor plugins in {target}: {sorted(extra)}")
    with tempfile.TemporaryDirectory() as publish:
        subprocess.run(["dotnet", "publish", str(ADAPTER), "-c", "Release", "--no-restore",
                        "-o", publish], cwd=ROOT, check=True)
        for filename in sorted(expected):
            source, dest = Path(publish) / filename, target / filename
            if not dest.exists() or source.read_bytes() != dest.read_bytes():
                shutil.copyfile(source, dest)
    scenario = DATA / "grid.scenario.json"
    bundled = PROJECT / "Assets/StreamingAssets" / scenario.name
    bundled.parent.mkdir(parents=True, exist_ok=True)
    if not bundled.exists() or bundled.read_bytes() != scenario.read_bytes():
        shutil.copyfile(scenario, bundled)


def verify(output):
    output.mkdir(parents=True, exist_ok=False)
    scene, commands = (output / name for name in ("grid.scenario.json", "grid.commands.json"))
    for path in (scene, commands):
        shutil.copyfile(DATA / path.name, path)
    subprocess.run(["dotnet", "restore", str(RUNNER), "--locked-mode"], cwd=ROOT, check=True)
    subprocess.run(["dotnet", "build", str(RUNNER), "-c", "Release", "--no-restore"], cwd=ROOT, check=True)
    runner = RUNNER / "bin/Release/net10.0/AiNative.Examples.Runner.dll"

    def cli(name, args, code, exit_code=0):
        proc = subprocess.run(["dotnet", str(runner), *args], cwd=ROOT, capture_output=True, timeout=60)
        (output / f"{name}.stdout.json").write_bytes(proc.stdout)
        (output / f"{name}.stderr.txt").write_bytes(proc.stderr)
        result = json.loads(proc.stdout)
        if proc.returncode != exit_code or result["code"] != code:
            raise RuntimeError(f"{name}: exit={proc.returncode}, code={result['code']}, see {output}")
        return result

    # The Editor receives only the input files. It cannot read a CLI baseline that does not yet exist.
    subprocess.run(["unity", "run", str(PROJECT), "--timeout", "300", "--no-tail", "--log-file",
                    str(output / "editor.log"), "--", "-executeMethod", "AiNative.Examples.GridGame.Editor.GridEditorAcceptance.Run"],
                   cwd=ROOT, env={**os.environ, "AINATIVE_GRID_EVIDENCE": str(output)}, check=True)
    baseline = output / "cli.replay.json"
    run = cli("run", ["run", "--scenario", scene, "--seed", "42", "--commands", commands, "--replay-out", baseline], "RunCompleted")
    summary = {"unityProject": PROJECT.relative_to(ROOT).as_posix(),
               "inputs": {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (scene, commands)},
               "seed": 42, "accepted": run["accepted"], "rejected": run["rejected"],
               "winner": run["observation"]["winner"], "comparisons": {}}
    ticks = [cp for cp in run["checkpoints"] if cp["result"]["kind"] == "tick"]
    summary["tickHashes"] = {cp["runtime"]["tick"]: cp["stateHash"] for cp in ticks}
    if len(ticks) != 8 or (run["accepted"], run["rejected"], summary["winner"]) != (6, 1, "A"):
        raise RuntimeError("CLI did not complete the expected match")
    expected_count = len(run["checkpoints"]) + 1
    for speed in ("fast", "slow"):
        replay = output / f"editor-{speed}.replay.json"
        compared = cli(f"compare-{speed}", ["compare", "--expected", baseline, "--actual", replay], "HostTracesMatched")
        if compared["comparedCheckpoints"] != expected_count:
            raise RuntimeError(f"{speed}: incomplete comparison")
        summary["comparisons"][speed] = {"code": compared["code"], "checkpoints": compared["comparedCheckpoints"]}

    # Change only a legal business error in a copy to prove the comparison detects a result-only divergence.
    editor = json.loads((output / "editor-fast.replay.json").read_bytes())
    summary["unityEnvironment"] = editor["initialSnapshot"]["environment"]
    summary["cliEnvironment"] = json.loads(baseline.read_bytes())["initialSnapshot"]["environment"]
    for name in PLUGINS:
        plugin = PROJECT / "Assets/Editor/Plugins" / (name + ".dll")
        if summary["unityEnvironment"]["components"].get(name) != hashlib.sha256(plugin.read_bytes()).hexdigest():
            raise RuntimeError(f"Editor did not load the staged {name} assembly")
    rejection = next(cp for cp in editor["checkpoints"] if any(
        not item["accepted"] for item in cp["result"].get("inputs", [])))
    rejection["result"]["inputs"][0]["error"] = "Blocked"
    changed = output / "changed-error.replay.json"
    changed.write_text(json.dumps(editor), encoding="utf-8")
    divergent = cli("compare-changed", ["compare", "--expected", baseline, "--actual", changed], "CheckpointMismatch", 3)
    if (divergent["location"]["operationIndex"] != rejection["operationIndex"]
            or divergent["divergence"][0]["path"] != "$.result.inputs[0].error"):
        raise RuntimeError("compare failed to locate the changed rejection")
    summary["negativeControl"] = {"code": divergent["code"], "operationIndex": rejection["operationIndex"],
                                  "tick": rejection["runtime"]["tick"], "difference": divergent["divergence"][0]}
    (output / "summary.json").write_bytes((json.dumps(summary, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
    print(f"{len(ticks)} ticks, {expected_count} checkpoints, fast/slow Editor matched CLI. Evidence: {output}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "verify"))
    parser.add_argument("--output", type=Path, help="new evidence directory (required for verify)")
    args = parser.parse_args()
    if args.action == "verify" and args.output is None:
        parser.error("verify requires --output")
    prepare()
    if args.action == "verify":
        verify(args.output.resolve())


if __name__ == "__main__":
    main()
