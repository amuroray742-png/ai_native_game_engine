"""Verification gate tests: failed evidence must never publish a successful matrix."""
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch

import verify_unity_ui as ui
from unity_packages import copy_verification_project, local_packages


class UiVerificationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def report(self, result="Passed", cases=None, **attributes):
        cases = cases if cases is not None else '<test-case fullname="AiNative.UnityCore.UI.Tests.Sample" result="Passed" />'
        attrs = dict(total="1", passed="1", failed="0", skipped="0", result=result)
        attrs.update(attributes)
        path = self.root / "tests.xml"
        path.write_text('<test-run ' + ' '.join(f'{k}="{v}"' for k, v in attrs.items()) + '>' + cases + '</test-run>', encoding="utf-8")
        return path

    def test_passed_report_is_counted_from_cases(self):
        self.assertEqual(1, ui.read_test_report(self.report(), ui.UI_FILTER)["passed"])

    def test_failed_empty_skipped_and_inconsistent_reports_rejected(self):
        for overrides in (dict(result="Failed"), dict(cases="", total="0", passed="0"),
                          dict(skipped="1"), dict(total="2"),
                          dict(cases='<test-case result="Skipped" />'),
                          dict(cases='<test-case fullname="Wrong.Tests.Test" result="Passed" />')):
            with self.subTest(overrides=overrides), self.assertRaises((ValueError, RuntimeError)):
                ui.read_test_report(self.report(**overrides), ui.UI_FILTER)

    def test_missing_and_malformed_reports_rejected(self):
        path = self.root / "missing.xml"
        with self.assertRaises(FileNotFoundError):
            ui.read_test_report(path, ui.UI_FILTER)
        path.write_text("<test-run", encoding="utf-8")
        with self.assertRaises(ET.ParseError):
            ui.read_test_report(path, ui.UI_FILTER)

    def test_incomplete_suite_is_rejected(self):
        with self.assertRaises(RuntimeError):
            ui.read_test_report(self.report(), ui.UI_FILTER, minimum=25)

    def test_actual_editor_version_must_match_project(self):
        log = self.root / "editor.log"
        log.write_text("Initialize engine version: 6000.3.23f1 (hash)\n", encoding="utf-8")
        self.assertEqual("6000.3.23f1", ui.read_unity_version(log, "6000.3.23f1"))
        with self.assertRaises(RuntimeError):
            ui.read_unity_version(log, "6000.3.24f1")
        log.write_text("No initialization evidence", encoding="utf-8")
        with self.assertRaises(RuntimeError):
            ui.read_unity_version(log, "6000.3.23f1")

    def test_full_log_including_shutdown_is_checked(self):
        path = self.root / "editor.log"
        for message in ("JobTempAlloc has allocations", "TLS Allocator ALLOC_TEMP", "error CS1002: ; expected",
                        "Scripts have compiler errors", "Assertion failed", "Unhandled Exception: broken",
                        "NullReferenceException: broken", "Error: failed shutdown", "UnityEngine.Debug:LogError (object)"):
            path.write_text("Tests passed\n" + message, encoding="utf-8")
            with self.subTest(message=message), self.assertRaises(RuntimeError):
                ui.check_log(path)
        path.write_text("LicensingClient validation\nTests passed\n", encoding="utf-8")
        ui.check_log(path)

    def test_fault_injection_allowance_is_exact_and_suite_scoped(self):
        path = self.root / "editor.log"
        expected = ui.EXPECTED_EXCEPTIONS["ui-play"]
        path.write_text("\n".join(expected), encoding="utf-8")
        ui.check_log(path, expected)
        with self.assertRaises(RuntimeError):
            ui.check_log(path)
        for suffix in ("\nNullReferenceException: unexpected", "\n" + expected[0]):
            path.write_text("\n".join(expected) + suffix, encoding="utf-8")
            with self.assertRaises(RuntimeError):
                ui.check_log(path, expected)

    def test_existing_output_is_rejected_before_cli_or_copy(self):
        with patch.object(ui, "verification_workspace") as workspace:
            with self.assertRaises(FileExistsError):
                ui.verify(self.root)
            workspace.assert_not_called()

    def test_three_packages_are_isolated_and_relative_references_preserved(self):
        project = self.root / "source/examples/unity/UIDemo"
        for folder in ("Assets", "Packages", "ProjectSettings"):
            (project / folder).mkdir(parents=True)
        deps = {}
        for name in ui.PACKAGES:
            package = self.root / "source/packages" / name
            package.mkdir(parents=True)
            (package / "package.json").write_text(json.dumps({"name": name}), encoding="utf-8")
            (package / "Code.cs").write_text("source", encoding="utf-8")
            deps[name] = "file:../../../../packages/" + name
        manifest = project / "Packages/manifest.json"
        manifest.write_text(json.dumps({"dependencies": deps}), encoding="utf-8")
        target = copy_verification_project(project, self.root / "isolated")
        self.assertEqual(set(ui.PACKAGES), set(local_packages(target)))
        self.assertEqual(manifest.read_bytes(), (target / "Packages/manifest.json").read_bytes())
        for source in local_packages(project).values():
            (source / "Code.cs").write_text("changed", encoding="utf-8")
        for copy in local_packages(target).values():
            self.assertEqual("source", (copy / "Code.cs").read_text(encoding="utf-8"))
        deps[ui.PACKAGES[0]] = "file:C:/live-source"
        manifest.write_text(json.dumps({"dependencies": deps}), encoding="utf-8")
        with self.assertRaises(ValueError):
            local_packages(project)

    def test_command_failure_and_timeout_preserve_evidence(self):
        import subprocess
        for response in (subprocess.CompletedProcess([], 6, b'failed', b'compile'),
                         subprocess.TimeoutExpired([], 1, output=b'partial', stderr=b'timed out')):
            label = 'failure' if isinstance(response, subprocess.CompletedProcess) else 'timeout'
            with patch.object(ui.subprocess, "run") as run:
                if isinstance(response, Exception):
                    run.side_effect = response
                else:
                    run.return_value = response
                with self.assertRaises(RuntimeError):
                    ui.run_command(self.root, label, ["unity", "test"], 1)
            self.assertTrue((self.root / (label + ".stdout.json")).exists())
            self.assertTrue((self.root / (label + ".stderr.txt")).exists())
        self.assertFalse((self.root / "summary.json").exists())


if __name__ == "__main__":
    unittest.main()
