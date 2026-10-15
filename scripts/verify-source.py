#!/usr/bin/env python3
"""Structural checks only: not a C# compiler, XAML compiler, or runtime test.

Python 3.10+ standard library. Optional --json-output writes a report at an
explicit path. Does not read the user's Proxyz data or alter Windows settings.
"""
from __future__ import annotations
import argparse
import json
import re
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
XNS = '{http://schemas.microsoft.com/winfx/2006/xaml}'
IGNORED = {'bin', 'obj', '.git', '.vs', '__pycache__', 'artifacts', 'TestResults', '.dotnet-home'}


def files() -> list[Path]:
    return sorted(p for p in ROOT.rglob('*') if p.is_file() and not any(x in IGNORED for x in p.relative_to(ROOT).parts))


def check_delimiters(text: str) -> None:
    """Check paired delimiters while skipping C# comments/literals.

    Handles the ordinary/verbatim/interpolated literals used in this tree. This
    intentionally does not parse C# declarations, types, grammar, or raw strings.
    """
    n = len(text)
    pairs = {'(': ')', '[': ']', '{': '}'}

    def error(message: str, i: int) -> None:
        raise ValueError(f'{message} at line {text.count(chr(10), 0, i) + 1}')

    def literal(i: int, quote: str, verbatim: bool, interpolated: bool) -> int:
        while i < n:
            c = text[i]
            if c == quote:
                if verbatim and i + 1 < n and text[i + 1] == quote:
                    i += 2
                    continue
                return i + 1
            if c == '\\' and not verbatim:
                i += 2
                continue
            if interpolated and c == '{':
                if text.startswith('{{', i):
                    i += 2
                else:
                    i = code(i + 1, '}')
                continue
            if interpolated and text.startswith('}}', i):
                i += 2
                continue
            i += 1
        error('Unclosed string/character', n - 1)
        return n

    def code(i: int, closing: str | None = None) -> int:
        while i < n:
            if text.startswith('//', i):
                end = text.find('\n', i + 2)
                i = n if end < 0 else end + 1
                continue
            if text.startswith('/*', i):
                end = text.find('*/', i + 2)
                if end < 0:
                    error('Unclosed comment', i)
                i = end + 2
                continue
            if text.startswith('"""', i):
                # Non-interpolated raw literals used for CLI help. This is only
                # delimiter skipping; the compiler validates actual C# syntax.
                delimiter_size = 3
                while i + delimiter_size < n and text[i + delimiter_size] == '"':
                    delimiter_size += 1
                end = text.find('"' * delimiter_size, i + delimiter_size)
                if end < 0:
                    error('Unclosed raw string', i)
                i = end + delimiter_size
                continue
            prefixed = False
            for prefix, verbatim, interpolated in [('$@"', True, True), ('@$"', True, True), ('@"', True, False), ('$"', False, True)]:
                if text.startswith(prefix, i):
                    i = literal(i + len(prefix), '"', verbatim, interpolated)
                    prefixed = True
                    break
            if prefixed:
                continue
            c = text[i]
            if c in {'"', "'"}:
                i = literal(i + 1, c, False, False)
            elif c in pairs:
                i = code(i + 1, pairs[c])
            elif c in ')]}':
                if c != closing:
                    error('Mismatched closing delimiter ' + c, i)
                return i + 1
            else:
                i += 1
        if closing is not None:
            error('Missing closing delimiter ' + closing, n - 1)
        return i

    code(0)


def run() -> dict:
    checks = []
    all_files = files()
    def record(name: str, action) -> None:
        try:
            detail = action()
            checks.append({'name': name, 'passed': True, 'detail': str(detail or 'OK')})
        except Exception as exc:
            checks.append({'name': name, 'passed': False, 'detail': str(exc)})

    def xml_check():
        candidates = [p for p in all_files if p.suffix in {'.xaml', '.csproj', '.props', '.slnx', '.manifest'} or p.name == 'NuGet.Config']
        for p in candidates:
            ET.parse(p)
        return f'{len(candidates)} XML documents parsed; no XAML compilation'
    record('xml-well-formedness', xml_check)

    def json_check():
        candidates = [p for p in all_files if p.suffix == '.json']
        for p in candidates:
            json.loads(p.read_text(encoding='utf-8-sig'))
        return f'{len(candidates)} JSON files parsed; no C# deserialization'
    record('json-syntax', json_check)

    def project_refs():
        projects = list(ROOT.rglob('*.csproj'))
        count = 0
        for p in projects:
            root = ET.parse(p).getroot()
            for ref in root.iter('ProjectReference'):
                target = p.parent / ref.attrib['Include']
                if not target.is_file():
                    raise ValueError(f'Missing project: {target}')
                count += 1
            for node in root.iter('SupportedOSPlatformVersion'):
                tfm = root.findtext('.//TargetFramework') or ''
                if tfm.endswith('-windows') and float(node.text or '0') > 7:
                    raise ValueError(f'{p.name}: base -windows TFM has API version 7.0; check NETSDK1135')
        solution = ET.parse(ROOT / 'Proxyz.slnx').getroot()
        for project in solution.iter('Project'):
            if not (ROOT / project.attrib['Path']).is_file():
                raise ValueError('Missing solution project ' + project.attrib['Path'])
        return f'{len(projects)} projects; {count} existing project references'
    record('project-and-solution-paths', project_refs)

    def xaml_check():
        count = 0
        for p in ROOT.rglob('*.xaml'):
            root = ET.parse(p).getroot()
            class_name = root.attrib.get(XNS + 'Class')
            named = [e.attrib[XNS + 'Name'] for e in root.iter() if XNS + 'Name' in e.attrib]
            if len(set(named)) != len(named):
                raise ValueError(f'Duplicate x:Name in {p.name}')
            if class_name:
                code_path = p.with_suffix(p.suffix + '.cs')
                source = code_path.read_text(encoding='utf-8')
                namespace, name = class_name.rsplit('.', 1)
                if f'namespace {namespace};' not in source or not re.search(r'\bpartial\s+class\s+' + re.escape(name) + r'\b', source):
                    raise ValueError(f'x:Class mismatch: {p.name}')
                for element in root.iter():
                    for event in ('Click', 'PreviewKeyDown', 'Loaded', 'Closing', 'Closed'):
                        handler = element.attrib.get(event)
                        if handler and not re.search(r'\b' + re.escape(handler) + r'\s*\(', source):
                            raise ValueError(f'Missing {handler}: {p.name}')
                count += 1
        return f'{count} code-behind pairs; names/explicit handlers only, not bindings or types'
    record('xaml-code-behind-links', xaml_check)

    def csharp_check():
        candidates = [p for p in all_files if p.suffix == '.cs']
        for p in candidates:
            try:
                check_delimiters(p.read_text(encoding='utf-8'))
            except ValueError as exc:
                raise ValueError(f'{p.relative_to(ROOT)}: {exc}') from exc
        return f'{len(candidates)} C# files; delimiter balance only, not syntax/type validation'
    record('csharp-delimiters-only', csharp_check)

    def links_check():
        count = 0
        for p in all_files:
            if p.suffix != '.md':
                continue
            for link in re.findall(r'\]\(([^)]+)\)', p.read_text(encoding='utf-8')):
                if re.match(r'^[A-Za-z][A-Za-z0-9+.-]*:', link) or link.startswith('#'):
                    continue
                destination = link.split('#', 1)[0]
                if destination and not (p.parent / destination).exists():
                    raise ValueError(f'{p.name}: missing link {destination}')
                count += 1
        return f'{count} local documentation links exist (anchors/external links not checked)'
    record('local-documentation-links', links_check)

    def artifact_check():
        prohibited = {'.exe', '.dll', '.pfx', '.p12', '.key', '.ttf', '.otf', '.woff', '.woff2', '.zip'}
        for p in all_files:
            if p.suffix.lower() in prohibited or p.name == '.env':
                raise ValueError('Unexpected binary/key/font/environment file: ' + str(p.relative_to(ROOT)))
            if p.suffix == '.cs' and 'throw new NotImplementedException' in p.read_text(encoding='utf-8'):
                raise ValueError('Unimplemented stub: ' + str(p.relative_to(ROOT)))
        return 'No packaged executables, keys, fonts, .env, or NotImplementedException stubs'
    record('source-artifact-hygiene', artifact_check)

    return {
        'scope': 'Structural source checks only; no .NET build, C# execution, native calls, or UI validation.',
        'passed': all(c['passed'] for c in checks),
        'checks': checks,
        'counts': {
            'files_seen': len(all_files),
            'csharp_files': sum(p.suffix == '.cs' for p in all_files),
            'csharp_lines': sum(len(p.read_text(encoding='utf-8').splitlines()) for p in all_files if p.suffix == '.cs'),
            'xaml_files': sum(p.suffix == '.xaml' for p in all_files)
        }
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--json-output', type=Path)
    args = parser.parse_args()
    report = run()
    for c in report['checks']:
        print(('PASS' if c['passed'] else 'FAIL') + ' ' + c['name'] + ': ' + c['detail'])
    print(report['scope'])
    if args.json_output:
        args.json_output.parent.mkdir(parents=True, exist_ok=True)
        args.json_output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return 0 if report['passed'] else 1

if __name__ == '__main__':
    sys.exit(main())
