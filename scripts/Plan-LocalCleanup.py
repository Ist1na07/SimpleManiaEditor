from pathlib import Path
import json, os, subprocess, zipfile, zlib, collections, hashlib, re, uuid
r=Path(__file__).resolve().parent.parent
assert (r/'.git').exists() and (r/'src').is_dir(), 'Development checkout required'
(r/'artifacts').mkdir(exist_ok=True)
launchers=[r/'Start-Editor.cmd', r/'Start-Demo.cmd']
kept_runtimes={match.group(1) for launcher in launchers if launcher.exists() for match in re.finditer(r'dist[\\/]+([^\\/\"%]+)', launcher.read_text(encoding='utf-8-sig'))}
# Keep the last public package as a rollback/distribution copy beside local builds.
public=[folder.name for folder in (r/'dist').iterdir() if folder.is_dir() and re.fullmatch(r'SimpleManiaEditor-\d+\.\d+\.\d+-Distribution',folder.name)]
if public: kept_runtimes.add(max(public,key=lambda name:tuple(map(int,re.search(r'(\d+\.\d+\.\d+)',name).group(1).split('.')))))
assert kept_runtimes, 'Cannot identify active startup runtime'
latest=next(match.group(1) for match in re.finditer(r'dist[\\/]+([^\\/\"%]+)', (r/'Start-Editor.cmd').read_text(encoding='utf-8-sig')))
tracked={str((r/p).resolve()).lower() for p in subprocess.check_output(['git','-C',str(r),'ls-files','-z']).decode('utf-8').split('\0') if p}
protected={'maps','lumaChart workspace'.lower(),'editing cache','skins','backups','examples','game-data','game-data-wilben','files','data'}
binary={'.dll','.exe','.pdb','.so','.dylib','.a','.lib'}
def generated(rel):
 parts=Path(rel).parts; low=[s.lower() for s in parts]; name=low[-1]
 if any(s in protected for s in low): return False
 return (Path(name).suffix in binary or 'ipadic' in low or 'runtimes' in low or
         ('cache' in low and 'fonts' in low) or name.endswith(('.deps.json','.runtimeconfig.json')))
plan={}; recovered=[]; archive_checked=[]
def add(p,why):
 p=p.resolve(); assert p.is_relative_to(r) and p!=r
 assert str(p).lower() not in tracked
 assert not any(p.is_relative_to(r/'dist'/name) for name in kept_runtimes)
 if p.is_file(): plan[str(p.relative_to(r))]={'Bytes':p.stat().st_size,'MtimeNs':p.stat().st_mtime_ns,'Reason':why}
# Preserve non-program files unique to obsolete archives before removing the archives.
for arc in (r/'dist').glob('*.zip'):
 if any(arc.name==name+'-win-x64.zip' for name in kept_runtimes): continue
 with zipfile.ZipFile(arc) as z:
  for i in z.infolist():
   if i.is_dir(): continue
   parts=Path(i.filename.replace('/',os.sep)).parts
   assert not Path(i.filename).is_absolute() and '..' not in parts
   if generated(i.filename): continue
   dest=r/'dist'/Path(*parts)
   match=False
   if dest.is_file() and dest.stat().st_size==i.file_size:
    with dest.open('rb') as f:
     crc=0
     while b:=f.read(1024*1024): crc=zlib.crc32(b,crc)
    match=(crc & 0xffffffff)==i.CRC
   if not match:
    target=r/'artifacts'/'retained-archive-data'/arc.stem/Path(*parts)
    assert target.resolve().is_relative_to(r/'artifacts'/'retained-archive-data')
    if target.exists():
     crc=0
     with target.open('rb') as previous:
      while chunk:=previous.read(1024*1024): crc=zlib.crc32(chunk,crc)
     if target.stat().st_size==i.file_size and (crc & 0xffffffff)==i.CRC: continue
     target=target.with_name(target.stem+'-retained-'+uuid.uuid4().hex+target.suffix)
    target.parent.mkdir(parents=True,exist_ok=True)
    with z.open(i) as source, target.open('wb') as output:
     while chunk:=source.read(1024*1024): output.write(chunk)
    recovered.append({'Archive':arc.name,'Path':str(target.relative_to(r)),'Bytes':i.file_size})
 archive_checked.append(arc.name); add(arc,'Obsolete package; non-program files retained or verified')
for base in ['src','tests','artifacts']:
 for parent,dirs,files in os.walk(r/base):
  dirs[:]=[d for d in dirs if d!='.git' and not (Path(parent)/d).is_junction()]
  rel=Path(parent).relative_to(r); low=[s.lower() for s in rel.parts]
  in_build=any(s in ('bin','obj') for s in low[1:])
  for name in files:
   p=Path(parent)/name
   if str(p.resolve()).lower() in tracked: continue
   if in_build and not any(s in protected for s in low): add(p,'Rebuildable bin/obj output')
   elif base=='artifacts' and 'retained-archive-data' not in low and 'source' not in low and generated(p.relative_to(r)):
    add(p,'Temporary published program / generated font or dictionary cache')
for folder in (r/'dist').iterdir():
 if not folder.is_dir() or folder.name in kept_runtimes: continue
 for p in folder.rglob('*'):
  if p.is_file() and generated(p.relative_to(folder)): add(p,'Obsolete executable/dependency/generated cache; user data stays in place')
# Check and fingerprint every file that will stay, including user data and latest application.
kept=[]
for parent,dirs,files in os.walk(r):
 dirs[:]=[d for d in dirs if d!='.git' and not (Path(parent)/d).is_junction()]
 for name in files:
  p=Path(parent)/name; rel=str(p.relative_to(r))
  if rel in plan or name.startswith('cleanup-'): continue
  h=hashlib.file_digest(p.open('rb'),'sha256').hexdigest()
  kept.append({'Path':rel,'Bytes':p.stat().st_size,'Sha256':h})
report={'Root':str(r),'KeptRuntimeFolders':sorted(kept_runtimes),'DeleteFiles':[{'Path':p,**v} for p,v in sorted(plan.items())], 'KeepFiles':kept,'RecoveredArchiveFiles':recovered,'CheckedArchives':archive_checked}
(r/'artifacts/cleanup-plan.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
c=collections.Counter()
for p,v in plan.items(): c[v['Reason']]+=v['Bytes']
print('Planned removal:',len(plan),'files',round(sum(v['Bytes'] for v in plan.values())/1024**3,3),'GiB')
print('Preserved fingerprints:',len(kept),'files',round(sum(v['Bytes'] for v in kept)/1024**3,3),'GiB')
print('Archive extra retained:',len(recovered),'files',round(sum(v['Bytes'] for v in recovered)/1024**2,2),'MiB')
print('Reasons:',dict(c))
