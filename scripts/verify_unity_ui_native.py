"""Prepare UIDemo Players, then record a native-input session and validate window-close teardown.

Input is supplied separately through Windows Computer Use or by a person. This script
does not inject events, call UI commands, or request a normal process exit.
"""
import argparse
import json
from pathlib import Path
import subprocess
from PIL import Image

from verify_unity_ui import ROOT, PROJECT, check_log, digest, fingerprint, read_unity_version, write_json
from verify_unity_ui_players import verify as build_players


def validate_journal(path, backend, unity):
    records = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
    if not records or any(item["kind"] == "error" for item in records):
        raise RuntimeError("Empty journal or runtime errors")
    if any(item.get("backend") != backend or item.get("unity") != unity for item in records):
        raise RuntimeError("Runtime identity mismatch")
    states = [item for item in records if item["kind"] == "state" and not item["busy"] and item["uiPending"] == 0]
    # Ordered observations, not merely a set of states seen somewhere in the run.
    expected = [(1, 1, 0, "Bootstrap"), (1, 2, 0, "Bootstrap"), (1, 3, 0, "Bootstrap"),
                (1, 3, 1, "Bootstrap"), (1, 3, 2, "Bootstrap"), (1, 3, 1, "Bootstrap"),
                (1, 3, 0, "Bootstrap"), (2, 1, 0, "Bootstrap"), (2, 1, 0, "Other"), (2, 2, 0, "Other")]
    index = 0
    for state in states:
        if index < len(expected) and tuple(state[key] for key in ("session", "pages", "modals", "scene")) == expected[index]:
            if not state["running"] or state["bundles"] <= 0 or state["inputModule"] != "StandaloneInputModule":
                raise RuntimeError("Expected live bundle-backed legacy input host")
            index += 1
    if index != len(expected):
        raise RuntimeError(f"Native input sequence incomplete: {index}/{len(expected)}")
    before_quit = [item for item in records if item["kind"] == "state"][-1]
    if (not before_quit["running"] or before_quit["modelDisposed"] or before_quit["windows"] <= 0 or
            before_quit["bundles"] <= 0 or before_quit["busy"] or before_quit["uiPending"] != 0 or
            tuple(before_quit[key] for key in ("session", "pages", "modals", "scene")) != expected[-1]):
        raise RuntimeError("Final state before window close must still own the live UI session")
    inputs = [item for item in records if item["kind"] == "input" and item["focused"]]
    counts = {key: sum(bool(item[key]) for item in inputs) for key in ("mouseDown", "submitDown", "rightDown")}
    if counts["mouseDown"] < 5 or counts["submitDown"] < 3 or counts["rightDown"] < 1:
        raise RuntimeError(f"Missing focused raw native inputs: {counts}")
    if not any(item["selected"] == "Back" and item["modals"] == 1 for item in states):
        raise RuntimeError("Keyboard navigation to modal Back was not observed")
    quit_records = [item for item in records if item["kind"] == "application-quit"]
    destroy = [item for item in records if item["kind"] == "destroy"]
    if len(quit_records) != 1 or len(destroy) != 1 or records[-1]["kind"] != "destroy":
        raise RuntimeError("Missing or duplicate normal Unity exit callbacks")
    closed = destroy[0]
    if closed["running"] or not closed["modelDisposed"] or any(closed[key] != 0 for key in
            ("windows", "pages", "modals", "cache", "uiPending", "failedCleanup", "bundles", "resources", "assetPending")):
        raise RuntimeError(f"Window close did not drain host resources: {closed}")
    if closed["frame"] < states[-1]["frame"] or records.index(quit_records[0]) > records.index(closed):
        raise RuntimeError("Invalid exit ordering")
    return dict(status="Passed", backend=backend, checkpoints=index, rawInputs=counts,
                beforeQuit=before_quit, applicationQuit=quit_records[0], shutdown=closed, journalSha256=digest(path))


def validate_screenshots(journal):
    records = [json.loads(line) for line in journal.read_text(encoding="utf-8").splitlines()]
    required = [(1, 1, 0, "Bootstrap"), (1, 2, 0, "Bootstrap"), (1, 3, 2, "Bootstrap"), (2, 2, 0, "Other")]
    index = 0
    images = []
    last_input = -1
    for sample in records:
        if sample["kind"] == "input":
            last_input = sample["frame"]
        if sample["kind"] != "screenshot":
            continue
        filename = f"native-frame-{sample['frame']}.png"
        if (sample.get("screenshot") != filename or sample.get("width") != 1280 or sample.get("height") != 720 or
                sample["busy"] or sample["uiPending"] != 0 or not sample["running"]):
            raise RuntimeError("Invalid native screenshot metadata")
        path = journal.parent / filename
        with Image.open(path) as image:
            if image.format != "PNG" or image.size != (1280, 720):
                raise RuntimeError("Wrong screenshot format or size")
            image.verify()
        with Image.open(path) as image:
            image.load()
            if all(low == high for low, high in image.convert("RGB").getextrema()):
                raise RuntimeError("Blank native screenshot")
        images.append(dict(file=filename, frame=sample["frame"], sha256=digest(path), previousInputFrame=last_input))
        state = tuple(sample[key] for key in ("session", "pages", "modals", "scene"))
        if index < len(required) and state == required[index]:
            if index > 0 and (last_input < 0 or last_input >= sample["frame"]):
                raise RuntimeError("Interaction screenshot must follow an actual input frame")
            index += 1
    if index != len(required):
        raise RuntimeError(f"Missing native visual checkpoints: {index}/{len(required)}")
    return dict(checkpoints=index, viewportOnly=True, images=images)


def run(output, backend, screenshots=False):
    output = output.resolve()
    build = output / "build"
    summary = json.loads((build / "summary.json").read_text(encoding="utf-8"))
    if summary["status"] != "Passed" or fingerprint(ROOT, PROJECT) != json.loads((build / "sources.json").read_text()):
        raise RuntimeError("Build evidence must pass and match current source")
    folder = Path(summary["workspace"]) / "TestResults/ui-players" / backend
    manifest = json.loads((build / (backend + "-player-files.json")).read_text())
    if any(digest(folder / name) != value for name, value in manifest.items()):
        raise RuntimeError("Player files differ from verified build")
    session = output / backend
    session.mkdir(exist_ok=False)
    journal = session / "native.jsonl"
    log = session / "player.log"
    command = [str(folder / "UIDemo.exe"), "-force-d3d11", "-screen-fullscreen", "0", "-screen-width", "1280",
               "-screen-height", "720", "-uiNativeResult", str(journal), "-logFile", str(log)]
    if screenshots:
        command.append("-uiNativeScreenshots")
    process = subprocess.Popen(command, cwd=ROOT)
    info = dict(command=command, pid=process.pid, backend=backend, scriptSha256=digest(Path(__file__)))
    write_json(session / "launch.json", info)
    print(json.dumps(info), flush=True)
    try:
        code = process.wait(timeout=1200)
        info["exitCode"] = code
        write_json(session / "process.json", info)
        if code != 0:
            raise RuntimeError(f"Player exited {code}")
        check_log(log)
        read_unity_version(log, summary["unity"])
        result = validate_journal(journal, backend, summary["unity"])
        if screenshots:
            result["screenshots"] = validate_screenshots(journal)
        if fingerprint(ROOT, PROJECT) != json.loads((build / "sources.json").read_text()):
            raise RuntimeError("Source changed during native acceptance")
        result["inputMethod"] = "Externally supplied OS input; independently review action transcript/screenshots"
        write_json(session / "summary.json", result)
        print(json.dumps(result), flush=True)
    except Exception as error:
        # A timeout is a failure, never a substitute for clicking the window close button.
        if process.poll() is None:
            process.kill()
            process.wait()
        info.update(status="Failed", error=str(error), exitCode=process.returncode)
        write_json(session / "failure.json", info)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "run"))
    parser.add_argument("output", type=Path)
    parser.add_argument("--backend", choices=("Mono", "Il2Cpp"), default="Mono")
    parser.add_argument("--screenshots", action="store_true", help="Capture and validate native-input Player viewport frames")
    args = parser.parse_args()
    if args.action == "prepare":
        args.output.mkdir(parents=True, exist_ok=False)
        build_players(args.output / "build")
    else:
        run(args.output, args.backend, args.screenshots)
