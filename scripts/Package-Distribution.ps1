param([string]$Dotnet = 'dotnet', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
[xml]$definition = Get-Content -LiteralPath (Join-Path $taskRoot 'src/LumaChart.Avalonia/LumaChart.Avalonia.csproj') -Raw
$version = [string](($definition.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version)
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $taskRoot "dist/SimpleManiaEditor-$version-Distribution"
}
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Choose a new, empty distribution path. Existing folders may contain user work.' }
$zipPath = $destination + '-win-x64.zip'
if (Test-Path -LiteralPath $zipPath) { throw 'Distribution archive already exists; choose a new output path.' }
$stage = Join-Path $taskRoot ('artifacts/distribution-build-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'publish'
$app = Join-Path $destination 'app'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
Push-Location $taskRoot
try {
    # Never publish into, or copy from, a folder that has been run by a mapper.
    & $Dotnet publish 'src/LumaChart.Avalonia/LumaChart.Avalonia.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Editor publish failed.' }
    & (Join-Path $PSScriptRoot 'Collect-OsuHostNotices.ps1') -Destination $publish -ProjectAssets 'src/LumaChart.Avalonia/obj/project.assets.json' -Caption 'Editor dependencies'
    & $Dotnet publish 'src/SimpleManiaEditor.OsuHost/SimpleManiaEditor.OsuHost.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $publish 'osu-host')
    if ($LASTEXITCODE -ne 0) { throw 'Official host publish failed.' }
    & (Join-Path $PSScriptRoot 'Collect-OsuHostNotices.ps1') -Destination (Join-Path $publish 'osu-host')
    New-Item -ItemType Directory -Path $app -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($publish, $file.FullName)
        if ($file.Extension -eq '.pdb' -or $relative -eq 'README.md') { continue }
        $target = Join-Path $app $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    # These are public, generic feature descriptions. Developer handoff,
    # performance reports, demos and test fixtures are deliberately not copied.
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/FEATURES.md') -Destination (Join-Path $destination 'FEATURES.md')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/LIBRARY-VERSIONS.md') -Destination (Join-Path $destination 'LibraryGuide.md')
    $publicReleaseNotes = Join-Path $taskRoot "docs/RELEASE-$version.md"
    if (Test-Path -LiteralPath $publicReleaseNotes) { Copy-Item -LiteralPath $publicReleaseNotes -Destination (Join-Path $destination 'ReleaseNotes.md') }
    [IO.File]::WriteAllText((Join-Path $destination 'Start-Editor.cmd'), @'
@echo off
setlocal
if not exist "%~dp0app\SimpleManiaEditor.exe" (
  echo Please extract the entire ZIP before starting the editor.
  pause
  exit /b 1
)
set "LUMACHART_AVALONIA_LOCAL_DATA_ROOT=%LOCALAPPDATA%\SimpleManiaEditor\Distribution"
set "LUMACHART_AVALONIA_EDITING_ROOT=%LUMACHART_AVALONIA_LOCAL_DATA_ROOT%\Editing Cache"
set "LUMACHART_AVALONIA_SINGLE_INSTANCE_SCOPE=simple-mania-editor-distribution"
set "LUMACHART_AVALONIA_WORKSPACE_ROOT="
set "LUMACHART_AVALONIA_ALLOW_MULTIPLE_INSTANCES="
if "%~1"=="--smoke-test" (
  "%~dp0app\SimpleManiaEditor.exe" --smoke-test
  exit /b
)
start "" "%~dp0app\SimpleManiaEditor.exe" %*
'@.Replace("`n", "`r`n"), [Text.ASCIIEncoding]::new())
    [IO.File]::WriteAllText((Join-Path $destination 'README.md'), @"
# SimpleManiaEditor $version · Windows x64 分发包

1. 将 ZIP **完整解压**到一个文件夹。
2. 双击根目录 **Start-Editor.cmd** 启动。

包内自带编辑器和官方 osu!mania 试玩所需的 .NET 运行时；无需安装 Python、Node.js、.NET SDK 或另一个 osu! 客户端。请保留整个 app 文件夹，不要只取出其中的 exe。

推荐 Windows 10/11 64 位。首次官方试玩会初始化资源，可能需要等待。

快捷操作：Space 播放/暂停，F5 从当前位置试玩，试玩 Shift 暂停、Esc 返回；Ctrl+滚轮调网格、Shift+滚轮调倍速、Alt+滚轮缩放。更多功能见 FEATURES.md。设置 → 外观可自动/手动调整 UI 大小；点击左上角品牌进入曲库，添加只读 Songs 目录，按曲名/作者/难度搜索。曲库难度行「整理 / 迁移」可设置标签备注或搬迁整套谱面，顶部「版本历史」可留稿、比较和恢复；流程见 LibraryGuide.md。

分发包不含制作者的谱面、音乐、皮肤、个人设置、工作缓存或测试素材。随官方程序提供的默认界面/皮肤素材属于程序资源。

启动入口使用独立的本机配置目录：%LOCALAPPDATA%\SimpleManiaEditor\Distribution，初始谱面目录为包体旁的 Maps；可在曲库中更改创作目录。已保存的目录选择会保留。设置、备份、缓存和自行导入的皮肤在运行后生成，不在此原始分发包内，也不继承开发体验版 Preview 的编辑会话。

升级时完整解压新包，再使用同一启动入口；本机配置和创作文件保留。转发时使用提供的原始 ZIP，避免把运行后的个人资料重新打包。

第三方许可与版权说明保留在 app/LICENSE、app/THIRD-PARTY-NOTICES.md、app/licenses 及 app/osu-host/licenses 中；打包不改变各组件的原有许可条件。
"@, [Text.UTF8Encoding]::new($false))
    # Validate before running or archiving: neither source directory nor an
    # already-used installation is a permissible distribution input.
    $files = @(Get-ChildItem -LiteralPath $destination -File -Recurse)
    $forbiddenExtensions = @('.osu', '.osz', '.mc', '.mcz', '.osk', '.mp3', '.wav', '.ogg', '.flac', '.db', '.sqlite3', '.sqlite3-wal', '.sqlite3-shm', '.realm', '.cfg', '.log', '.pdb')
    $records = @()
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($destination, $file.FullName).Replace('\', '/')
        if ($file.Extension.ToLowerInvariant() -in $forbiddenExtensions -or
            $relative -match '(?i)(^|/)(data|cache|backups?|logs?|skins?|songs|editing cache|lumaChart workspace|examples|performance|versions|transfers|\.git)(/|$)') {
            throw "Personal/test data forbidden in distribution: $relative"
        }
        if ($file.Extension -eq '.json' -and $file.Name -notmatch '(?i)\.(deps|runtimeconfig)\.json$') {
            throw "Unexpected settings/JSON file: $relative"
        }
        $records += [pscustomobject]@{ Path = $relative; Bytes = $file.Length; Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $manifest = [pscustomobject]@{ Product = 'SimpleManiaEditor'; Version = $version; Runtime = 'Self-contained win-x64'; EntryPoint = 'Start-Editor.cmd'; UserDataIncluded = $false; Files = $records }
    [IO.File]::WriteAllText((Join-Path $destination 'PACKAGE-MANIFEST.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $stdout = Join-Path $stage 'smoke.stdout.txt'; $stderr = Join-Path $stage 'smoke.stderr.txt'
    $smoke = Start-Process -FilePath (Join-Path $app 'SimpleManiaEditor.exe') -ArgumentList '--smoke-test' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Get-Content -LiteralPath $stdout
    Get-Content -LiteralPath $stderr
    if ($smoke.ExitCode -ne 0) { throw 'Distribution runtime verification failed.' }
    Compress-Archive -LiteralPath $destination -DestinationPath $zipPath
    Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    Write-Output "PASS clean distribution: $($files.Count) files, no personal maps/skins/settings; $zipPath"
}
finally { Pop-Location }
