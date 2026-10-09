# 开发交接摘要

## 基线

0.1.19：`CenterSymmetryService` 提供精确有理数反射、局部有序列、LN 端点交换、目标索引和冲突预检。WindowsProEditorCommand 的可选 symmetry 参数将主键/被替换键/伴随键放在一份 Mutate 历史；不建立永久键间链接。TimelineSurface 新 partial 绘制范围与预览；左栏弹层、F6 catalog/router、每图/轨 sidecar。F6 新默认遇旧自定义冲突时留空。工具设置在 LocalDataRoot/drawing-tools，只读引用禁用，CWT 避免关闭会话后滞留文档。54核心、完整UI与最终工作台回归通过，宿主只版本号变化。默认关闭，性能按0.1.18限制描述，不声称180稳定达标。

0.1.18：默认 osu 导入从 Preserve 切换 Calibrate；补齐 5/7/9/10/64，红线/对象百万分拍，保留原始源封套。`_sme_osu_grid_calibration` 仅存编辑器缓存，Timing 重锚沿用校准；未编辑保存仍原文。`NoteGridSnapService` 单次 Mutate，负拍/LN/保护/只读/undo，短 LN 不塌；UI 设置内选中/全部入口。窗口 Tunnel 空白点击释放文本焦点。默认 WGL→ANGLE→software / RedirectionSurface，兼容选项持久化在 editor-presentation.json，重启生效，启动环境覆盖 SIMPLEMANIA_RENDERER。高采样线程最多240Hz单pending UI派发，暂停/试玩/关闭停止；timeBeginPeriod/endPeriod成对。结束范围按tab/document/serial缓存，ruler文本和globaldifficulty屏幕行缓存；同步投影精度提高。53核心及完整UI通过，真实窗口测量见performance/EDITOR-RENDER-0.1.18.md；稳定180/low180未达标，不得称已解决全部卡顿。官方宿主仅版本号变化。

- 工作目录：`E:\Files\Developing\SimpleManiaEditor`，Windows / PowerShell。
- 用户负责功能和真实 osu!/音频设备验收；代码、测试和便携包由助手完成。不要反复请求已授权的开发确认。
- 用户允许调整 Python/Web 技术栈，优先复用成熟代码。当前为 MIT 的 Luma 客户端改造，C#/.NET + Avalonia。
- 最新交付 0.1.19；0.1.11 基线提交 `0b536b9`。源码位于 `src/`。
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

## 0.1.12 常驻试玩与资源缓存

右侧 Settings 首部 BuildOfficialPlaytestSettings 显式按钮替代右键菜单，并显示预热状态。OfficialPlaytestProcess 是编辑器所有的单实例游戏监督器，Opened 异步预热，Closed 关闭；JSON ResidentMessage 通过继承管道通信。宿主 EOF/shutdown 退出，异常退出下一次重建，AllowSetForegroundWindow 仅授权自己的子进程。后台隐藏/静音/停音乐/10 Hz，实际试玩恢复帧率和音量。PlaytestGame 用轻量 OsuScreen 加载官方 PracticePlayer，保留重试与高性能会话，移除 PlayerLoader 私有固定 650+500 ms 转场。TryTransferTrack 复用音轨。OfficialPlaytestPackageCache 监控源/工作资源目录，复用压缩资源并仅重写 .osu；官方导出可选 CaptureNormalizedDocument 回调保留路径规范化映射，测试覆盖冲突、新编辑、切片、资源更新和原件不写。Protected 根和监控异常禁用缓存。原生 GPU 常驻测试 scripts/Test-ResidentPlaytest.ps1；实际监督器/缓存/异常重建端到端入口 UiSmoke --resident-process-test <self-contained host exe>。首次预热/素材不能声称全部秒开；重复热启动实测数百毫秒，报告位置见 RELEASE-0.1.12。

## 0.1.11 官方宿主

用户要求右下角可拖动滑条卡片，质疑自制皮肤渲染并授权优先官方客户端。MainWindow 唯一顶部行 + BuildQuickValuesPalette 三滑条竖排，旧卡片拖动/保存/夹取复用。OfficialPlaytestLauncher 用独立测试副本 OsuFullPackageExportService 生成 .osz，将音频/资源/起点/倍率和迁移设定写 PlaytestRequest（独立 net8 DTO）。SimpleManiaEditor.OsuHost 为 net10，官方 NuGet 2026.916.0 + Framework 2026.914.0，不与 Avalonia 同进程；OsuGame/Player/官方 skin 与 Ctrl+O 设置真实运行。首次迁移，显式 cfg revision 一次性应用，随后不覆盖官方配置。源皮肤四份包括 Wilben 已实际导入，截图仅在 artifacts，不分发用户图。RetainedImportTask 禁止官方导入后删除归档。PracticePlayer 无失败/上传，时钟当前位置、Tab 自动演奏和 Ctrl+P，回放截取保留空帧避免第一下漏判。旧窗口仍留在源码用于历史测试，实际入口没有降级。新 Test-OfficialPlaytest.ps1 使用真实 GPU、自动退出、隔离数据，可对便携 host exe 验证。官方资源/字体/native 许可与窄范围 NuGet advisory 记录见 notices/UPSTREAM；公开发行前重新核对。

## 0.1.10 皮肤工作流

调整控件并入唯一 Editor.Toolbar.Fixed，移除独立调整 Expander，保留一个 Editor.Toolbar.Playtest。新增 OsuSkinLibrary 明文持久化目录；SkinSelector/Preview 与皮肤、游戏、按键三页，移除 Note 外观控件与旧外观值对渲染的影响。SelectSkinAsync 先持久化选择再异步加载；generation 只允许最后选择应用；图像不依赖声音解码成功；settingsUiReady 在初始化完成即打开，不丢音频等待期间设置。父窗口全屏不覆盖已保存的试玩窗口选择。OsuIniDocument 支持同一 Mania 多 Keys、扩展名动画与旧 K.ini；按官方顶部锚定 LN 尾。SkinWorkflowSpecs 测关闭加载、反复重进、快速切换、损坏声音与失效目录；固定工具栏与 LN 像素检查补齐。没有收到异常皮肤实际路径，不声称已对其验收。

## 0.1.9 试玩与皮肤

用户允许改为 lazer 界面/落速。新增 OsuIniDocument、OsuManiaSkin（后台纹理、HD/动画/颜色/预算/归档）、OsuPlaytestImport（cfg 键位/落速/皮肤/全局键）；PlaytestSurface.Skin 与 PlaytestWindow.Presentation 分离游戏渲染和 UI/导入。默认全屏，F1 设置浮层，Esc 暂停，F11 全屏，F3/F4 1–40；官方公式 11485/speed，视觉 402/480 标定及倍率补偿。stable 判定保留。鼠标按住时绘制预览保留。测试含皮肤/色光/声音/配置隐私/.osk/100k/浮层不改画面大小。范围与尚未完成的完整 HP/Mods/谱面 HS/特殊皮肤见 PLAYTEST.md；不要声称嵌入整个官方游戏。

## 已定位的原因

0.1.8：用户明确要求 playtest 对齐 osu! stable。PlaytestRuleset.OsuStable 为 Windows 设置默认，LegacyCustom 保留。StableManiaRules 轻量移植 ppy/osu 固定提交 Classic/native/ScoreV1 窗口，官方 wiki 的单次 LN 头尾合并（非 lazer 1.5x 松手/双判定）、非对称晚侧截止、准确率及无 Mods base/bonus 分数。PlaytestSession.Stable.cs 持续 hold/regrab/timeout 和单次计分；ManiaScrollMap 连续毫秒积分 BPM/SV、按时长主 BPM 归一化。绘制头尾底边/按住 head/clipping/7K 列色，误差条和判定反馈。Render 与输入读取实时音频时钟，偏移按实际毫秒乘倍率，frame miss 使用相同手动时钟。真实 Avalonia 7K Space、重复、松手和窗口/偏移、profile/persistence、100k 可见区回归；核心 50/50。

上游参考 `third_party/osu/UPSTREAM.md`，源码 `ManiaHitWindows.cs`，完整 MIT 许可 `src/licenses/osu-MIT.txt` 随包复制。不是嵌入完整游戏；HP/Mods/replay/完整 combo tick/外部 skin/native chart hitsound bank 待补，像素流速不是 stable 1–40 值，硬件延迟不能用 headless 结果代替。

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

- 50 项核心 specs；自有 `tests/SimpleManiaEditor.UiSmoke` 测试实际 Avalonia 控件、真实解码/DSP、模拟设备和 Skia 截图。
- 不运行依赖作者私有 D:\ 数据的全量上游 UI specs。
- 发布 `scripts/Publish.ps1 -Dotnet <SDK> -Zip`，自包含 Windows x64 包；发布前提交以便内嵌版本 SHA 正确。
- 最终 ZIP 必须解压到新的 artifacts 子目录，隐藏运行 `--smoke-test`，检查真实 ExitCode、版本 SHA、演示、说明、许可；PowerShell 直接调用 GUI exe 的 LASTEXITCODE 不可信。
- 构建物在忽略的 `dist/`、`artifacts/`；不要删除旧包或用户数据。
- 不把 headless 测试当作真实音频设备或官方 osu 比较验收。

需求和边界见 `BACKLOG.md`、`WORKBENCH-TRACKS.md`、历次 RELEASE 文档。当前代码状态以 Git 为准；0.1.5 请求和自动回归已完成。

## 0.1.5 完成状态

已实现上述请求，新增 EditorTrackPanel / MainWindow.TrackBoard.cs 统一底板，Songs 导入副本与写保护、时间投影、格线前置放置、精确尾部、默认保音高和单一顶部栏。核心49/49通过；新增 Songs、精确放置、菜单关闭吞点击、实际拖动与顺序恢复、不同 BPM 高缩放、波形高缩放等界面回归通过。用户已说明警告原文忘记，后续再次触发再补，勿声称复现官方报错。

## 0.1.13 试玩焦点交接

PlaytestWindowActivation 在 RunAsync 发出 open 后等待自有子进程可见窗口，再由仍持有前台的编辑器调用 SetForegroundWindow；取消、退出或 10 秒截止终止等待，用户主动切到其他进程则停止交接。避免仅授权子进程/调用 SDL Raise 却没有实际切换前台。ResidentSupervisorSpecs 使用响应消息循环的原生父进程窗口，三个回合都断言 Windows 前台 PID 为实际 GPU 游戏宿主；保留资源缓存、同 PID、异常重建和关闭测试。

## 0.1.14 试玩与选项交互

官方游戏 Content 顶层 PracticeShortcutHandler 在 replay/ruleset 输入之前接管 Shift 快速暂停/继续和 Esc 直接 ReturnToEditor；不改官方渲染、皮肤、计分。--verify 的 VerificationInputManager 注入标准 KeyboardKeyInput 走完整队列，检查左右 Shift、按住不重复、暂停时钟停止、Esc 返回和连续三次完美演奏（artifacts/resident-playtest/d26fb72f25084ef6b82541d9f842b2ab）。MainWindow.OptionControls 统一滚轮和逐项默认值按钮，显式引用出厂值；逻辑树要 Distinct，调整卡片的复原放在标题行保证滑条与刻度对齐。外观 Expander 默认收起，编辑器皮肤从快捷键区移入。OD/HP 原已有模型/读写但控件未入 UI，现在进入元数据页；提交前两项都校验 0–10，原子提交/撤销。底板两侧 padding=Viewport.Width；重建轨道临时缩小 extent 会强制偏移，因此 RestoreBoardPanAfterLayout 在最终布局后恢复平移。UiSmoke --workbench-test 提供快速编辑器回归（输出在隔离临时截图目录），完整脚本仍覆盖历史音频/皮肤用例。

## 0.1.15 输入修正与原生 Timing

0.1.14 的 Content 级 PracticeShortcutHandler 在真实输入中可能被全局 Back 抢先处理，已经移除。PracticeInputManager 在 HandleKeyboardKeyStateChange 拦截 Shift/Esc，其他行为对应 upstream OsuUserInputManager；VerificationInputManager 继承它，验证和实际输入走同一路径。Shift 调用官方 Pause/Resume；官方 Pause 拒绝 replay，所以自动演奏薄覆写 Stop+PauseOverlay.Show，仍使用官方面板和 Resume。真实 GPU 回归覆盖播放中、自动暂停和手动暂停面板中的 Escape。

独立 playtestRate 和 SnapAll 存于 editor-presentation.json。RelativeBeatMove 支持绝对吸附修正，导入大分母使用当前分度；已写入的合法细分用 <=1920 的公共网格保留，防止粗网格压短 LN。未吸附移动的 LCM 超限使用半微拍误差以内的有界表达。原生负拍允许编辑；MinimumEditableBeat 按 session/serial 缓存，避免逐帧建 TimingMap。调整卡片默认仅跨工具/底板的前三列，避免遮住右侧栏。

OsuTimingEditingService 使用绝对整数毫秒 DTO，草稿弹窗不占历史。_sme_requested_timing_ms 由原生 ReanchorTiming 消费，再重新建立时间映射，保留旧音符绝对时间。OsuTimingPointSettings 在 UnknownData 中保留节拍数/bank/index/volume/effects；原生写入现有字段，MC recovery 的红/绿线均保存。新增事件继承当时音效/Kiai 设置，编辑 Kiai 保留其他 effects 位。UI 原生 Offset 对 Malody Delay 在捕获/填表边界反号；BPM 显示六位小数，时间一毫秒，原文件未编辑行继续保真。MainWindow.TimingExperience 和 Dialogs/OsuTimingDialog 隔离新功能，MC 保留原来的 Timing 表单。

## 0.1.16 工作台与音频所有权

- 试玩入口先设置 playtestInProgress、取消 Timeline 指针预览、禁用 editorView、暂停音频再保存。ToggleAudioPlaybackAsync 由 editorPlaybackCommandGate 串行化；PauseAll 使用 idempotent transport.PauseAsync，取消 seek 与 silent clock。主窗口键盘、帧泵与 seek 恢复均检查 playtestInProgress；结束 finally 解锁。
- WindowsRenderingConfiguration 使用 WinUIComposition / DirectComposition / RedirectionSurface，不再优先 LowLatencyDxgiSwapChain；具体撕裂效果需实机。
- EventTimeRuler 按 session serial 缓存事件/Kiai，播放只更新位置；时间轴可拖动 seek、双击原生事件编辑。Timing 右键/擦除删除入口完全移除，保留选中后删除。
- MainWindow.GlobalOverviews 固定左侧全局视图与显隐偏好；DifficultyOverview 未有 Rework 结果时显示实验负荷。EditorMetricTrackSurface.CalculateLocal 为 500 ms 窗口、8 ms 采样（最多 250k）、连续屏幕绘制；Calculate 保留全局每秒分桶语义。
- WorkbenchSafetySpecs 覆盖实际编辑器音乐播放 + 滚轮后试玩、输入锁定/返回、silent clock、固定全局/持久化、Timing 右键/擦除、Kiai 范围/时间定位和局部 NPS。OptionControlsSpecs 改为只允许全局偏好恢复按钮。

## 0.1.17 恢复、布局与帧时钟

- DesktopDocumentWorkspace.PersistCoreAsync 必须对所有实际写入的缓存更新 WorkingSha256；旧代码仅 retained room draft 更新，导致普通 checkpoint 的误判和丢弃。RequiresRecoverySaveAs 跨清单持久化：不可信但有效的旧脏副本保留，Save/同路径 SaveAs 阻止覆盖；不同路径成功另存为或从源重新载入后解除。无效缓存保留到 editingRoot/unverified，独立于标签缓存并受清理保护；关闭恢复标签不删除它。
- 当前用户 FEHLT 只读检查：源 hash 匹配、缓存不匹配。没有改写用户清单、谱面或缓存。历史已被旧版覆盖的内容不保证可找回。
- TrackBoard 使用 desiredBoardPan/pendingBoardPan，实际 Extent 就绪才应用 Offset；LayoutUpdated/ExtentProperty 触发提交。顶部 Views flyout 同步原全局显隐设置，Arrange 归位主轨和卡片。
- EditorPlaybackVisualClock 只影响显示，使用 compositionTimestamp 和有界 2.5% slew；音频/打击音依旧采用原设备时钟。UpdateUi 播放中禁止把 timeline 写回 stale ActiveTab.CurrentBeat。
- WorkspaceCheckpointSpecs：checkpoint hash/dirty reopen/旧 hash recovery/原谱保护/SaveAs/损坏副本保留。StartupPlaybackSpecs：布局/resize/归位/全局恢复/长路径提示/播放刷新不回跳/离散音频时钟匀速及漂移。
