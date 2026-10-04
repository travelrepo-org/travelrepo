#!/usr/bin/env python3
"""Read the TravelRepo SDK version from Directory.Build.props, the only place it is defined.

  python eng/version.py                     prints the version, for example 0.2.0
  python eng/version.py --check-tag v0.2.0  fails unless the tag is v<Version>
"""
from pathlib import Path
import argparse
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
VERSION = re.compile(r'^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')


def read():
    props = ROOT / 'Directory.Build.props'
    for group in ET.parse(props).getroot().iter('PropertyGroup'):
        node = group.find('Version')
        if node is not None and node.text and node.text.strip():
            value = node.text.strip()
            if not VERSION.match(value):
                raise SystemExit(f'{props}: Version "{value}" is not a version like 1.2.3 or 1.2.3-beta.1')
            return value
    raise SystemExit(f'{props} does not define Version')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--check-tag')
    parser.add_argument('--github', action='store_true', help='format errors as GitHub Actions annotations')
    args = parser.parse_args()
    if args.check_tag:
        version = read()
        if args.check_tag != 'v' + version:
            message = (f'Tag {args.check_tag} does not match the version in Directory.Build.props ({version}). '
                       f'Release v{version}, or change <Version> on main first.')
            print(('::error::' if args.github else '') + message, file=sys.stderr)
            sys.exit(1)
    else:
        print(read())
