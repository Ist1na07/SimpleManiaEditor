param([string]$Dotnet='dotnet')
$ErrorActionPreference='Stop'
$checkout=Split-Path -Parent $PSScriptRoot
[xml]$definition=Get-Content -LiteralPath (Join-Path $checkout 'src/LumaChart.Avalonia/LumaChart.Avalonia.csproj') -Raw
$version=[string](($definition.Project.PropertyGroup|Where-Object Version|Select-Object -First 1).Version)
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid version.'}
$folder="SimpleManiaEditor-$version-Local-"+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')
$destination=Join-Path $checkout ('dist/'+$folder)
if(Test-Path -LiteralPath $destination){throw 'Choose a fresh local build directory.'}
$app=Join-Path $destination 'app'
Push-Location $checkout
try {
 & $Dotnet publish src/LumaChart.Avalonia/LumaChart.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $app
 if($LASTEXITCODE -ne 0){throw 'Local editor publish failed.'}
 & $Dotnet publish src/SimpleManiaEditor.OsuHost/SimpleManiaEditor.OsuHost.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $app 'osu-host')
 if($LASTEXITCODE -ne 0){throw 'Local playtest publish failed.'}
 & (Join-Path $PSScriptRoot 'Collect-OsuHostNotices.ps1') -Destination $app -ProjectAssets 'src/LumaChart.Avalonia/obj/project.assets.json' -Caption 'Editor dependencies'
 & (Join-Path $PSScriptRoot 'Collect-OsuHostNotices.ps1') -Destination (Join-Path $app 'osu-host')
 $smoke=Start-Process -FilePath (Join-Path $app 'SimpleManiaEditor.exe') -ArgumentList '--smoke-test' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $destination 'smoke.txt') -RedirectStandardError (Join-Path $destination 'smoke-error.txt')
 if($smoke.ExitCode -ne 0){throw 'Local editor runtime check failed.'}
 $storage=Start-Process -FilePath (Join-Path $app 'osu-host/SimpleManiaEditor.OsuHost.exe') -ArgumentList '--verify-storage' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $destination 'storage.txt') -RedirectStandardError (Join-Path $destination 'storage-error.txt')
 if($storage.ExitCode -ne 0){throw 'Local playtest storage check failed.'}
 Copy-Item -LiteralPath (Join-Path $checkout 'docs/FEATURES.md') -Destination $destination
 $launcher='@echo off'+[Environment]::NewLine+('start "" "%~dp0dist\{0}\app\SimpleManiaEditor.exe" %*' -f $folder)+[Environment]::NewLine
 [IO.File]::WriteAllText((Join-Path $checkout 'Start-Editor.cmd'),$launcher,[Text.ASCIIEncoding]::new())
 Write-Output ('PASS local build and launcher updated; no ZIP, tag or GitHub release created: '+$destination)
}
finally{Pop-Location}
