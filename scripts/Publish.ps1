param([string]$Dotnet = 'dotnet', [switch]$Zip)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $taskRoot 'dist/SimpleManiaEditor'
Push-Location $taskRoot
try {
    & $Dotnet publish 'src/LumaChart.Avalonia/LumaChart.Avalonia.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'Windows portable publish failed.' }
    & (Join-Path $PSScriptRoot 'New-Demo.ps1') -OutputDirectory (Join-Path $publishRoot 'examples/7K-Sandbox')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/QUICKSTART.md') -Destination (Join-Path $publishRoot 'QuickStart.md')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/RELEASE-0.1.md') -Destination (Join-Path $publishRoot 'ReleaseNotes.md')
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
        Compress-Archive -LiteralPath $publishRoot -DestinationPath (Join-Path $taskRoot 'dist/SimpleManiaEditor-0.1.0-win-x64.zip') -Force
    }
}
finally { Pop-Location }
