"""Isolated ShaderProbe variant stripping demo (Windows Mono, D3D11)."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
from unity_packages import copy_verification_project, verification_workspace
from verify_unity_assets import check_native_leaks

ROOT = Path(__file__).resolve().parents[1]


def verify(output, backend, current, expectation):
    output.mkdir(parents=True, exist_ok=False)
    workspace = verification_workspace(output)
    project = copy_verification_project(ROOT / 'examples/unity/AssetsDemo', workspace)
    (project / '.shader-variant-isolated').write_text('Disposable shader variant demonstration project.\n')
    cli = shutil.which('unity')
    if not cli:
        raise RuntimeError('Unity CLI is required')
    hashes = {}
    for base in (project / 'Assets', project / 'Packages', project / 'ProjectSettings', workspace / 'packages'):
        for path in sorted(base.rglob('*')):
            if path.is_file() and 'StreamingAssets' not in path.parts:
                hashes[path.relative_to(workspace).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    (output / 'sources.json').write_text(json.dumps(hashes, indent=2), encoding='utf-8')

    def method(label, entry, extra=()):
        print(label, flush=True)
        command = [cli, 'run', str(project), '--timeout', '1200', '--no-tail', '--format', 'json', '--',
                   '-executeMethod', entry, '-logFile', str(output / (label + '.log')), *map(str, extra)]
        result = subprocess.run(command, cwd=ROOT, capture_output=True, timeout=1300)
        (output / (label + '.stdout.json')).write_bytes(result.stdout)
        (output / (label + '.stderr.txt')).write_bytes(result.stderr)
        if result.returncode:
            raise RuntimeError(f'{label} failed ({result.returncode}); see {output}')
        check_native_leaks(output / (label + '.log'))

    method('configure', 'AssetsDemoSetup.ConfigureBuildSettings')
    method('forward', 'ExampleUrpSetup.Forward')
    method('create', 'ShaderVariantDemoSetup.Create')
    summary = {}
    for target in ('builtin', 'sbp') if backend == 'both' else (backend,):
        for state in ('current',) if current else ('retained', 'stripped', 'restored'):
            label = target + '-' + state
            expected = expectation if current else 'missing' if state == 'stripped' else 'present'
            entry = 'ShaderVariantDemoSetup.BuildCurrentForBackend' if current else 'ShaderVariantDemoSetup.PrepareVerificationCase'
            method(label + '-content', entry, ['-variantBackend', target, '-variantCase', state])
            content = Path((project / 'variant-last-build.txt').read_text().strip())
            shutil.copytree(content, output / (label + '-content'))
            shutil.copytree(project / 'Assets/ShaderVariantDemo', output / (label + '-config'))
            report = json.loads((content / 'shader-build-report.json').read_bytes())
            passes = [p for p in report['Passes'] if p['ShaderPath'] == 'Assets/Rendering/KeywordProbe.shader' and p['PassName'] == 'Forward']
            if not passes:
                raise RuntimeError('No observed Keyword Probe Forward pass')
            green_rules = [int(index) for index in (project / 'variant-green-rule-indices.txt').read_text().split(',') if index]
            if expected == 'missing' and not any(p['RuleHits'][index] > 0 for p in passes for index in green_rules):
                raise RuntimeError('Green exclusion rule was not observed removing variants')
            (output / (label + '-green-rules.json')).write_text(json.dumps(green_rules))
            executable = output / label / 'ShaderVariantDemo.exe'
            method(label + '-build', 'ShaderVariantDemoSetup.BuildPlayer', ['-variantPlayerOutput', executable])
            result_path = output / (label + '.json')
            log_path = output / (label + '-player.log')
            print(label + '-player', flush=True)
            result = subprocess.run([str(executable), '-batchmode', '-force-d3d11', '-variantExpect', expected,
                                     '-variantResult', str(result_path), '-logFile', str(log_path)],
                                    cwd=executable.parent, timeout=120,
                                    creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            check_native_leaks(log_path)
            data = json.loads(result_path.read_bytes()) if result_path.exists() else {}
            if result.returncode or not data.get('success'):
                raise RuntimeError(f'{label} Player failed: {data}')
            text = log_path.read_text(encoding='utf-8', errors='replace')
            missing = [line for line in text.splitlines() if 'variant' in line.lower() and 'not found' in line]
            if expected == 'missing':
                if not missing or not all(re.fullmatch(r'Shader AI Native/Keyword Probe, subshader 0, pass 0, stage (vertex|pixel): variant PROBE_GREEN not found\.', line) for line in missing):
                    raise RuntimeError('Missing-variant logs do not match the intended shader/pass/keyword')
            elif missing:
                raise RuntimeError('Unexpected missing variant')
            summary[label] = data
            (output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    # The interactive fixture expects a retained/restored current build.
    if not current or expectation == 'present':
        for reload in ('On', 'Off'):
            method('reload-' + reload, 'AssetsDemoSetup.Reload' + reload)
            label = 'editor-' + reload
            print(label, flush=True)
            xml = output / (label + '.xml')
            log = output / (label + '.log')
            result = subprocess.run([cli, 'test', str(project), '--mode', 'EditMode', '--filter', 'ShaderVariantDemoTests',
                                     '--timeout', '900', '--output', str(xml), '--format', 'json', '--', '-logFile', str(log)],
                                    cwd=ROOT, capture_output=True, timeout=1000)
            (output / (label + '.stdout.json')).write_bytes(result.stdout)
            (output / (label + '.stderr.txt')).write_bytes(result.stderr)
            if result.returncode: raise RuntimeError(label + ' failed')
            check_native_leaks(log)
            report = ET.parse(xml).getroot()
            if report.get('result') != 'Passed' or int(report.get('passed', 0)) != 3 or int(report.get('skipped', 0)):
                raise RuntimeError('Editor fixture did not pass all three tests')
            summary[label] = dict(report.attrib)
            shutil.copytree(project / 'TestResults/VariantEditor', output / label)
        (output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print('PASS: ' + ', '.join(summary), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    parser.add_argument('--backend', choices=['builtin', 'sbp', 'both'], default='both')
    parser.add_argument('--current', action='store_true', help='Build your current manual demo configuration, without changing its variants')
    parser.add_argument('--expect', choices=['present', 'missing'], default='present')
    args = parser.parse_args()
    verify(args.output.resolve(), args.backend, args.current, args.expect)
