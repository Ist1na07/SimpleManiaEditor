param([string[]]$Archive = @(), [switch]$ExplicitReleaseRequest)
$ErrorActionPreference = 'Stop'
if (!$ExplicitReleaseRequest) { throw 'A GitHub release requires an explicit human request. Use Build-Local.ps1 for normal iteration; supply -ExplicitReleaseRequest only after that request.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
[xml]$definition = Get-Content -LiteralPath (Join-Path $taskRoot 'src/LumaChart.Avalonia/LumaChart.Avalonia.csproj') -Raw
$version = [string](($definition.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version)
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
if ($Archive.Count -eq 0) {
    $Archive = @(
        (Join-Path $taskRoot "dist/SimpleManiaEditor-$version-Full-win-x64.zip"),
        (Join-Path $taskRoot "dist/SimpleManiaEditor-$version-Portable-win-x64.zip")
    ) | Where-Object { Test-Path -LiteralPath $_ }
}
$Archive = @($Archive | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
if ($Archive.Count -eq 0) { throw 'No distribution archive found to publish.' }
$repo = 'Ist1na07/SimpleManiaEditor'
$tag = 'v' + $version
$notes = Get-Content -LiteralPath (Join-Path $taskRoot "docs/RELEASE-$version.md") -Raw
$assets = @()
foreach ($path in $Archive) {
    $assets += [pscustomobject]@{
        Path   = $path
        Name   = [IO.Path]::GetFileName($path)
        Hash   = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        Length = (Get-Item -LiteralPath $path).Length
    }
}
Push-Location $taskRoot
try {
    $commit = (& git rev-parse HEAD).Trim()
    $remote = (& git ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or !$remote.StartsWith($commit)) { throw 'Push and verify main before publishing.' }
    if ((& git remote get-url origin).Trim() -ne "https://github.com/$repo.git") { throw 'Unexpected origin repository.' }
    # Use the existing Git credential helper, never persist or print its token.
    $credential = "protocol=https`nhost=github.com`nusername=Ist1na07`n`n" | & git credential fill
    if ($LASTEXITCODE -ne 0) { throw 'GitHub authentication unavailable.' }
    $fields = @{}
    foreach ($line in $credential) { $pair = $line -split '=', 2; if ($pair.Count -eq 2) { $fields[$pair[0]] = $pair[1] } }
    if (!$fields['password']) { throw 'Git credential helper returned no token.' }
    $headers = @{ Authorization = 'Bearer ' + $fields['password']; Accept = 'application/vnd.github+json'; 'User-Agent' = 'SimpleManiaEditor-release'; 'X-GitHub-Api-Version' = '2026-03-10' }
    $base = "https://api.github.com/repos/$repo"
    $profile = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $headers
    if ($profile.login -ne 'Ist1na07') { throw 'Authenticated account is not the repository owner.' }
    # Never overwrite a published release. A draft allows a failed upload to resume.
    $release = $null
    $releases = Invoke-RestMethod -Uri "$base/releases?per_page=100" -Headers $headers
    $release = $releases | Where-Object tag_name -eq $tag | Select-Object -First 1
    if ($release -and !$release.draft) { throw 'This version is already published.' }
    $hashLines = ($assets | ForEach-Object { '- ' + '``' + $_.Name + '``' + ' (' + [math]::Round($_.Length / 1MB, 1) + ' MiB) SHA256: ``' + $_.Hash + '``' }) -join "`n"
    $body = $notes + "`n`n" + $hashLines
    if (!$release) {
        $payload = @{ tag_name = $tag; target_commitish = $commit; name = "SimpleManiaEditor $version"; body = $body; draft = $true; prerelease = $false } | ConvertTo-Json
        $release = Invoke-RestMethod -Uri "$base/releases" -Headers $headers -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload))
    }
    if ($release.target_commitish -ne $commit) { throw 'Draft release belongs to a different commit.' }
    $upload = $release.upload_url -replace '\{.*$', ''
    if ($upload -notlike "https://uploads.github.com/repos/$repo/releases/*/assets") { throw 'Unexpected asset upload host.' }
    foreach ($item in $assets) {
        $asset = $release.assets | Where-Object name -eq $item.Name | Select-Object -First 1
        if (!$asset) {
            $asset = Invoke-RestMethod -Uri ($upload + '?name=' + [Uri]::EscapeDataString($item.Name)) -Headers $headers -Method Post -ContentType 'application/zip' -InFile $item.Path -TimeoutSec 1800
        }
        if ($asset.state -ne 'uploaded' -or $asset.size -ne $item.Length -or $asset.digest -ne ('sha256:' + $item.Hash)) { throw "Uploaded asset $($item.Name) checksum or size does not match. Draft retained." }
        Write-Output "Asset verified: $($item.Name) ($($item.Length) bytes), SHA256 $($item.Hash)"
    }
    $payload = @{ draft = $false; make_latest = 'true'; body = $body } | ConvertTo-Json
    $published = Invoke-RestMethod -Uri "$base/releases/$($release.id)" -Headers $headers -Method Patch -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload))
    Write-Output "Published: $($published.html_url)"
}
finally { $headers = $null; $fields = $null; $credential = $null; Pop-Location }
