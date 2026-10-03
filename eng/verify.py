#!/usr/bin/env python3
from pathlib import Path
import subprocess,json,sys,os
root=Path(__file__).resolve().parents[1]
for required in ['LICENSE','THIRD_PARTY_NOTICES.md','docs/development.md','docs/providers.md','docs/plugins.md']:
 assert (root/required).is_file(),required
if (root/'assets/fonts/manifest.json').exists():
 import hashlib
 for name,meta in json.loads((root/'assets/fonts/manifest.json').read_text()).items():assert hashlib.sha256((root/'assets/fonts'/name).read_bytes()).hexdigest()==meta['sha256'],name
output=subprocess.check_output([os.environ.get('DOTNET','dotnet'),'list','package','--vulnerable','--include-transitive','--format','json'],cwd=root,text=True)
report=json.loads(output[output.index('{'):]);(root/'artifacts').mkdir(exist_ok=True);(root/'artifacts/dependency-audit.json').write_text(json.dumps(report,indent=2)+'\n')
issues=[]
for project in report.get('projects',[]):
 for framework in project.get('frameworks',[]):
  for package in framework.get('topLevelPackages',[])+framework.get('transitivePackages',[]):
   if package.get('vulnerabilities'):issues.append(package['id'])
if issues:raise SystemExit('Vulnerable packages: '+', '.join(issues))
print('License files, font hashes and dependency audit passed.')
