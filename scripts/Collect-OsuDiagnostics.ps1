[CmdletBinding()]
param(
    [string]$OsuRoot,
    [ValidateRange(0, 7200)][int]$DurationSeconds = 0,
    [ValidateRange(1, 30)][int]$IntervalSeconds = 2
)

# Observe only. Never launch/attach a debugger, stop a game, change registry or
# write inside an osu! installation. Reports stay in this checkout's artifacts.
$ErrorActionPreference = 'Stop'
$checkout = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $checkout ('artifacts/osu-diagnostics-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
for ($ancestor = [IO.DirectoryInfo]::new($destination); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Diagnostic output must not pass through directory links.'
    }
    if ([IO.File]::Exists((Join-Path $ancestor.FullName 'osu!.exe'))) {
        throw 'Diagnostic output must not be inside an osu! installation.'
    }
}
[IO.Directory]::CreateDirectory($destination) | Out-Null
$encoding = [Text.UTF8Encoding]::new($false)
$roots = @{}
if ($OsuRoot) { $roots[[IO.Path]::GetFullPath($OsuRoot)] = $true }

function Save-Json($Name, $Value) {
    [IO.File]::WriteAllText((Join-Path $destination $Name), (ConvertTo-Json -InputObject $Value -Depth 10), $encoding)
}

function Get-LogSnapshot {
    @($roots.Keys | ForEach-Object {
        $path = Join-Path $_ 'Logs/update_success.log'
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            try {
                $file = Get-Item -LiteralPath $path
                # Windows PowerShell 5's Get-Content -Tail can stall on some
                # newline-free logs. Bound both allocation and bytes read.
                $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
                try {
                    $count = [int][Math]::Min(32768, $stream.Length)
                    $stream.Position = [Math]::Max(0, $stream.Length - $count)
                    $buffer = [byte[]]::new($count)
                    $read = $stream.Read($buffer, 0, $count)
                    $text = [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
                } finally { $stream.Dispose() }
                [pscustomobject]@{ Path = $path; ModifiedUtc = $file.LastWriteTimeUtc.ToString('o'); Lines = @($text -split '\r?\n' | Select-Object -Last 100) }
            } catch { [pscustomobject]@{ Path = $path; Error = $_.Exception.Message } }
        }
    })
}

function Get-Events($Since) {
    $issues = [Collections.Generic.List[string]]::new()
    $records = @()
    foreach ($query in @(
        @{ LogName = 'Application'; Id = @(1000, 1001, 1026); StartTime = $Since },
        @{ LogName = 'System'; Id = @(2004); StartTime = $Since }
    )) {
        $failures = @()
        $records += @(Get-WinEvent -FilterHashtable $query -ErrorAction SilentlyContinue -ErrorVariable failures | Where-Object {
            $query.LogName -eq 'System' -or $_.Message -match 'osu!\.exe|SimpleManiaEditor|LumaChart'
        } | ForEach-Object {
            [pscustomobject]@{ Time = $_.TimeCreated.ToString('o'); Log = $_.LogName; Id = $_.Id; Record = $_.RecordId; Message = $_.Message }
        })
        foreach ($failure in $failures) {
            if ($failure.FullyQualifiedErrorId -notmatch '^NoMatchingEventsFound') { $issues.Add($failure.ToString()) }
        }
    }
    [pscustomobject]@{ Records = $records; ReadErrors = @($issues.ToArray()) }
}

$started = Get-Date
Save-Json 'events-before.json' (Get-Events ($started.AddDays(-90)))
$wer = @('HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting', 'HKCU:\SOFTWARE\Microsoft\Windows\Windows Error Reporting') | ForEach-Object {
    $value = Get-ItemProperty -LiteralPath $_ -ErrorAction SilentlyContinue
    [pscustomobject]@{ Key = $_; Disabled = $value.Disabled; DontShowUI = $value.DontShowUI }
}
Save-Json 'metadata.json' ([pscustomobject]@{
    Started = $started.ToString('o'); DurationSeconds = $DurationSeconds; IntervalSeconds = $IntervalSeconds
    WindowsErrorReporting = @($wer)
    Limitations = @('Samples may miss short-lived processes and allocation spikes.', 'No debugger or crash dump is attached; no causal attribution of file changes.', 'Current memory values cannot establish memory usage at an earlier crash.')
})
Save-Json 'update-before.json' @(Get-LogSnapshot)
$writer = [IO.StreamWriter]::new((Join-Path $destination 'samples.jsonl'), $false, $encoding)
$writer.AutoFlush = $true
$watch = [Diagnostics.Stopwatch]::StartNew()
Write-Host ('Local diagnostic report: ' + $destination)
Write-Host 'Read-only observation. Reproduce the issue normally; Ctrl+C stops sampling.'
try {
    do {
        $processes = @(Get-Process -Name 'osu!', 'osu', 'SimpleManiaEditor', 'SimpleManiaEditor.OsuHost', 'LumaChart*' -ErrorAction SilentlyContinue | ForEach-Object {
            $process = $_
            try {
                $path = $process.Path
                if ($process.ProcessName -eq 'osu!' -and $path) { $roots[[IO.Path]::GetDirectoryName($path)] = $true }
                [pscustomobject]@{
                    Id = $process.Id; Name = $process.ProcessName; Path = $path
                    PrivateBytes = $process.PrivateMemorySize64; VirtualBytes = $process.VirtualMemorySize64
                    WorkingSetBytes = $process.WorkingSet64; PeakVirtualBytes = $process.PeakVirtualMemorySize64
                    Handles = $process.HandleCount; Threads = $process.Threads.Count
                }
            } catch { [pscustomobject]@{ Id = $process.Id; Name = $process.ProcessName; ReadError = $_.Exception.Message } }
            finally { $process.Dispose() }
        })
        $memory = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory | Select-Object AvailableMBytes, CommittedBytes, CommitLimit, PercentCommittedBytesInUse
        $sample = [pscustomobject]@{ Time = (Get-Date).ToString('o'); Processes = $processes; Memory = $memory }
        $writer.WriteLine((ConvertTo-Json -InputObject $sample -Depth 6 -Compress))
        if ($watch.Elapsed.TotalSeconds -ge $DurationSeconds) { break }
        Start-Sleep -Milliseconds ([int][Math]::Min($IntervalSeconds * 1000, [Math]::Max(1, ($DurationSeconds - $watch.Elapsed.TotalSeconds) * 1000)))
    } while ($true)
} finally {
    $writer.Dispose()
    Save-Json 'update-after.json' @(Get-LogSnapshot)
    Save-Json 'events-after.json' (Get-Events $started)
    Save-Json 'finished.json' ([pscustomobject]@{ Time = (Get-Date).ToString('o'); ElapsedSeconds = $watch.Elapsed.TotalSeconds })
    Write-Host ('Saved locally: ' + $destination)
}
