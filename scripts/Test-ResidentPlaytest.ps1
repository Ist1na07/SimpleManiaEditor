param([string]$Dotnet='dotnet', [string]$HostExecutable='', [string[]]$SkinPaths=@(), [string]$SelectedSkinPath='')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$testRoot=Join-Path $taskRoot ('artifacts/resident-playtest/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $testRoot -Force | Out-Null
& (Join-Path $PSScriptRoot 'New-Demo.ps1') -OutputDirectory (Join-Path $testRoot 'map')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=Join-Path $testRoot 'practice.osz'
[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $testRoot 'map'),$archive)
$request=@{ProtocolVersion=1;BeatmapArchive=$archive;ChartEntry='7K Sandbox.osu';DataRoot=(Join-Path $testRoot 'game-data');StartMilliseconds=0;Rate=1;PreservePitch=$true;AutoPlay=$true;ManiaSpeed=31;FullScreen=$false;Volume=0;SkinPaths=$SkinPaths;SelectedSkinPath=$SelectedSkinPath;KeyBindings=@{'7'=@('S','D','F','Space','J','K','L')};SettingsOnly=$true;WarmupOnly=$true}
$bootstrap=Join-Path $testRoot 'bootstrap.json'
$request | ConvertTo-Json -Depth 5 | Set-Content $bootstrap -Encoding utf8
if(-not $HostExecutable){
    & $Dotnet build (Join-Path $taskRoot 'src/SimpleManiaEditor.OsuHost/SimpleManiaEditor.OsuHost.csproj') -c Release -r win-x64 -warnaserror
    if($LASTEXITCODE -ne 0){throw 'Resident host build failed'}
    $info=[Diagnostics.ProcessStartInfo]::new($Dotnet)
    $info.ArgumentList.Add('exec'); $info.ArgumentList.Add((Join-Path $taskRoot 'src/SimpleManiaEditor.OsuHost/bin/Release/net10.0-windows/win-x64/SimpleManiaEditor.OsuHost.dll'))
} else { $info=[Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($HostExecutable)) }
$info.ArgumentList.Add('--request');$info.ArgumentList.Add($bootstrap);$info.ArgumentList.Add('--resident');$info.ArgumentList.Add('--verify')
$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$info.StandardInputEncoding=[Text.UTF8Encoding]::new($false);$info.StandardOutputEncoding=[Text.Encoding]::UTF8
$clock=[Diagnostics.Stopwatch]::StartNew()
$process=[Diagnostics.Process]::Start($info)
$errors=$process.StandardError.ReadToEndAsync()
$events=[Collections.Generic.List[object]]::new()
function Read-Until([string]$kind){
    while($true){
        $line=$process.StandardOutput.ReadLineAsync()
        if(-not $line.Wait(60000)){throw ('Resident timeout: '+$kind)}
        if($null -eq $line.Result){throw ('Resident exited: '+$errors.GetAwaiter().GetResult())}
        try{$message=$line.Result | ConvertFrom-Json -ErrorAction Stop}catch{continue}
        $events.Add($message)
        if($message.Kind -eq 'error'){throw $message.Detail}
        if($message.Kind -eq $kind){return $message}
    }
}
function Send-Message([string]$kind,[string]$path=''){
    $process.StandardInput.WriteLine((@{Kind=$kind;Path=$path} | ConvertTo-Json -Compress));$process.StandardInput.Flush()
}
try{
    Read-Until 'warm' | Out-Null
    $cold=$clock.Elapsed.TotalMilliseconds
    $times=[Collections.Generic.List[double]]::new()
    for($iteration=0;$iteration -lt 3;$iteration++){
        $request.SettingsOnly=$false;$request.WarmupOnly=$false
        $path=Join-Path $testRoot ('request-'+$iteration+'.json')
        $request | ConvertTo-Json -Depth 5 | Set-Content $path -Encoding utf8
        $click=[Diagnostics.Stopwatch]::StartNew();Send-Message 'open' $path
        $ready=Read-Until 'ready';$times.Add($click.Elapsed.TotalMilliseconds)
        Read-Until 'done' | Out-Null
        $result=Get-Content ($path+'.verified.json') -Raw | ConvertFrom-Json
        if(-not $result.AutoPlay -or $result.Accuracy -lt .999){throw 'Resident replay did not judge perfectly'}
        if($process.HasExited){throw 'Resident restarted/exited between requests'}
    }
    $request.SettingsOnly=$true;$path=Join-Path $testRoot 'settings.json'
    $request | ConvertTo-Json -Depth 5 | Set-Content $path -Encoding utf8
    Send-Message 'open' $path;Read-Until 'ready' | Out-Null;Read-Until 'done' | Out-Null
    if((Get-Item ($path+'.png')).Length -lt 20000){throw 'Resident settings screenshot missing'}
    Send-Message 'shutdown'
    if(-not $process.WaitForExit(10000)){throw 'Resident did not shut down'}
    if($process.ExitCode -ne 0){throw 'Resident abnormal exit'}
    @{ColdWarmupMs=$cold;ClickToReadyMs=@($times);Events=@($events);ProcessId=$process.Id} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $testRoot 'timings.json') -Encoding utf8
    Write-Output ('PASS one resident PID, three perfect GPU playtests, return/reopen, complete settings and shutdown; timings='+($times -join ',')+' ms; results='+$testRoot)
}finally{
    if(-not $process.HasExited){$process.Kill();$process.WaitForExit()}
    $process.Dispose()
}
