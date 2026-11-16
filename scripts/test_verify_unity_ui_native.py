import copy
import json
from pathlib import Path
import tempfile
import unittest

from PIL import Image
from verify_unity_ui_native import validate_journal, validate_screenshots


class NativeEvidenceTests(unittest.TestCase):
    def test_native_screenshots_require_decodable_frames_and_input_order(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            journal = folder / "native.jsonl"
            records = []
            for index, state in enumerate(((1, 1, 0, "Bootstrap"), (1, 2, 0, "Bootstrap"),
                                           (1, 3, 2, "Bootstrap"), (2, 2, 0, "Other"))):
                frame = index * 10 + 10
                if index: records.append(dict(kind="input", frame=frame - 1))
                sample = dict(kind="screenshot", frame=frame, screenshot=f"native-frame-{frame}.png",
                              width=1280, height=720, running=True, busy=False, uiPending=0)
                sample.update(zip(("session", "pages", "modals", "scene"), state))
                records.append(sample)
                image = Image.new("RGB", (1280, 720), "black")
                image.putpixel((10, 10), (255, 255, 255))
                image.save(folder / sample["screenshot"])
            def check(items):
                journal.write_text("\n".join(json.dumps(item) for item in items))
                return validate_screenshots(journal)
            self.assertEqual(check(records)["checkpoints"], 4)
            for items in (records[:-1], [item for item in records if item['kind'] != 'input']):
                with self.assertRaises(RuntimeError): check(items)
            for key, value in (("screenshot", "../other.png"), ("frame", 9), ("busy", True), ("width", 0)):
                bad = copy.deepcopy(records); bad[-1][key] = value
                with self.assertRaises(RuntimeError): check(bad)
            (folder / records[-1]["screenshot"]).write_bytes(b"not a PNG")
            with self.assertRaises(OSError): check(records)

    def records(self):
        template = dict(kind="state", backend="Mono", unity="test", busy=False, uiPending=0, running=True,
                        bundles=3, resources=3, inputModule="StandaloneInputModule", focused=True, selected="Next",
                        windows=2, failedCleanup=0, assetPending=0, cache=0, modelDisposed=False,
                        mouseDown=False, submitDown=False, rightDown=False)
        steps = [(1, 1, 0, "Bootstrap"), (1, 2, 0, "Bootstrap"), (1, 3, 0, "Bootstrap"),
                 (1, 3, 1, "Bootstrap"), (1, 3, 2, "Bootstrap"), (1, 3, 1, "Bootstrap"),
                 (1, 3, 0, "Bootstrap"), (2, 1, 0, "Bootstrap"), (2, 1, 0, "Other"), (2, 2, 0, "Other")]
        records = []
        for frame, (session, pages, modals, scene) in enumerate(steps):
            state = dict(template, frame=frame + 10, session=session, pages=pages, modals=modals, scene=scene,
                         selected="Back" if frame == 5 else "Next")
            records.append(dict(state, kind="input", mouseDown=frame < 5, submitDown=5 <= frame < 8, rightDown=frame == 8))
            records.append(state)
        records.append(dict(records[-1], kind="application-quit", frame=30))
        records.append(dict(records[-1], kind="destroy", running=False, modelDisposed=True,
                            windows=0, pages=0, modals=0, cache=0, bundles=0, resources=0))
        return records

    def test_accepts_complete_sequence_but_rejects_false_success(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "native.jsonl"
            def validate(records):
                path.write_text("\n".join(json.dumps(item) for item in records), encoding="utf-8")
                return validate_journal(path, "Mono", "test")
            records = self.records()
            self.assertEqual(validate(records)["checkpoints"], 10)
            for key, value in (("resources", 1), ("uiPending", 1), ("modelDisposed", False), ("running", True)):
                bad = copy.deepcopy(records)
                bad[-1][key] = value
                with self.assertRaises(RuntimeError, msg=key): validate(bad)
            for bad in ([], records[:-1], records[2:], records + [dict(records[0], kind="error")],
                        [dict(item, focused=False) for item in records],
                        [dict(item, backend="Il2Cpp") for item in records],
                        [dict(item, selected="Next") for item in records], records[:-1] + [records[-2], records[-1]]):
                with self.assertRaises(RuntimeError): validate(bad)
            for key, value in (("windows", 0), ("bundles", 0), ("running", False), ("modelDisposed", True)):
                bad = copy.deepcopy(records)
                bad[-3][key] = value
                with self.assertRaises(RuntimeError, msg="pre-quit " + key): validate(bad)


if __name__ == "__main__":
    unittest.main()
