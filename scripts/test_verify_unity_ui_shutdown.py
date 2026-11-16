import copy
import json
from pathlib import Path
import tempfile
import unittest
from PIL import Image

from verify_unity_ui_shutdown import validate


class ShutdownEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / 'shutdown.jsonl'
        image = Image.new('RGB', (1280, 720), 'white')
        image.putpixel((10, 10), (10, 20, 30))
        image.save(self.path.parent / 'pending-frame-4.png')

    def records(self, scenario):
        base = dict(kind='state', backend='Mono', scenario=scenario, unity='6000.3.23f1',
                    frame=1, running=True, busy=False, modelDisposed=False, pages=1, windows=2,
                    uiPending=0, failedCleanup=0, bundles=3, resources=3, assetPending=0, owners=10,
                    targetState='Absent', alpha=-1, deliveryHeld=False, deliveryReleased=False, assetErrors=[])
        ready = dict(base, kind='ready')
        target = dict(base, busy=True, uiPending=1, frame=3, targetState='Opening' if scenario == 'loading' else 'Exiting',
                      pages=1 if scenario == 'loading' else 2, alpha=-1 if scenario == 'loading' else .9,
                      deliveryHeld=scenario == 'loading', assetPending=1 if scenario == 'loading' else 0)
        shot = dict(target, kind='screenshot', frame=4, screenshot='pending-frame-4.png')
        request = dict(target, kind='wants-to-quit', frame=5, alpha=-1 if scenario == 'loading' else .8)
        quit_sample = dict(request, kind='application-quit')
        closed = dict(quit_sample, kind='destroy', frame=6, running=False, modelDisposed=True, deliveryHeld=False,
                      deliveryReleased=scenario == 'loading')
        for key in ('windows', 'pages', 'uiPending', 'failedCleanup', 'bundles', 'resources', 'assetPending', 'owners'):
            closed[key] = 0
        middle = [dict(base, kind='gallery-ready', frame=2, pages=2)] if scenario == 'exiting' else []
        late = [dict(quit_sample, kind='retired', running=False), dict(closed, kind='late-delivery', frame=5)] if scenario == 'loading' else []
        return [ready, *middle, target, shot, request, quit_sample, *late, closed]

    def verify(self, rows, scenario):
        self.path.write_text(''.join(json.dumps(r) + '\n' for r in rows), encoding='utf-8')
        return validate(self.path, 'Mono', scenario, '6000.3.23f1')

    def test_both_valid_pending_phases(self):
        for scenario in ('loading', 'exiting'):
            self.assertEqual('Passed', self.verify(self.records(scenario), scenario)['status'])

    def test_rejects_close_after_phase_or_false_loading(self):
        for scenario, changes in [('loading', {'deliveryHeld': False}), ('loading', {'assetPending': 0}),
                                  ('loading', {'pages': 2}), ('exiting', {'targetState': 'Visible'}),
                                  ('exiting', {'alpha': 0}), ('exiting', {'busy': False}), ('exiting', {'uiPending': 0})]:
            rows = self.records(scenario)
            next(r for r in rows if r['kind'] == 'wants-to-quit').update(changes)
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                self.verify(rows, scenario)

    def test_rejects_bad_identity_lifecycle_errors_and_ownership_leaks(self):
        good = self.records('loading')
        variants = []
        for key, value in [('owners', 1), ('resources', 1), ('assetPending', 1), ('running', True), ('modelDisposed', False)]:
            rows = copy.deepcopy(good); rows[-1][key] = value; variants.append(rows)
        rows = copy.deepcopy(good); rows[0]['backend'] = 'Il2Cpp'; variants.append(rows)
        rows = copy.deepcopy(good); rows.insert(-1, dict(rows[0], kind='error')); variants.append(rows)
        rows = copy.deepcopy(good); rows[-2]['running'] = True; variants.append(rows)
        rows = copy.deepcopy(good); next(r for r in rows if r['kind'] == 'retired')['running'] = True; variants.append(rows)
        rows = copy.deepcopy(good); rows[-1]['assetErrors'] = ['AssetTypeMismatch']; variants.append(rows)
        rows = copy.deepcopy(good); rows[4], rows[5] = rows[5], rows[4]; variants.append(rows)
        variants.append(good[:-1])
        variants.append(good + [good[-1]])
        for i, rows in enumerate(variants):
            with self.subTest(case=i), self.assertRaises(RuntimeError):
                self.verify(rows, 'loading')

    def test_rejects_stationary_exit_and_bad_screenshots(self):
        rows = self.records('exiting')
        next(r for r in rows if r['kind'] == 'wants-to-quit')['alpha'] = .95
        with self.assertRaises(RuntimeError): self.verify(rows, 'exiting')
        for change in ({'targetState': 'Visible'}, {'screenshot': '../pending-frame-4.png'}):
            rows = self.records('exiting'); next(r for r in rows if r['kind'] == 'screenshot').update(change)
            with self.assertRaises(RuntimeError): self.verify(rows, 'exiting')
        (self.path.parent / 'pending-frame-4.png').write_bytes(b'bad PNG')
        with self.assertRaises(OSError): self.verify(self.records('loading'), 'loading')


if __name__ == '__main__':
    unittest.main()
