#!/usr/bin/env python3
"""Points Package.swift at the released TravelRepo.xcframework instead of the local build.

    python3 eng/swift-release.py <tag> <checksum> [--repository owner/name]

The release workflow runs it on a commit of its own, which it tags with the version without "v" (v1.2.3 → 1.2.3), so
SwiftPM resolves that version to a manifest with the download address and checksum of the zip attached to the release.
"""
from pathlib import Path
import argparse, re, sys

parser = argparse.ArgumentParser()
parser.add_argument('tag')
parser.add_argument('checksum')
parser.add_argument('--repository', default='travelrepo-org/travelrepo')
args = parser.parse_args()
if not re.fullmatch(r'v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?', args.tag): sys.exit(f'Not a release tag: {args.tag}')
if not re.fullmatch(r'[0-9a-f]{64}', args.checksum): sys.exit(f'Not a SwiftPM checksum: {args.checksum}')

manifest = Path(__file__).resolve().parents[1] / 'Package.swift'
text = manifest.read_text()
local = 'let binary: Target = .binaryTarget(name: "CTravelRepo", path: "artifacts/native/TravelRepo.xcframework")'
if text.count(local) != 1: sys.exit('Package.swift does not contain the local binary target line exactly once.')
url = f'https://github.com/{args.repository}/releases/download/{args.tag}/TravelRepo.xcframework.zip'
released = f'let binary: Target = .binaryTarget(name: "CTravelRepo", url: "{url}", checksum: "{args.checksum}")'
manifest.write_text(text.replace(local, released))
print(released)
