#!/usr/bin/env python3
from pathlib import Path
import json,xml.etree.ElementTree as ET,shutil,os
root=Path(__file__).resolve().parents[1];cache=Path(os.environ.get('NUGET_PACKAGES',str(Path.home()/'.nuget/packages')))
packages={}
for lock in root.rglob('packages.lock.json'):
 for target,deps in json.loads(lock.read_text())['dependencies'].items():
  for name,data in deps.items():
   if data['type']=='Project':continue
   version=data.get('resolved');directory=cache/name.lower()/version
   nuspec=directory/(name.lower()+'.nuspec')
   if not nuspec.exists():continue
   tree=ET.parse(nuspec);metadata=next(n for n in tree.getroot() if n.tag.endswith('metadata'))
   values={n.tag.split('}')[-1]:n.text for n in metadata}
   packages[name+'/'+version]={'license':values.get('license') or values.get('licenseUrl'),'project':values.get('projectUrl')}
   for f in directory.iterdir():
    if f.is_file() and ('license' in f.name.lower() or 'notice' in f.name.lower()) and f.suffix.lower() not in ['.nuspec','.nupkg','.sha512']:
     targetdir=root/'licenses'/name.lower();targetdir.mkdir(parents=True,exist_ok=True);shutil.copy2(f,targetdir/f.name)
(root/'licenses').mkdir(exist_ok=True);(root/'licenses/dependencies.json').write_text(json.dumps(dict(sorted(packages.items())),indent=2)+'\n')
print(str(len(packages))+' dependency notices')
