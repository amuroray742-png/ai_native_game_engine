"""Observe external window closure while UIDemo is loading or playing an exit transition.

Prepare uses verify_unity_ui_native.py prepare. This runner never requests a normal
exit. Setup inside the opt-in Player fixture uses host APIs, not native input.
"""
import argparse
import json
from pathlib import Path
import subprocess
from PIL import Image

from verify_unity_ui import ROOT, PROJECT, check_log, digest, fingerprint, read_unity_version, write_json


def pending(sample, scenario):
    common = sample['running'] and sample['busy'] and sample['uiPending'] > 0 and not sample['modelDisposed']
    if scenario == 'loading':
        return (common and sample['targetState'] == 'Opening' and sample['pages'] == 1 and
                sample['deliveryHeld'] and not sample['deliveryReleased'] and sample['assetPending'] > 0)
    return (common and sample['targetState'] == 'Exiting' and sample['pages'] == 2 and
            0 < sample['alpha'] < 1 and not sample['deliveryHeld'])


def validate(path, backend, scenario, unity):
    records = [json.loads(line) for line in path.read_text(encoding='utf-8').splitlines()]
    if not records or any(r['kind'] == 'error' or r['assetErrors'] for r in records):
        raise RuntimeError('Empty journal or runtime errors')
    if any((r['backend'], r['scenario'], r['unity']) != (backend, scenario, unity) for r in records):
        raise RuntimeError('Runtime identity mismatch')
    if any(a['frame'] > b['frame'] for a, b in zip(records, records[1:])):
        raise RuntimeError('Non-monotonic frame order')
    def one(kind):
        matches = [r for r in records if r['kind'] == kind]
        if len(matches) != 1:
            raise RuntimeError('Expected exactly one ' + kind)
        return matches[0]
    ready, request, quit_sample, closed = [one(k) for k in ('ready', 'wants-to-quit', 'application-quit', 'destroy')]
    if not (records.index(ready) < records.index(request) < records.index(quit_sample) < records.index(closed)):
        raise RuntimeError('Invalid lifecycle ordering')
    if records[-1] != closed or not ready['running'] or ready['pages'] != 1:
        raise RuntimeError('Missing initial live session or final destruction')
    if not pending(request, scenario):
        raise RuntimeError('Window close arrived outside the required pending phase')
    zero = ('windows', 'pages', 'uiPending', 'failedCleanup', 'bundles', 'resources', 'assetPending', 'owners')
    if closed['running'] or not closed['modelDisposed'] or any(closed[k] != 0 for k in zero):
        raise RuntimeError('Shutdown did not release all observed ownership')
    screenshot = one('screenshot')
    if not pending(screenshot, scenario) or not records.index(ready) < records.index(screenshot) < records.index(request):
        raise RuntimeError('Missing pending-phase screenshot before close')
    name = f"pending-frame-{screenshot['frame']}.png"
    if screenshot['screenshot'] != name:
        raise RuntimeError('Screenshot frame/path mismatch')
    image_path = path.parent / name
    with Image.open(image_path) as image:
        if image.format != 'PNG' or image.size != (1280, 720):
            raise RuntimeError('Wrong screenshot format/size')
        image.verify()
    with Image.open(image_path) as image:
        image.load()
        if all(lo == hi for lo, hi in image.convert('RGB').getextrema()):
            raise RuntimeError('Blank screenshot')
    if scenario == 'loading':
        retired = one('retired')
        late = one('late-delivery')
        if not records.index(quit_sample) < records.index(retired) < records.index(late) < records.index(closed):
            raise RuntimeError('Late delivery did not follow retirement')
        if retired['running'] or not retired['deliveryHeld'] or late['running'] or not late['deliveryReleased'] or late['deliveryHeld']:
            raise RuntimeError('Invalid delayed delivery lifecycle')
        if any(r['pages'] > 1 for r in records):
            raise RuntimeError('Loading candidate committed across shutdown')
    else:
        gallery = one('gallery-ready')
        phases = [r for r in records[:records.index(request)] if r['kind'] == 'state' and pending(r, scenario)]
        if not records.index(ready) < records.index(gallery) < records.index(screenshot) or not phases:
            raise RuntimeError('Missing committed Gallery before exit')
        if not gallery['running'] or gallery['pages'] != 2 or gallery['busy']:
            raise RuntimeError('Invalid Gallery setup')
        if phases[0]['alpha'] <= request['alpha']:
            raise RuntimeError('Exit animation did not advance before close')
    return dict(status='Passed', backend=backend, scenario=scenario, closeRequest=request,
                applicationQuit=quit_sample, shutdown=closed, journalSha256=digest(path),
                screenshot=dict(file=name, sha256=digest(image_path), viewportOnly=True),
                setupMethod='Opt-in fixture calls host APIs; external operator closes OS window',
                loadingScope='Real Bundle/asset completed; controlled delayed delivery, released after Core retirement')


def run(output, backend, scenario):
    output = output.resolve()
    build = output / 'build'
    summary = json.loads((build / 'summary.json').read_text(encoding='utf-8'))
    source = json.loads((build / 'sources.json').read_text())
    if summary['status'] != 'Passed' or fingerprint(ROOT, PROJECT) != source:
        raise RuntimeError('Build evidence must pass and match current source')
    folder = Path(summary['workspace']) / 'TestResults/ui-players' / backend
    manifest = json.loads((build / (backend + '-player-files.json')).read_text())
    if any(digest(folder / name) != value for name, value in manifest.items()):
        raise RuntimeError('Player files differ from verified build')
    session = output / (backend + '-' + scenario)
    session.mkdir(exist_ok=False)
    journal, log = session / 'shutdown.jsonl', session / 'player.log'
    command = [str(folder / 'UIDemo.exe'), '-force-d3d11', '-screen-fullscreen', '0', '-screen-width', '1280',
               '-screen-height', '720', '-uiShutdownScenario', scenario, str(journal), '-logFile', str(log)]
    script_hash = digest(Path(__file__))
    process = subprocess.Popen(command, cwd=ROOT)
    info = dict(command=command, pid=process.pid, backend=backend, scenario=scenario, scriptSha256=script_hash,
                waitTimeoutSeconds=3600)
    write_json(session / 'launch.json', info)
    print(json.dumps(info), flush=True)
    try:
        code = process.wait(timeout=info['waitTimeoutSeconds'])
        write_json(session / 'process.json', dict(info, exitCode=code))
        if code != 0:
            raise RuntimeError(f'Player exited {code}')
        check_log(log)
        read_unity_version(log, summary['unity'])
        result = validate(journal, backend, scenario, summary['unity'])
        if fingerprint(ROOT, PROJECT) != source or digest(Path(__file__)) != script_hash:
            raise RuntimeError('Source/runner changed during acceptance')
        write_json(session / 'summary.json', result)
        print(json.dumps(result), flush=True)
    except Exception as error:
        if process.poll() is None:
            process.kill()
            process.wait()
        write_json(session / 'failure.json', dict(info, status='Failed', error=str(error), exitCode=process.returncode))
        raise


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    parser.add_argument('--backend', required=True, choices=('Mono', 'Il2Cpp'))
    parser.add_argument('--scenario', required=True, choices=('loading', 'exiting'))
    args = parser.parse_args()
    run(args.output, args.backend, args.scenario)
