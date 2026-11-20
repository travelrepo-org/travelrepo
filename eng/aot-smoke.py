#!/usr/bin/env python3
"""Publishes tests/TravelRepo.AotSmoke with NativeAOT for this machine and runs it on the samples.

Fails when the publish reports trim or AOT warnings (IL2xxx, IL3xxx), in TravelRepo or in a dependency, or when a
step of the smoke test fails. Needs the native toolchain NativeAOT uses: clang on Linux, Xcode on macOS, and
Visual Studio's C++ tools on Windows.
"""
from pathlib import Path
import os, re, subprocess, sys

root = Path(__file__).resolve().parents[1]
project = root / 'tests' / 'TravelRepo.AotSmoke' / 'TravelRepo.AotSmoke.csproj'
output = root / 'artifacts' / 'aot-smoke'
dotnet = os.environ.get('DOTNET', 'dotnet')

publish = subprocess.run([dotnet, 'publish', str(project), '-c', 'Release', '-o', str(output)],
                         cwd=root, text=True, capture_output=True)
print(publish.stdout, publish.stderr, sep='', flush=True)
if publish.returncode != 0: raise SystemExit('The NativeAOT publish failed.')
warnings = sorted(set(re.findall(r'^.*warning IL[23]\d{3}:.*$', publish.stdout + publish.stderr, re.MULTILINE)))
if warnings: raise SystemExit(f'{len(warnings)} trim or AOT warnings:\n' + '\n'.join(warnings))

binary = output / ('TravelRepo.AotSmoke.exe' if os.name == 'nt' else 'TravelRepo.AotSmoke')
print(f'{binary.name}: {binary.stat().st_size / 1_000_000:.1f} MB', flush=True)
sys.exit(subprocess.run([str(binary), str(root / 'samples')]).returncode)
