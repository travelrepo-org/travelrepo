#!/usr/bin/env python3
"""Release notes from docs/releases/<version>.yaml, the only place they are written.

  python eng/changelog.py                 writes CHANGELOG.md
  python eng/changelog.py --check         fails when a release file is invalid, the current version has no
                                          release file, or CHANGELOG.md is out of date
  python eng/changelog.py --release-body v0.4.0 [--existing body.md]
                                          prints the GitHub release text: the existing text, then the changes
"""
from pathlib import Path
import argparse
import re
import sys

import yaml

ROOT = Path(__file__).resolve().parents[1]
RELEASES = ROOT / 'docs' / 'releases'
CHANGELOG = ROOT / 'CHANGELOG.md'
MARKER = '<!-- travelrepo-release-notes: generated from docs/releases, edit above this line -->'
SECTIONS = [('changes', 'Changes'), ('fixes', 'Fixes')]


def current():
    match = re.search(r'<Version>([^<]+)</Version>', (ROOT / 'Directory.Build.props').read_text(encoding='utf-8'))
    return match.group(1).strip()


def key(version):
    core, _, pre = version.partition('-')
    return tuple(int(p) for p in core.split('.')) + ((1,) if not pre else (0, pre))


def load():
    releases = []
    for path in RELEASES.glob('*.yaml'):
        data = yaml.safe_load(path.read_text(encoding='utf-8')) or {}
        data['_path'] = path; releases.append(data)
    return sorted(releases, key=lambda r: key(str(r.get('version', '0.0.0'))), reverse=True)


def problems(releases, version):
    found = [] if any(str(r.get('version')) == version for r in releases) else [f'docs/releases/{version}.yaml is missing: every release needs notes.']
    for r in releases:
        name = r['_path'].name
        if name != str(r.get('version')) + '.yaml':
            found.append(f'{name}: version "{r.get("version")}" does not match the file name.')
        if not any(r.get(s) for s, _ in SECTIONS):
            found.append(f'{name}: has no changes or fixes.')
        for section, _ in SECTIONS:
            for i, entry in enumerate(r.get(section) or []):
                if not isinstance(entry, dict) or not str(entry.get('en', '')).strip():
                    found.append(f'{name}: {section}[{i}] needs an English text (en).')
    return found


def section_lines(release, level):
    lines = []
    for section, heading in SECTIONS:
        entries = release.get(section) or []
        if entries:
            lines += [f'{level} {heading}', ''] + ['- ' + ' '.join(str(e['en']).split()) for e in entries] + ['']
    return lines


def render(releases):
    lines = ['# Changelog', '', 'All notable changes to TravelRepo. Generated from `docs/releases` by `python eng/changelog.py`; do not edit by hand.', '']
    for r in releases:
        lines += [f'## {r["version"]}' + (f' – {r["date"]}' if r.get('date') else ''), ''] + section_lines(r, '###')
    return '\n'.join(lines).rstrip() + '\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--release-body', metavar='TAG')
    parser.add_argument('--existing', metavar='FILE')
    parser.add_argument('--github', action='store_true')
    args = parser.parse_args()
    releases = load(); version = current()
    if args.release_body:
        release = next((r for r in releases if str(r.get('version')) == args.release_body.lstrip('v')), None)
        if release is None:
            raise SystemExit(f'No docs/releases/{args.release_body.lstrip("v")}.yaml.')
        own = Path(args.existing).read_text(encoding='utf-8').split(MARKER)[0].rstrip() if args.existing and Path(args.existing).exists() else ''
        sys.stdout.write('\n'.join(([own, ''] if own else []) + [MARKER, '', '## Changes', ''] + section_lines(release, '###')).rstrip() + '\n'); return 0
    errors = problems(releases, version); text = render(releases)
    if args.check:
        if not CHANGELOG.exists() or CHANGELOG.read_text(encoding='utf-8') != text:
            errors.append('CHANGELOG.md is out of date. Run: python eng/changelog.py')
        for error in errors:
            print('::error::' + error if args.github else error, file=sys.stderr)
        if not errors:
            print(f'Release notes are complete for {version} and CHANGELOG.md is up to date.')
        return 1 if errors else 0
    if errors:
        print('\n'.join(errors), file=sys.stderr); return 1
    CHANGELOG.write_text(text, encoding='utf-8'); print(f'Wrote CHANGELOG.md with {len(releases)} releases.'); return 0


if __name__ == '__main__':
    sys.exit(main())
