param(
    [Parameter(Mandatory = $true)][string[]]$TestArgs,
    [string]$Name = 'agent',
    [switch]$NoBuild,
    [int]$LockTimeoutSeconds = 1800,
    [string]$Sdk = 'D:\CodexBuildTools\SimpleManiaEditor\sdk\dotnet.exe',
    [string]$Runtime = 'C:\Program Files\dotnet\dotnet.exe'
)
# Serialized build + test runner for concurrent agents.
# All agents share one checkout and one set of bin/obj folders, so builds and
# test runs must not overlap. This wrapper holds a cross-process lock for the
# whole build+run window; tests start only after the build finished.
$ErrorActionPreference = 'Stop'
$checkout = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $checkout 'artifacts/agent-verify.lock'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $lockPath) | Out-Null
$env:NUGET_PACKAGES = 'D:\CodexBuildTools\SimpleManiaEditor\nuget'
$env:DOTNET_CLI_HOME = 'D:\CodexBuildTools\SimpleManiaEditor\cli'

$deadline = [DateTime]::UtcNow.AddSeconds($LockTimeoutSeconds)
$lock = $null
$waited = $false
while ($true) {
    try { $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None'); break }
    catch [IO.IOException] {
        if (-not $waited) { Write-Output 'WAIT  another agent is building/testing; waiting for the shared lock…'; $waited = $true }
        if ([DateTime]::UtcNow -gt $deadline) { throw "Timed out after $LockTimeoutSeconds s waiting for $lockPath" }
        Start-Sleep -Milliseconds 750
    }
}
try {
    Set-Location $checkout
    $logDirectory = Join-Path $checkout 'artifacts'
    $logPath = Join-Path $logDirectory ('agent-' + $Name + '-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '.txt')
    $transcript = [Collections.Generic.List[string]]::new()
    function Emit([string]$text) { Write-Output $text; $transcript.Add($text) }
    try {
        if (-not $NoBuild) {
            Emit '=== build UiSmoke (Release, warnaserror) ==='
            $buildOutput = & $Sdk build tests/SimpleManiaEditor.UiSmoke/SimpleManiaEditor.UiSmoke.csproj -c Release -warnaserror 2>&1
            $buildExit = $LASTEXITCODE
            $buildOutput | Select-Object -Last 25 | ForEach-Object { Emit ([string]$_) }
            if ($buildExit -ne 0) { throw 'UiSmoke build failed with exit code ' + $buildExit }
        }
        $dll = Join-Path $checkout 'tests/SimpleManiaEditor.UiSmoke/bin/Release/net8.0-windows/SimpleManiaEditor.UiSmoke.dll'
        if (-not (Test-Path -LiteralPath $dll)) { throw "Missing test assembly: $dll" }
        Emit ('=== run ' + ($TestArgs -join ' ') + ' ===')
        & $Runtime $dll @TestArgs 2>&1 | ForEach-Object { Emit ([string]$_) }
        $exit = $LASTEXITCODE
        $transcript | Set-Content -LiteralPath $logPath -Encoding utf8
        Emit ('EXIT ' + $exit + '  (log: ' + $logPath + ')')
        exit $exit
    }
    catch {
        $transcript | Set-Content -LiteralPath $logPath -Encoding utf8
        Write-Error $_
        exit 1
    }
}
finally { if ($lock) { $lock.Dispose() } }
