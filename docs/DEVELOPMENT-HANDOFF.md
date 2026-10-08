# 开发交接摘要

## 基线

- 工作目录：`E:\Files\Developing\SimpleManiaEditor`，Windows / PowerShell。
- 用户负责功能和真实 osu!/音频设备验收；代码、测试和便携包由助手完成。不要反复请求已授权的开发确认。
- 用户允许调整 Python/Web 技术栈，优先复用成熟代码。当前为 MIT 的 Luma 客户端改造，C#/.NET + Avalonia。
- 最新交付 0.1.7；0.1.6 基线提交 `bb96b0b`。源码位于 `src/`。
- 原生 `.osu` 源资料保真、外部修改检测、保存前备份、恢复工作副本已实现；完整 command journal / 版本树仍未完成。
- osu 键鼠方案默认；网格内部分母是每四分音符拍的切分数，osu 1/4 = general 1/16 = 120 BPM 下 125 ms。不要再把内部网格盲目乘四。
- 编辑播放常规 seek 复用设备；0.25x 保音高采用后台准备。性能数字仅为模拟输出引擎时间，不能称为硬件端到端延迟。
- 附加草稿/参考/hitsound/密度/实验负荷轨已交付。轨道资料为独立明文 JSON/.mc，不进入主谱 `.osu`。官方/Sunny 难度未接入。

## 0.1.5 已完成的请求

1. 回答上下文压缩入口：官方桌面端文档有 `/compact`、`/status`，命令可用性依环境；自动阈值配置 `model_auto_compact_token_limit`。不擅自改 Codex 配置，不声称已执行压缩。
2. Songs 原件疑似引起 osu 警告/修复；等待用户可选提供提示原文，独立推进安全创作副本和写入保护。
3. 倍速默认保音高。
4. 修快捷编辑图标/边框错位。
5. 修同步轨道和波形/频谱不同流速，特别是残留 480 缩放上限。
6. LN 尾只在精确尾部区域触发调整。
7. 上方命令和倍速/网格合成一栏。
8. 所有轨道统一底板地位；density/diff/音频面板也在底板内，支持拖动顺序；底部横向滚动条控制整个底板。新增轨不自动改宽或跳滚动位置。
9. 主轨/草稿/对照复用相同轨道容器与绘制/放置逻辑；波形频谱不再限制在主轨范围内移动。
10. osu 点击两个格线之间始终选时间上前一格，和 note 厚度无关；略减默认厚度。
11. 仅底板空白处右键显示添加菜单；轨道内部不显示。
12. 菜单显示后第一次左键仅关闭，不放键。

## 0.1.6 修正

- 用户更改吸附要求：osu 放置和 LN 拖动取鼠标最近格线，替代 0.1.5 前一格；鼠标时间不减去按键厚度。
- osu 的绘制中心为实际时间 Y 减去半个按键高度，确保底边表示时间；使用像素偏移，避免不同 BPM 的参考轨投影放大偏移。
- 精确面尾拖动命中以实际时间底边为入口；空白右键取消 Note/Timing 选择，底板空白也取消各轨选择。
- 底板未处理滚轮交给主时间轴；横向条 Visibility=Visible、AllowAutoHide=false、高 24 DIP、柄 12 DIP。
- ZCode 窗口可发现，但两次 Computer Use 捕获返回 FrameArrived/window capture timed out；本轮未提交外部模型任务。

## 已定位的原因

0.1.7：TimelinePlacementGeometry 的 Tap 永远返回单键，Hold 零长度也返回单键。osu 绘制工具左键替换（整块尾部优先调整）、中键移动，其他工具中键平移。替换对象按手势仅隐藏预览，release 在 WindowsProEditorCommand 的一次 Mutate 中删旧添新，包含 changeSerial/只读/锁校验。hover 按键按原鼠标位置画、底边中心对尖部，不写文档。默认 NoteHeight=28，layout revision 4 迁移原默认 32。ChartValidator 同轨尾头接触（含 tap）也报重叠。

- `MainWindow.Tracks.cs` 新增后 `UpdateMainTrackWidth` 改主轨宽度，且 `AddEditorTrackAsync` 主动滚到底。
- `AudioVisualizationSideSurface.SetViewport` 仍将缩放夹到 24～480。
- 同步参考轨只按歌曲时间定位光标但仍按本轨 beat 绘制；不同 BPM 时同样 px/beat 不等于同样 px/歌曲毫秒。
- 草稿的 ConfigureView 硬编码了 42 / 37 / 26 等，与主轨不同。
- `QuickPaletteButton` 的内容固定 84 宽，合并到较窄双列工具栏后内容溢出。
- `TimelineHoldTailResize` 使用高达 40 px 纵向容错和左右额外 12 px。
- `TimelinePlacementGeometry` 基于 note 中心/anchor 偏移后四舍五入，不是时间前一格。
- 添加菜单被绑定到各 TimelineSurface 空白事件，而非专属底板空白；菜单关闭点击未吞掉。

## 构建/测试

使用 SDK 10.0.401（Avalonia 生成器不支持 SDK 8 的编译器），运行目标 net8.0-windows。

当前隔离 SDK：

```powershell
$env:NUGET_PACKAGES = 'C:\Users\Administrator\AppData\Local\Temp\sme-dotnet-audit-bc77d02e8ce44c438d3a7de2e0060cbd\nuget'
$env:DOTNET_CLI_HOME = 'C:\Users\Administrator\AppData\Local\Temp\sme-dotnet-audit-bc77d02e8ce44c438d3a7de2e0060cbd\cli'
./scripts/Test.ps1 -Dotnet 'C:\Users\Administrator\AppData\Local\Temp\sme-dotnet-audit-bc77d02e8ce44c438d3a7de2e0060cbd\sdk10\dotnet.exe' -RuntimeDotnet 'C:\Program Files\dotnet\dotnet.exe'
```

- 49 项核心 specs；自有 `tests/SimpleManiaEditor.UiSmoke` 测试实际 Avalonia 控件、真实解码/DSP、模拟设备和 Skia 截图。
- 不运行依赖作者私有 D:\ 数据的全量上游 UI specs。
- 发布 `scripts/Publish.ps1 -Dotnet <SDK> -Zip`，自包含 Windows x64 包；发布前提交以便内嵌版本 SHA 正确。
- 最终 ZIP 必须解压到新的 artifacts 子目录，隐藏运行 `--smoke-test`，检查真实 ExitCode、版本 SHA、演示、说明、许可；PowerShell 直接调用 GUI exe 的 LASTEXITCODE 不可信。
- 构建物在忽略的 `dist/`、`artifacts/`；不要删除旧包或用户数据。
- 不把 headless 测试当作真实音频设备或官方 osu 比较验收。

需求和边界见 `BACKLOG.md`、`WORKBENCH-TRACKS.md`、历次 RELEASE 文档。当前代码状态以 Git 为准；0.1.5 请求和自动回归已完成。

## 0.1.5 完成状态

已实现上述请求，新增 EditorTrackPanel / MainWindow.TrackBoard.cs 统一底板，Songs 导入副本与写保护、时间投影、格线前置放置、精确尾部、默认保音高和单一顶部栏。核心49/49通过；新增 Songs、精确放置、菜单关闭吞点击、实际拖动与顺序恢复、不同 BPM 高缩放、波形高缩放等界面回归通过。用户已说明警告原文忘记，后续再次触发再补，勿声称复现官方报错。
