"""Isolated UIDemo SBP build and dual Domain Reload Editor acceptance.

Every invocation uses a new evidence directory and a private source copy.
Screen captures are a separate visible-Editor check; see the UIDemo README.
"""
import argparse
import json
from pathlib import Path
import shutil

from unity_packages import copy_verification_project, local_packages, verification_workspace
from unity_verification import (
    ROOT, check_log, digest, fingerprint_project, read_test_report, read_unity_version, run_command, write_json,
)

PROJECT = ROOT / "examples/unity/UIDemo"
PACKAGES = ("com.ainative.unitycore", "com.ainative.unitycore.assets", "com.ainative.unitycore.ui")
UI_FILTER = "AiNative.UnityCore.UI.Tests"
DEMO_FILTER = "AiNative.UIDemo.Tests.UIDemoTests"


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


def fingerprint(root, project):
    return fingerprint_project(root, project, PACKAGES, exclude_streaming_assets=True)


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
                   ("verify_unity_ui.py", "test_verify_unity_ui.py", "unity_packages.py", "unity_verification.py")})
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
