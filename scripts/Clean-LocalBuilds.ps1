param([switch]$Preview)
$ErrorActionPreference = 'Stop'
$cleanupRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if (!(Test-Path -LiteralPath (Join-Path $cleanupRoot '.git')) -or !(Test-Path -LiteralPath (Join-Path $cleanupRoot 'Start-Editor.cmd'))) { throw 'Development checkout required.' }
# A saved plan becomes stale after every build. Always inventory the current checkout.
& python (Join-Path $PSScriptRoot 'Plan-LocalCleanup.py')
if ($LASTEXITCODE -ne 0) { throw 'Cannot generate a current cleanup plan.' }
$cleanupPlan = Get-Content -LiteralPath (Join-Path $cleanupRoot 'artifacts/cleanup-plan.json') -Encoding UTF8 -Raw | ConvertFrom-Json
if ($cleanupPlan.Root -ne $cleanupRoot) { throw 'Plan belongs to another workspace.' }
$cleanupPrefix = $cleanupRoot + '\'
$cleanupRuntimes = @($cleanupPlan.KeptRuntimeFolders | ForEach-Object { Join-Path $cleanupRoot ('dist/' + $_) })
if ($Preview) { Write-Host 'Preview only. No generated files deleted.'; return }
$cleanupEpochTicks = [long]621355968000000000
$cleanupParents = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Get-CheckedCleanupPath([string]$relativePath) {
    if ([IO.Path]::IsPathRooted($relativePath)) { throw "Absolute plan path: $relativePath" }
    $target = [IO.Path]::GetFullPath((Join-Path $cleanupRoot $relativePath))
    if (-not $target.StartsWith($cleanupPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Outside workspace: $relativePath" }
    $parent = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($target)) -Force
    while ($parent -and $parent.FullName.StartsWith($cleanupPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        if (-not $cleanupParents.Add($parent.FullName)) { break }
        if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked directory: $($parent.FullName)" }
        $parent = $parent.Parent
    }
    return $target
}

function Assert-PreservedFiles {
    $checked = 0
    foreach ($entry in $cleanupPlan.KeepFiles) {
        $target = Get-CheckedCleanupPath $entry.Path
        $item = Get-Item -LiteralPath $target -Force
        if ($item.PSIsContainer -or $item.Length -ne $entry.Bytes) { throw "Preserved file changed: $($entry.Path)" }
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($hash -ne $entry.Sha256) { throw "Preserved file changed: $($entry.Path)" }
        $checked++
        if ($checked % 2000 -eq 0) { Write-Host "Verified $checked preserved files..." }
    }
}

$running = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($cleanupPrefix, [StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count) { throw 'Close editor/playtest windows and project tools, then run this file again.' }

Write-Host 'Checking the newly generated cleanup plan. No files have been deleted yet.'
$tracked = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$trackedFiles = & git -C $cleanupRoot -c core.quotepath=false ls-files
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify tracked source files. Cleanup cancelled.' }
foreach ($path in $trackedFiles) { [void]$tracked.Add([IO.Path]::GetFullPath((Join-Path $cleanupRoot $path))) }
foreach ($entry in $cleanupPlan.DeleteFiles) {
    $target = Get-CheckedCleanupPath $entry.Path
    if ($tracked.Contains($target) -or @($cleanupRuntimes | Where-Object { $target.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) -or $target -eq $_ + '-win-x64.zip' }).Count -gt 0 -or
        $entry.Path -match '(^|[\\/])(Maps|LumaChart Workspace|Editing Cache|Skins|Backups|examples|game-data|game-data-wilben|files|data)([\\/]|$)') {
        throw "Protected target: $($entry.Path)"
    }
    if (-not (Test-Path -LiteralPath $target)) { continue }
    $item = Get-Item -LiteralPath $target -Force
    $mtime = [long](($item.LastWriteTimeUtc.Ticks - $cleanupEpochTicks) * 100)
    if ($item.PSIsContainer -or $item.Length -ne $entry.Bytes -or $mtime -ne $entry.MtimeNs -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Deletion candidate changed: $($entry.Path)" }
}
Assert-PreservedFiles

Write-Host 'Removing only individually listed generated files; keeping source, charts, skins, settings, and latest release.'
$deleted = 0
$bytes = [long]0
$failures = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $cleanupPlan.DeleteFiles) {
    $target = Get-CheckedCleanupPath $entry.Path
    if (-not (Test-Path -LiteralPath $target)) { continue }
    try {
        $item = Get-Item -LiteralPath $target -Force
        $mtime = [long](($item.LastWriteTimeUtc.Ticks - $cleanupEpochTicks) * 100)
        if ($item.Length -ne $entry.Bytes -or $mtime -ne $entry.MtimeNs) { throw 'File changed since inventory.' }
        Remove-Item -LiteralPath $target -Force -ErrorAction Stop
        $deleted++
        $bytes += $entry.Bytes
        if ($deleted % 2000 -eq 0) { Write-Host "Removed $deleted generated files..." }
    } catch { $failures.Add([pscustomobject]@{ Path = $entry.Path; Error = $_.Exception.Message }) }
}
$result = [pscustomobject]@{ DeletedFiles = $deleted; DeletedBytes = $bytes; PreservedFilesVerified = $false; Failures = @($failures.ToArray()) }
$resultPath = Join-Path $cleanupRoot 'artifacts/cleanup-result.json'
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding UTF8
Write-Host 'Verifying every preserved file again...'
Assert-PreservedFiles
$result.PreservedFilesVerified = $true
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding UTF8
Write-Host ("Finished. Freed {0:N2} GiB; skipped failures: {1}." -f ($bytes / 1GB), $failures.Count)
Write-Host 'Launch the current editor using Start-Editor.cmd as before.'
if ($failures.Count) { throw 'Some generated files were locked or changed. See artifacts\cleanup-result.json.' }
