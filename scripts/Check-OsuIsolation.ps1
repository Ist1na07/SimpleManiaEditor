param([Parameter(Mandatory)][string]$OsuRoot, [string]$PackageRoot='', [int]$IdleSeconds=15)
$ErrorActionPreference='Stop'
$checkout=Split-Path -Parent $PSScriptRoot
if (!$PackageRoot) {
 $launcher=[IO.File]::ReadAllText((Join-Path $checkout 'Start-Editor.cmd'))
 if ($launcher -notmatch 'dist[\\/]+([^\\/"%]+)') { throw 'Cannot locate active editor build.' }
 $PackageRoot=Join-Path $checkout ('dist/'+$Matches[1]+'/app')
}
$OsuRoot=(Resolve-Path -LiteralPath $OsuRoot).Path
$PackageRoot=(Resolve-Path -LiteralPath $PackageRoot).Path
if (@(Get-Process 'osu!','osu' -ErrorAction SilentlyContinue).Count) { throw 'Exit osu! before this read-only isolation check.' }
$session=Join-Path $env:TEMP ('sme-isolation-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $session | Out-Null
$profile=Join-Path $session 'profile'; New-Item -ItemType Directory -Path $profile | Out-Null
$settingsFile=Join-Path $env:LOCALAPPDATA 'SimpleManiaEditor/Preview/playtest-settings.json'
$skin=''
if (Test-Path -LiteralPath $settingsFile) {
 $skin=(Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).SkinPath
 Copy-Item -LiteralPath $settingsFile -Destination (Join-Path $profile 'playtest-settings.json')
}
function Get-Snapshot {
 $files=@(Get-ChildItem -LiteralPath $OsuRoot -Force -File | Where-Object { $_.Extension -in '.exe','.dll','.cfg','.db','.ini','.lnk' })
 if ($skin -and (Test-Path -LiteralPath $skin -PathType Container)) { $files+=@(Get-ChildItem -LiteralPath $skin -Recurse -File) }
 $snapshot=@{}
 foreach($file in $files) {
  $snapshot[$file.FullName]=[pscustomobject]@{Bytes=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash;WriteTicks=$file.LastWriteTimeUtc.Ticks;Attributes=[string]$file.Attributes}
 }
 return $snapshot
}
$before=Get-Snapshot
$events=[Collections.Concurrent.ConcurrentQueue[object]]::new()
$watcher=[IO.FileSystemWatcher]::new($OsuRoot)
$watcher.IncludeSubdirectories=$true; $watcher.InternalBufferSize=65536
$watcher.NotifyFilter=[IO.NotifyFilters]'FileName,DirectoryName,LastWrite,Size,Attributes,Security'
$prefix='SME-Audit-'+[guid]::NewGuid().ToString('N'); $subscriptions=@()
foreach($kind in 'Changed','Created','Deleted','Renamed','Error') {
 $subscriptions+=Register-ObjectEvent -InputObject $watcher -EventName $kind -SourceIdentifier ($prefix+$kind) -MessageData $events -Action {
  $a=$Event.SourceEventArgs
  $Event.MessageData.Enqueue([pscustomobject]@{Time=[DateTimeOffset]::UtcNow.ToString('O');Kind=$Event.SourceIdentifier;Path=$a.FullPath;OldPath=$a.OldFullPath;Error=[string]$a})
 }
}
$watcher.EnableRaisingEvents=$true
$names=@('LUMACHART_AVALONIA_LOCAL_DATA_ROOT','LUMACHART_AVALONIA_EDITING_ROOT','LUMACHART_AVALONIA_WORKSPACE_ROOT','LUMACHART_AVALONIA_SINGLE_INSTANCE_SCOPE','LUMACHART_AVALONIA_ALLOW_MULTIPLE_INSTANCES')
$old=@{}; foreach($name in $names) { $old[$name]=[Environment]::GetEnvironmentVariable($name) }
$children=@(); $outcome='Failed'
try {
 [Environment]::SetEnvironmentVariable($names[0],$profile)
 [Environment]::SetEnvironmentVariable($names[1],(Join-Path $session 'editing'))
 [Environment]::SetEnvironmentVariable($names[2],(Join-Path $session 'maps'))
 [Environment]::SetEnvironmentVariable($names[3],$prefix)
 [Environment]::SetEnvironmentVariable($names[4],'1')
 $editor=Start-Process -FilePath (Join-Path $PackageRoot 'SimpleManiaEditor.exe') -WindowStyle Hidden -PassThru
 $children+=$editor
 Start-Sleep -Seconds $IdleSeconds
 if ($editor.HasExited) { throw 'Editor exited before idle check.' }
 [void]$editor.CloseMainWindow()
 if (!$editor.WaitForExit(15000)) { throw 'Editor did not finish its normal close.' }
 # All music and notes below are generated in the owned fixture. The skin is read-only.
 $env:SME_ISOLATION_FIXTURE=$session
 @'
import os,wave,math,struct,zipfile
from pathlib import Path
r=Path(os.environ['SME_ISOLATION_FIXTURE'])
with wave.open(str(r/'tone.wav'),'wb') as w:
 w.setparams((1,2,44100,0,'NONE','not compressed'))
 w.writeframes(b''.join(struct.pack('<h',int(500*math.sin(i*2*math.pi*440/44100))) for i in range(44100*12)))
chart='osu file format v14\n[General]\nAudioFilename: tone.wav\nMode:3\n[Metadata]\nTitle:Isolation fixture\nArtist:Fixture\nCreator:Fixture\nVersion:7K\n[Difficulty]\nCircleSize:7\nOverallDifficulty:8\nHPDrainRate:8\n[TimingPoints]\n0,500,4,1,0,0,1,0\n[HitObjects]\n36,192,1000,1,0,0:0:0:0:\n256,192,2000,128,0,5000:0:0:0:0:\n'
with zipfile.ZipFile(r/'fixture.osz','w') as z:
 z.writestr('fixture.osu',chart);z.write(r/'tone.wav','tone.wav')
'@ | python -
 if ($LASTEXITCODE -ne 0) { throw 'Cannot create synthetic fixture.' }
 $request=Join-Path $session 'request.json'
 @{ProtocolVersion=1;BeatmapArchive=(Join-Path $session 'fixture.osz');ChartEntry='fixture.osu';DataRoot=(Join-Path $profile 'osu-host');StartMilliseconds=0;Rate=1;PreservePitch=$true;AutoPlay=$true;FullScreen=$false;Volume=0;SkinPaths=@($skin | Where-Object {$_});SelectedSkinPath=$skin} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $request -Encoding utf8NoBOM
 $hostProcess=Start-Process -FilePath (Join-Path $PackageRoot 'osu-host/SimpleManiaEditor.OsuHost.exe') -ArgumentList @('--request',('"'+$request+'"'),'--verify') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $session 'host.stdout.txt') -RedirectStandardError (Join-Path $session 'host.stderr.txt')
 $children+=$hostProcess
 if (!$hostProcess.WaitForExit(90000)) { throw 'Gameplay audit timed out.' }
 if ($hostProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath ($request+'.verified.json'))) { throw ('Gameplay audit did not pass: '+(Get-Content -LiteralPath (Join-Path $session 'host.stderr.txt') -Raw)) }
 $outcome='Passed'
}
finally {
 foreach($process in $children) { if (!$process.HasExited) { [void]$process.CloseMainWindow(); if (!$process.WaitForExit(5000)) { $process.Kill() } } }
 Start-Sleep -Seconds 2
 $watcher.EnableRaisingEvents=$false; $watcher.Dispose()
 foreach($sub in $subscriptions) { Unregister-Event -SourceIdentifier $sub.Name -ErrorAction SilentlyContinue; Remove-Job -Id $sub.Id -Force -ErrorAction SilentlyContinue }
 foreach($name in $names) { [Environment]::SetEnvironmentVariable($name,$old[$name]) }
 $after=Get-Snapshot; $differences=@()
 foreach($path in @($before.Keys)+@($after.Keys)|Sort-Object -Unique) {
  if (!$before.ContainsKey($path) -or !$after.ContainsKey($path) -or ($before[$path]|ConvertTo-Json -Compress) -ne ($after[$path]|ConvertTo-Json -Compress)) { $differences+=$path }
 }
 $report=[pscustomobject]@{Outcome=$outcome;Source=$OsuRoot;Package=$PackageRoot;Time=[DateTimeOffset]::Now.ToString('O');SnapshotFiles=$before.Count;ChangedSnapshotFiles=$differences;Events=@($events.ToArray());Fixture=$session}
 $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $session 'audit.json') -Encoding utf8NoBOM
 Write-Output ($report|Select-Object Outcome,SnapshotFiles,@{Name='ChangedFiles';Expression={$_.ChangedSnapshotFiles.Count}},@{Name='Events';Expression={$_.Events.Count}},Fixture|ConvertTo-Json -Compress)
}
if ($differences.Count -or !$events.IsEmpty) { throw 'Changes observed in osu! installation; inspect the private audit report.' }
