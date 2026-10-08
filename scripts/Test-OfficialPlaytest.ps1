param(
    [string]$Dotnet = 'dotnet',
    [string]$HostExecutable = '',
    [string[]]$SkinPaths = @(),
    [string]$SelectedSkinPath = '',
    [string]$ExpectedSkin = '',
    [string]$DataRoot = '',
    [double]$StartMilliseconds = 0,
    [double]$Rate = 1,
    [switch]$VerifySettings,
    [switch]$SettingsOnly
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $taskRoot ('artifacts/official-playtest/run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
& (Join-Path $PSScriptRoot 'New-Demo.ps1') -OutputDirectory (Join-Path $testRoot 'map')
$chartPath = Join-Path $testRoot 'map/7K Sandbox.osu'
if ($StartMilliseconds -gt 0) {
    $text = [IO.File]::ReadAllLines($chartPath)
    $inNotes = $false
    $lines = foreach ($line in $text) {
        if ($line -eq '[HitObjects]') { $inNotes = $true; $line; continue }
        if ($inNotes -and $line -match '^\d+,\d+,([\d.]+),') {
            if ([double]$Matches[1] -ge $StartMilliseconds) { $line }
        } else { $line }
    }
    [IO.File]::WriteAllLines($chartPath, [string[]]$lines, [Text.UTF8Encoding]::new($false))
}
$archive = Join-Path $testRoot 'practice.osz'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $testRoot 'map'), $archive)
if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Join-Path $testRoot 'game-data' }
$request = @{
    ProtocolVersion=1; BeatmapArchive=$archive; ChartEntry='7K Sandbox.osu'; DataRoot=[IO.Path]::GetFullPath($DataRoot)
    StartMilliseconds=$StartMilliseconds; Rate=$Rate; PreservePitch=$true; AutoPlay=$true; ManiaSpeed=31
    FullScreen=$false; Volume=0; SelectedSkinPath=$SelectedSkinPath; SkinPaths=$SkinPaths
    KeyBindings=@{'7'=@('S','D','F','Space','J','K','L')}
    SettingsOnly=[bool]$SettingsOnly
}
$request.KeyBindings['11'] = @('A','S','D','F','G','Space','H','J','K','L','OemSemicolon')
$manifest = Join-Path $testRoot 'request.json'
$request | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifest -Encoding utf8
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
$skinArchiveHashes = @{}
foreach ($path in $SkinPaths) {
    if (Test-Path -LiteralPath $path -PathType Leaf) { $skinArchiveHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
if ([string]::IsNullOrWhiteSpace($HostExecutable)) {
    & $Dotnet build (Join-Path $taskRoot 'src/SimpleManiaEditor.OsuHost/SimpleManiaEditor.OsuHost.csproj') -c Release -r win-x64 -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Official host build failed.' }
    $hostDll = Join-Path $taskRoot 'src/SimpleManiaEditor.OsuHost/bin/Release/net10.0-windows/win-x64/SimpleManiaEditor.OsuHost.dll'
    $arguments = @('exec', ('"' + $hostDll + '"'), '--request', ('"' + $manifest + '"'), '--verify')
    $executable = $Dotnet
} else {
    $executable = [IO.Path]::GetFullPath($HostExecutable)
    $arguments = @('--request', ('"' + $manifest + '"'), '--verify')
}
if ($VerifySettings) { $arguments += '--verify-settings' }
$process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testRoot 'stdout.txt') -RedirectStandardError (Join-Path $testRoot 'stderr.txt')
Write-Output ('Official engine verification: ' + $testRoot)
if (-not $process.WaitForExit(60000)) {
    # This test owns precisely this process, never any existing osu!/editor process.
    $process.Kill()
    throw 'Official host verification exceeded 60 seconds. Inspect its logs.'
}
$process.WaitForExit()
if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath ($manifest + '.error.txt'))) {
    if (Test-Path -LiteralPath ($manifest + '.error.txt')) { Get-Content -LiteralPath ($manifest + '.error.txt') }
    Get-Content -LiteralPath (Join-Path $testRoot 'stderr.txt')
    throw 'Official engine verification failed.'
}
$ready = Get-Content -LiteralPath ($manifest + '.ready.json') -Raw | ConvertFrom-Json
if ($SettingsOnly) {
    if ($ready.Mode -ne 'settings' -or (Get-Item -LiteralPath ($manifest + '.png')).Length -lt 20000) { throw 'Official settings did not load.' }
    Write-Output ('PASS official settings-only entry, no gameplay lock; screenshot=' + $manifest + '.png')
    return
}
$verified = Get-Content -LiteralPath ($manifest + '.verified.json') -Raw | ConvertFrom-Json
if ($ready.OfficialPlayer -ne 'osu.Game.Screens.Play.Player' -or $ready.Ruleset -ne 'mania' -or $ready.KeyCount -ne 7) { throw 'Wrong game/ruleset.' }
if ($ExpectedSkin -and $ready.Skin -notlike ('*' + $ExpectedSkin + '*')) { throw ('Wrong skin: ' + $ready.Skin) }
if (-not $verified.AutoPlay -or $verified.Accuracy -lt 0.999) { throw ('Autoplay did not judge perfectly: ' + ($verified | ConvertTo-Json -Compress)) }
if ($verified.Time -lt $StartMilliseconds + 500) { throw 'Gameplay clock did not advance from requested time.' }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) { throw 'Importer modified/deleted the handoff archive.' }
foreach ($path in $skinArchiveHashes.Keys) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $skinArchiveHashes[$path]) { throw 'Importer modified/deleted a skin archive.' }
}
if ((Get-Item -LiteralPath ($manifest + '.png')).Length -lt 20000) { throw 'GPU screenshot is empty.' }
if ($VerifySettings) {
    $switched = Get-Content -LiteralPath ($manifest + '.skin-switched.json') -Raw | ConvertFrom-Json
    $config = [IO.File]::ReadAllText((Join-Path $DataRoot 'game.ini'))
    if ($config -notmatch [regex]::Escape($switched.Id)) { throw 'Official selector did not persist skin choice.' }
    if ((Get-Item -LiteralPath ($manifest + '.settings.png')).Length -lt 20000) { throw 'Settings screenshot is empty.' }
    Write-Output ('PASS actual official skin dropdown and persistent selection: ' + $switched.Skin)
}
Write-Output ('PASS official GPU gameplay, 7K, skin=' + $ready.Skin + ', perfect autoplay, current time, retained archive; screenshot=' + $manifest + '.png')
