param([string]$Dotnet = 'dotnet', [string]$RuntimeDotnet = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RuntimeDotnet)) { $RuntimeDotnet = $Dotnet }
Push-Location $taskRoot
try {
    & $Dotnet build 'src/LumaChart.Specs/LumaChart.Specs.csproj' -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Core test build failed.' }
    & $RuntimeDotnet exec --roll-forward Major 'src/LumaChart.Specs/bin/Release/net8.0/LumaChart.Specs.dll'
    if ($LASTEXITCODE -ne 0) { throw 'Core specifications failed.' }
    & $Dotnet build 'tests/SimpleManiaEditor.UiSmoke/SimpleManiaEditor.UiSmoke.csproj' -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'UI test build failed.' }
    & $RuntimeDotnet exec --roll-forward Major 'tests/SimpleManiaEditor.UiSmoke/bin/Release/net8.0-windows/SimpleManiaEditor.UiSmoke.dll' 'artifacts/screenshots'
    if ($LASTEXITCODE -ne 0) { throw 'Headless UI tests failed.' }
}
finally { Pop-Location }
