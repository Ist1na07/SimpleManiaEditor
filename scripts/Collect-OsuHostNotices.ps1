param([Parameter(Mandatory=$true)][string]$Destination, [string]$ProjectAssets = 'src/SimpleManiaEditor.OsuHost/obj/project.assets.json', [string]$Caption = 'Official gameplay host dependencies')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$assets = Get-Content -LiteralPath (Join-Path $taskRoot $ProjectAssets) -Raw | ConvertFrom-Json -AsHashtable
$packageRoots = @($assets.packageFolders.Keys)
$noticeRoot = Join-Path $Destination 'licenses/packages'
New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# ' + $Caption)
$lines.Add('')
$lines.Add('Pinned packages are unmodified. The notices below do not relicense their content; original copyright, licence files and terms remain in effect.')
foreach ($name in ($assets.libraries.Keys | Sort-Object)) {
    $library = $assets.libraries[$name]
    if ($library.type -ne 'package') { continue }
    $package = $null
    foreach ($root in $packageRoots) {
        $candidate = Join-Path $root $library.path
        if (Test-Path -LiteralPath $candidate) { $package = $candidate; break }
    }
    if ($null -eq $package) { throw ('Package provenance missing: ' + $name) }
    $spec = Get-ChildItem -LiteralPath $package -Filter '*.nuspec' | Select-Object -First 1
    [xml]$xml = Get-Content -LiteralPath $spec.FullName -Raw
    $meta = $xml.package.metadata
    $lines.Add('')
    $lines.Add('## ' + $name)
    $lines.Add('Authors: ' + [string]$meta.authors)
    if ($meta.copyright) { $lines.Add('Copyright: ' + [string]$meta.copyright) }
    if ($meta.projectUrl) { $lines.Add('Source/project: ' + [string]$meta.projectUrl) }
    if ($meta.license) {
        $lines.Add('Licence (' + [string]$meta.license.type + '): ' + [string]$meta.license.InnerText)
        if ($meta.license.type -eq 'file') {
            $source = Join-Path $package ([string]$meta.license.InnerText)
            if (Test-Path -LiteralPath $source) {
                $target = Join-Path $noticeRoot (($name -replace '[^A-Za-z0-9._-]', '-') + '-' + [IO.Path]::GetFileName($source))
                Copy-Item -LiteralPath $source -Destination $target
            }
        }
    }
    if ($meta.licenseUrl) { $lines.Add('Licence URL: ' + [string]$meta.licenseUrl) }
    # Some older NuGet packages include a licence without a nuspec file field.
    foreach ($file in (Get-ChildItem -LiteralPath $package -File | Where-Object {$_.Name -match '(?i)^(licen[cs]e|notice|copying)'})) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $noticeRoot (($name -replace '[^A-Za-z0-9._-]', '-') + '-' + $file.Name)) -Force
    }
}
[IO.File]::WriteAllLines((Join-Path $Destination 'DEPENDENCIES.md'), $lines, [Text.UTF8Encoding]::new($false))
