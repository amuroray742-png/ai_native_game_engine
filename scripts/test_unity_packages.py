"""Verify that isolated Editor projects resolve their own package source copies."""
import json
from pathlib import Path
import tempfile
import unittest

from unity_packages import copy_verification_project, local_packages


class LocalPackageTests(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.TemporaryDirectory(prefix="ainative-package-test-")
        self.addCleanup(self.work.cleanup)
        self.root = Path(self.work.name)
        self.project = self.root / "source/examples/unity/Game"
        for directory in ("Assets", "Packages", "ProjectSettings"):
            (self.project / directory).mkdir(parents=True)
        self.name = "com.ainative.unity"
        self.package = self.root / "source/packages" / self.name
        self.package.mkdir(parents=True)
        (self.package / "package.json").write_text(json.dumps({"name": self.name}), encoding="utf-8")
        (self.package / "Example.cs").write_text("original", encoding="utf-8")
        manifest = {"dependencies": {self.name: "file:../../../../packages/" + self.name}}
        (self.project / "Packages/manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

    def test_relocated_project_resolves_isolated_sources(self):
        copy = copy_verification_project(self.project, self.root / "verification")
        package = local_packages(copy)[self.name]
        self.assertEqual(package, self.root / "verification/packages" / self.name)
        (self.package / "Example.cs").write_text("changed", encoding="utf-8")
        self.assertEqual((package / "Example.cs").read_text(encoding="utf-8"), "original")
        self.assertEqual((copy / "Packages/manifest.json").read_bytes(),
                         (self.project / "Packages/manifest.json").read_bytes())

    def test_embedded_override_is_rejected_without_deleting_edits(self):
        embedded = self.project / "Packages" / self.name
        embedded.mkdir()
        edit = embedded / "Example.cs"
        edit.write_text("local edit", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "shadows"):
            local_packages(self.project)
        self.assertEqual(edit.read_text(encoding="utf-8"), "local edit")


if __name__ == "__main__":
    unittest.main()
