import json
from pathlib import Path
import tempfile
import unittest
from PIL import Image

from verify_unity_ui_players import CHECKS, validate_report, verify


class PlayerEvidenceTests(unittest.TestCase):
    def test_report_requires_backend_checks_frames_and_screenshots(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.json"
            report = dict(success=True, backend="Il2Cpp", unity="test", graphics="Direct3D11",
                          checks=CHECKS, frames=100, width=1280, height=720, error="",
                          referenceResolution=dict(x=2000, y=1125), matchWidthOrHeight=0.25,
                          modalMaskColor=dict(r=0.1, g=0.2, b=0.3, a=0.65),
                          windows=0, uiPending=0, failedCleanup=0, bundles=0, resources=0,
                          assetPending=0, owners=0, assetErrors=0)
            for name in ("home", "modals"):
                image = Image.new("RGB", (1280, 720), "black")
                image.putpixel((10, 10), (255, 255, 255))
                image.save(path.with_suffix(f".{name}.png"))
            path.write_text(json.dumps(report))
            self.assertEqual(validate_report(path, "Il2Cpp", "test"), report)
            for key, value in (("success", False), ("backend", "Mono"), ("checks", CHECKS[:-1]),
                               ("frames", 0), ("graphics", "Null"), ("unity", "other"), ("error", "failure"), ("width", 1),
                               ("referenceResolution", {"x": 1920, "y": 1080}), ("matchWidthOrHeight", 0.5),
                               ("modalMaskColor", {"r": 0, "g": 0, "b": 0, "a": 0.5}),
                               ("owners", 1), ("assetErrors", 1),
                               ("checks", [check for check in CHECKS if check != "invalid-cached-canvas-rollback"])):
                path.write_text(json.dumps({**report, key: value}))
                with self.assertRaises(RuntimeError, msg=key):
                    validate_report(path, "Il2Cpp", "test")
            path.write_text(json.dumps(report))
            screenshot = path.with_suffix(".home.png")
            valid = screenshot.read_bytes()
            for invalid in (b"empty", valid[:24] + b"\0" * 4096, valid[:-20], valid[:50] + b"BROKEN" + valid[56:]):
                screenshot.write_bytes(invalid)
                with self.assertRaises(RuntimeError):
                    validate_report(path, "Il2Cpp", "test")
            Image.new("RGB", (1280, 720)).save(screenshot)
            with self.assertRaises(RuntimeError):
                validate_report(path, "Il2Cpp", "test")

    def test_existing_evidence_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(FileExistsError):
                verify(Path(directory))


if __name__ == "__main__":
    unittest.main()
