param([string]$Dotnet = 'dotnet', [switch]$Zip)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
[xml]$projectDefinition = Get-Content -LiteralPath (Join-Path $taskRoot 'src/LumaChart.Avalonia/LumaChart.Avalonia.csproj') -Raw
$releaseVersion = [string](($projectDefinition.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version)
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid project release version.' }
$publishRoot = Join-Path $taskRoot ("dist/SimpleManiaEditor-$releaseVersion")
$releaseNotes = Join-Path $taskRoot ("docs/RELEASE-$releaseVersion.md")
if (-not (Test-Path -LiteralPath $releaseNotes)) { $releaseNotes = Join-Path $taskRoot 'docs/RELEASE-0.1.md' }
Push-Location $taskRoot
try {
    & $Dotnet publish 'src/LumaChart.Avalonia/LumaChart.Avalonia.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'Windows portable publish failed.' }
    & $Dotnet publish 'src/SimpleManiaEditor.OsuHost/SimpleManiaEditor.OsuHost.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o (Join-Path $publishRoot 'osu-host')
    if ($LASTEXITCODE -ne 0) { throw 'Official osu! gameplay host publish failed.' }
    & (Join-Path $PSScriptRoot 'Collect-OsuHostNotices.ps1') -Destination (Join-Path $publishRoot 'osu-host')
    & (Join-Path $PSScriptRoot 'New-Demo.ps1') -OutputDirectory (Join-Path $publishRoot 'examples/7K-Sandbox')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/QUICKSTART.md') -Destination (Join-Path $publishRoot 'QuickStart.md')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/PLAYTEST.md') -Destination (Join-Path $publishRoot 'PLAYTEST.md')
    Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $publishRoot 'ReleaseNotes.md')
    $handoffNotes = Join-Path $taskRoot 'docs/DEVELOPMENT-HANDOFF.md'
    if (Test-Path -LiteralPath $handoffNotes) {
        Copy-Item -LiteralPath $handoffNotes -Destination (Join-Path $publishRoot 'DEVELOPMENT-HANDOFF.md')
    }
    $trackNotes = Join-Path $taskRoot 'docs/WORKBENCH-TRACKS.md'
    if (Test-Path -LiteralPath $trackNotes) {
        Copy-Item -LiteralPath $trackNotes -Destination (Join-Path $publishRoot 'WORKBENCH-TRACKS.md')
    }
    $backlogNotes = Join-Path $taskRoot 'docs/BACKLOG.md'
    if (Test-Path -LiteralPath $backlogNotes) {
        Copy-Item -LiteralPath $backlogNotes -Destination (Join-Path $publishRoot 'BACKLOG.md')
    }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/FEATURES.md') -Destination (Join-Path $publishRoot 'FEATURES.md')
    $performanceNotes = Join-Path $taskRoot 'docs/performance'
    if (Test-Path -LiteralPath $performanceNotes) {
        Copy-Item -LiteralPath $performanceNotes -Destination $publishRoot -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Demo.cmd') -Destination (Join-Path $publishRoot 'Demo.cmd')
    $verificationRoot = Join-Path $taskRoot 'artifacts'
    New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
    $smokeOutput = Join-Path $verificationRoot 'publish-smoke.stdout.txt'
    $smokeError = Join-Path $verificationRoot 'publish-smoke.stderr.txt'
    # PowerShell can return before a GUI executable exits. Wait for the process;
    # LASTEXITCODE from the previous dotnet invocation is not a smoke-test result.
    $smoke = Start-Process -FilePath (Join-Path $publishRoot 'SimpleManiaEditor.exe') -ArgumentList '--smoke-test' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $smokeOutput -RedirectStandardError $smokeError
    Get-Content -LiteralPath $smokeOutput
    Get-Content -LiteralPath $smokeError
    if ($smoke.ExitCode -ne 0) { throw 'Published runtime smoke test failed.' }
    if ($Zip) {
        Compress-Archive -LiteralPath $publishRoot -DestinationPath (Join-Path $taskRoot "dist/SimpleManiaEditor-$releaseVersion-win-x64.zip") -Force
    }
}
finally { Pop-Location }
