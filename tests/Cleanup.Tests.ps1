$ErrorActionPreference='Stop'
$checkout=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $env:TEMP ('sme-cleanup-test-'+[guid]::NewGuid().ToString('N'))
foreach($directory in 'scripts','artifacts','src/bin','dist/current/app','dist/demo','dist/old/LumaChart Workspace','dist/old/Skins') {
 New-Item -ItemType Directory -Path (Join-Path $fixture $directory) -Force | Out-Null
}
Copy-Item -LiteralPath (Join-Path $checkout 'scripts/Clean-LocalBuilds.ps1'),(Join-Path $checkout 'scripts/Plan-LocalCleanup.py') -Destination (Join-Path $fixture 'scripts')
& git -C $fixture init --quiet
if($LASTEXITCODE -ne 0){throw 'Cannot initialise isolated cleanup fixture.'}
[IO.File]::WriteAllText((Join-Path $fixture 'Start-Editor.cmd'),'start "" "%~dp0dist\current\app\editor.exe"')
[IO.File]::WriteAllText((Join-Path $fixture 'Start-Demo.cmd'),'start "" "%~dp0dist\demo\editor.exe"')
$keep=@('src/bin/tracked.dll','dist/current/app/editor.exe','dist/demo/editor.exe','dist/old/LumaChart Workspace/song.osu','dist/old/Skins/skin.png','dist/old/notes.txt')
foreach($relative in $keep){[IO.File]::WriteAllText((Join-Path $fixture $relative),'keep '+$relative)}
& git -C $fixture add -f -- src/bin/tracked.dll
if($LASTEXITCODE -ne 0){throw 'Cannot track isolated source fixture.'}
$hashes=@{};foreach($relative in $keep){$hashes[$relative]=(Get-FileHash -LiteralPath (Join-Path $fixture $relative)).Hash}
$candidate=Join-Path $fixture 'src/bin/test.deps.json'
[IO.File]::WriteAllText($candidate,'first build')
[IO.File]::WriteAllText((Join-Path $fixture 'dist/old/editor.exe'),'obsolete generated program')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::Open((Join-Path $fixture 'dist/old.zip'),[IO.Compression.ZipArchiveMode]::Create)
try {
 $entry=$archive.CreateEntry('OldMap/unique.osu')
 $writer=[IO.StreamWriter]::new($entry.Open())
 try{$writer.Write('unique original chart, retain before deleting archive')}finally{$writer.Dispose()}
}finally{$archive.Dispose()}
& pwsh -NoProfile -File (Join-Path $fixture 'scripts/Clean-LocalBuilds.ps1') -Preview
if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $candidate)){throw 'Preview failed or deleted data.'}
# A build changed after preview: the old frozen-plan tool failed here.
[IO.File]::WriteAllText($candidate,'second build with changed size and time')
& pwsh -NoProfile -File (Join-Path $fixture 'scripts/Clean-LocalBuilds.ps1')
if($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $candidate)){throw 'Fresh cleanup did not handle a rebuilt candidate.'}
foreach($relative in $keep){
 if((Get-FileHash -LiteralPath (Join-Path $fixture $relative)).Hash -ne $hashes[$relative]){throw ('Preserved data changed: '+$relative)}
}
$retained=@(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/retained-archive-data') -Filter unique.osu -Recurse -File)
if(!$retained.Count -or [IO.File]::ReadAllText($retained[0].FullName) -ne 'unique original chart, retain before deleting archive'){throw 'Unique archive data was lost.'}
if(Test-Path -LiteralPath (Join-Path $fixture 'dist/old.zip')){throw 'Verified obsolete archive was not removed.'}
$report=Get-Content -LiteralPath (Join-Path $fixture 'artifacts/cleanup-result.json') -Raw | ConvertFrom-Json
if(!$report.PreservedFilesVerified -or $report.DeletedFiles -ne 3 -or (Test-Path -LiteralPath (Join-Path $fixture 'scripts/cleanup-result.json'))){throw 'Cleanup report was misplaced or incomplete.'}
Write-Output ('PASS fresh plan after rebuild, preview, tracked/generated distinction, active/demo runtime, map/skin/notes preservation and unique archive recovery; fixture='+$fixture)
