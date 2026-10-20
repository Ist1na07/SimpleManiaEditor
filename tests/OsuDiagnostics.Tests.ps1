$ErrorActionPreference = 'Stop'
$checkout = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $env:TEMP ('sme-diagnostics-test-' + [guid]::NewGuid().ToString('N'))
foreach ($directory in 'scripts', 'fake-osu/Logs', 'fake-osu/Songs', 'blocked/scripts') {
    [IO.Directory]::CreateDirectory((Join-Path $fixture $directory)) | Out-Null
}
$source = Join-Path $checkout 'scripts/Collect-OsuDiagnostics.ps1'
Copy-Item -LiteralPath $source -Destination (Join-Path $fixture 'scripts')
Copy-Item -LiteralPath $source -Destination (Join-Path $fixture 'blocked/scripts')
$game = Join-Path $fixture 'fake-osu'
$protected = @('osu!.exe', 'osu!.cfg', 'Songs/chart.osu', 'Logs/update_success.log')
$before = @{}
foreach ($name in $protected) {
    $path = Join-Path $game $name
    [IO.File]::WriteAllText($path, ('synthetic sentinel: ' + $name))
    $before[$name] = @{ Hash = (Get-FileHash -LiteralPath $path).Hash; Time = (Get-Item -LiteralPath $path).LastWriteTimeUtc }
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'scripts/Collect-OsuDiagnostics.ps1') -OsuRoot $game -DurationSeconds 2 -IntervalSeconds 1
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic sampling failed.' }
$report = Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts') -Directory | Select-Object -First 1
$samples = @(Get-Content -LiteralPath (Join-Path $report.FullName 'samples.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
if ($samples.Count -lt 2 -or $samples[0].Memory.CommitLimit -le 0) { throw 'Expected repeated valid memory samples.' }
foreach ($name in 'metadata.json', 'update-before.json', 'update-after.json', 'events-before.json', 'events-after.json', 'finished.json') {
    Get-Content -LiteralPath (Join-Path $report.FullName $name) -Raw | ConvertFrom-Json | Out-Null
}
foreach ($name in $protected) {
    $path = Join-Path $game $name
    if ((Get-FileHash -LiteralPath $path).Hash -ne $before[$name].Hash -or (Get-Item -LiteralPath $path).LastWriteTimeUtc -ne $before[$name].Time) {
        throw ('Game input changed: ' + $name)
    }
}
[IO.File]::WriteAllText((Join-Path $fixture 'blocked/osu!.exe'), 'synthetic game root')
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'blocked/scripts/Collect-OsuDiagnostics.ps1') 2>$null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath (Join-Path $fixture 'blocked/artifacts'))) { throw 'Output inside game root was not rejected.' }
Write-Host ('PASS: repeated samples, readable reports, unchanged fake game inputs, game-directory output rejected. Fixture: ' + $fixture)
