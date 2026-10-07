# 基于 LumaChart Studio 的改造方案与源码审计

日期：2026-10-07。

实施更新（2026-10-08）：已交付 0.1.0 改造体验版，见 [发布说明](RELEASE-0.1.md)。本文保留为改造前审计基线，不能将其中的上游缺陷描述当成当前版本的全部状态。

用户已确认：原先 Python + Web 技术栈可以调整，优先复用成熟代码。因此当前建议路线变为 **C#/.NET + Avalonia，基于 LumaChart Studio 客户端建立独立改造分支**。原需求中的功能目标、7K 优先级和数据可靠性要求不变。

本次完成源码审计、客户端领域测试、Windows Release 构建和 `.osu` 往返探针；未修改上游应用源码、未创建远程 GitHub fork、未完成真实 GUI/设备验收。具体结果见验证记录。

## 1. 结论

值得作为基础，比从零复刻已有播放、渲染、LN、格式导入和工作区设施更有价值。但不能把它理解为“改快捷键就能满足全部需求”：

- 操作习惯涉及鼠标状态机、选择、拖动、吸附、滚轮、粘贴与面板反馈。
- 原生 `.osu` 保留能力需要优先补强，不能只验证音符数量没变。
- 快照撤销不是规划中的持久化 command 历史。
- 分组和横向草稿列不是完整的多轨道模型。
- Sunny 当前实现仅支持 4K，不能直接满足 7K 难度需求。
- 已有客户端协作不等于可复用的自托管服务器；服务端不在该公开仓库内。

先做一个完整的 osu! 编辑流程，验证收益和改造边界，再逐步补齐高级功能。避免先花大量时间重写底层，失去复用意义。

## 2. 审计基线与证据范围

仓库：[cux-rn/LumaChartStudio-Client](https://github.com/cux-rn/LumaChartStudio-Client)。

固定 commit：`b78bdf6d2a4461184a2b56d474dce6aa1680a09c`。

独立浅克隆保存在系统临时目录；用户项目目录没有塞入完整上游源码、SDK 或 NuGet 缓存。以下链接固定 commit，后续改造应记录 upstream 基线并保留归属。

证据分为源码可确认、实际运行验证和待真实环境验证；仓库 README/功能矩阵中的 Ready 不自动等于满足我们的验收。

## 3. 技术栈与可复用边界

Windows 主项目为 `net8.0-windows`，Avalonia 12.1.0；Core/Application 等为 `net8.0`。音频使用 NAudio、Vorbis/MP3 解码和 SoundTouch，显示/分析均为原生 C# 实现，不是 Web 前端。

目标框架为 net8 不代表 SDK 8 能编译全部项目。本轮 SDK 8.0.425 在 Avalonia 的 Roslyn 分析器/生成器处报 CS9057；改用 SDK 10.0.401 后 Windows Release 构建通过。F0 应固定兼容 SDK 与依赖，加入 global.json 和可复现构建说明。

| 现有模块 | 判断 | 改造边界 |
| --- | --- | --- |
| Core 的音符、分数拍和 timing | 有价值 | 保留基础结构；增加 osu 原始字段及权威时间的表示，避免 Malody 转换成为唯一状态 |
| Application 的编辑服务 | 可复用 | 逐步统一命令执行入口；不能继续允许任意 UI mutation 绕过历史 |
| 渲染与音符区间索引 | 可复用 | 已有可见窗口/长 LN 索引；仍需针对每次编辑的重建成本实测 |
| 波形/频谱与播放链 | 可复用 | 用真实样本测试 seek、设备延迟、极端倍率和声音语义 |
| tap/LN、批量变换、分组、草稿 | 可复用 | 换 osu 输入配置；草稿列逐步升级为真正独立轨道 |
| 工作区、多 tabs、备份/恢复 | 可复用 | 保留已有原子写入与快照设施，再增加频繁命令日志和恢复确认 |
| 谱面库、封面、目录访问 | 部分复用 | 保留 UI 与资源访问策略，加入持久化增量索引、多 root、私有资料 |
| 试玩 | 部分复用 | 渲染/输入可用，判定、计分、LN 与 OD 需要 osu profile |
| Sunny/Rework | 部分复用 | 当前 4K 限制，7K 和官方 strain 需要独立 provider |
| 联网客户端协议 | 作为参考保留 | 不依赖作者生产服务器；后续自行实现可本地部署的服务端 |
| Android/iOS hosts | 暂缓产品验收 | 首发 Windows；尽量不破坏编译边界，不为它们同时投入完整测试 |

工程依据：[Windows 项目配置](https://github.com/cux-rn/LumaChartStudio-Client/blob/b78bdf6d2a4461184a2b56d474dce6aa1680a09c/LumaChart.Avalonia/LumaChart.Avalonia.csproj)、[音符索引](https://github.com/cux-rn/LumaChartStudio-Client/blob/b78bdf6d2a4461184a2b56d474dce6aa1680a09c/LumaChart.Avalonia/Rendering/TimelineNoteIndex.cs)。

Python 后续可以作为离线分析的可选工作进程，例如复用算法原实现；不需要为了原方案再把编辑器切回 Python/Web，关键输入和音频路径保持原生。

## 4. 操作习惯：需要改动的真实入口

现有默认工具切换为 Q/W/E/R，数字键用于分组；快捷键已经支持 JSON 配置、冲突检查和编辑上下文。这部分适合直接复用。

但 `TimelineSurface` 里的鼠标行为目前直接决定：

- 右键点音符删除，右键空白拖动框选。
- 左键命中音符时选择/拖动，Shift 切换选择，Ctrl 保留批选择；LN 尾部有单独缩放路径。
- 中键平移；ProEditor 中 Space+左拖横向平移。
- Ctrl+滚轮缩放，ProEditor 中 Shift+滚轮横移，普通滚轮按当前分度导航。
- 时间轴控件自身还有上下键移动路径，窗口快捷键路由并不是唯一键盘入口。

这些是对当前源码的描述，不能据此把全部行为称为 Malody 官方行为，也不代表每项都与 osu 不同。用户提供的 mapper 反馈是产品问题依据，具体差异需要逐项对照。

源码：[快捷键配置](https://github.com/cux-rn/LumaChartStudio-Client/blob/b78bdf6d2a4461184a2b56d474dce6aa1680a09c/LumaChart.Avalonia/Infrastructure/WindowsEditorShortcutSettingsStore.cs)、[时间轴输入实现](https://github.com/cux-rn/LumaChartStudio-Client/blob/b78bdf6d2a4461184a2b56d474dce6aa1680a09c/LumaChart.Avalonia/Controls/TimelineSurface.cs)。

### 4.1 改造方案

建立 `EditorInteractionProfile`：默认 `OsuMania`，保留 `LumaLegacy`。profile 分别定义键盘映射和鼠标手势；输入先解释成编辑意图，再由统一执行入口变成 command。

需要覆盖的交互契约：

| 场景 | 要固定的行为 |
| --- | --- |
| 工具切换 | 选择/tap/LN 的快捷键、状态可见性、切换后选区是否保留 |
| 点击已有音符 | 当前工具下是选择、移动、删除还是阻止重叠；不能靠偶然分支顺序决定 |
| LN 创建/伸缩 | 按下/拖动/松开、反向拖动、头/尾命中、跨屏滚动、取消 |
| 框选与追加选择 | 框选方向、LN 按头或相交、Ctrl/Shift 的含义、多选拖动 |
| 滚轮与快捷导航 | 游标、可见区域和歌曲位置如何联动；按分度/拍/时间步进 |
| 时间/列移动 | 是否保持原间距、是否重新吸附、循环列移动和冲突策略 |
| 粘贴 | 歌曲时间/拍锚点、鼠标位置、重复粘贴后的光标及选区 |
| 播放与编辑 | Space、暂停、循环和继续放键；与平移手势不产生二次触发 |
| 文本输入/弹窗 | 输入框正常使用 Ctrl+C/Z；编辑快捷键不穿透对话框 |
| 焦点丢失 | 清除 modifier 和 pointer capture，取消/提交有明确规则 |

先按指定 stable 版本和用户真实使用方式建立对照，不凭记忆声称完全官方一致。以可快速试用的纵向流程确认，而不是交付一份需要用户自己配置几十项的设置。

## 5. `.osu`：已经实际验证的资料丢失

`OsuManiaSerializer.Save` 重新生成 `.osu`。`DesktopDocumentWorkspace.WriteSourceSnapshotAtomicAsync` 的 osu 原位保存路径也调用此 writer，原子文件替换不能解决语义丢失。

合成 7K 样本经过公开 Specs 的 `--normalize-osu` 路径实际执行一次读取→保存，结果如下：

| 资料 | 结果 |
| --- | --- |
| 7K 列数、样本中音符/LN 时间、声音文件名 | 此样本保留 |
| Source、Tags | 清空或替换为固定内容 |
| BeatmapID/BeatmapSetID | 改写为 -1 |
| Editor Bookmarks、原始分度 | 不保留原始值 |
| Video、break、sample events、未知 section | 输出中缺失 |
| timing 拍号、sampleSet/index/volume/effects | 改写为固定值 |
| hitSound 位及 sampleSet/addition/index、继承音量 | 改写，不保持原语义 |

例如，探针中红线属性 `0,500,3,3,2,37,1,1` 被写为 `0,500,4,2,1,70,1,0`。这是合成样本的实际结果，不是用户真实谱面的操作记录。

源码：[osu writer](https://github.com/cux-rn/LumaChartStudio-Client/blob/b78bdf6d2a4461184a2b56d474dce6aa1680a09c/LumaChart.Core/IO/OsuManiaSerializer.cs)、[工作区保存](https://github.com/cux-rn/LumaChartStudio-Client/blob/b78bdf6d2a4461184a2b56d474dce6aa1680a09c/LumaChart.Application/Workspaces/DesktopDocumentWorkspace.cs)。探针文件见 [合成样本](../fixtures/compatibility/luma-roundtrip-input.osu)。

因此首个改造增量必须增加 osu source envelope/字段保留与语义写回：读取原始 section/行/字段，保存只更新实际编辑的数据。原生 osu 文档的资料不能仅靠 `.mc` 工作副本重建。

同时明确两种保存策略：原生 osu 编辑保持音符绝对毫秒时间；拍坐标重定时作为显式操作。现有分数拍模型可保留给网格/排列/Malody，不需要一次删除全部 beat-based 实现。

先在副本上跑官方解析器与游戏验收，直到完整往返测试通过再开放正式 Songs 原位编辑。原始 ID 不能默认清零；只有用户明确创建独立新谱面身份时才应用该策略。

## 6. 历史、恢复、多轨道和大库的差距

### 6.1 历史与可靠性

`EditorSession` 使用 before/after 全文档 Clone 保存 HistoryEntry，最多 200 项。存在统一 mutation 和交互手势合并入口，这有利于改造；但任意 Action 不能直接持久化或确定性回放。

将其逐步改为有操作 ID、明确目标 ID 和前后值的增量 command。先覆盖常见音符/timing/元数据操作，再淘汰旧 mutation 入口；过渡期的全快照操作要明确标记并可恢复。

现有 workspace 有 `working.mc`、原子写入、备份和恢复；UI 有自动备份定时器，默认周期为 5 分钟，且有异常退出紧急持久化。不能声称现有项目没有自动备份，也不能把这些等同于每次已确认编辑的持久化日志。强制终止不一定有机会运行退出处理。

大谱面下全文档 Clone 与索引重建是需要测量的性能风险；本次没有测得它必然卡顿，不用代码规模替代性能实测。

### 6.2 多轨道

现有 `ChartNote.Group/IsDraft/DraftColumn` 可以复用为过渡；它们仍在一个 document.Notes 列表中，没有完整 track 类型、独立播放/滚动、来源文档及资源导出模型。

后续增加稳定 track ID、正式/草稿按键轨道、音频、声音编辑、只读对照等；避免把横向增加草稿列误当成增加游戏列数。保留现有草稿不导出的测试。

### 6.3 大库

当前 `DesktopChartLibraryService` 会递归枚举文件，读取元数据、返回内存列表，并用内存字符串 Contains 过滤。已有并行读取、进度、目录边界和 UI 虚拟化相关实现，但当前读取路径没有持久化增量索引。

保留库 UI 和安全路径处理，接入 SQLite 索引、多个 root 和独立 JSON 私有资料；重启避免再次遍历解析全库。标签、未发布状态、备注与排列库都不借用官方 Tags 字段。

## 7. 音频、试玩和难度的差距

- `WindowsAudioPlaybackService` 和设备层把 rate 限在 0.2～1；底层某个 provider 接受 0.1～4 不代表上层可以使用。需要修改整个 transport/缓存/DSP 路径并验证全部需求预设。
- 现有保音高链可复用，但 0.1x、2x 与独立半音变调都需要专项验收。原生 WASAPI 也不自动保证低延迟，缓冲和 DSP 延迟仍要测量。
- `PlaytestSession` 的判定枚举为 Best/Great/Good/Miss；新增完整 osu 判定 profile，验证 OD、rate、LN 头尾及计分。已有视觉与自动播放可保留。
- `ReworkDifficultyAnalyzer` 明确拒绝非 4K。7K 不能只删除这条检查，内部 4K 参数/预处理也必须核查；通过独立 provider 接入固定的算法来源与版本。
- 自动 BPM 有 `VariableTempoEstimator` 和 beat analysis 设施，优先复用，但音乐样本质量、变速误差和可接受结果流程还需实测。

源码：[播放 rate](https://github.com/cux-rn/LumaChartStudio-Client/blob/b78bdf6d2a4461184a2b56d474dce6aa1680a09c/LumaChart.Avalonia/Audio/WindowsAudioPlaybackService.cs)、[试玩](https://github.com/cux-rn/LumaChartStudio-Client/blob/b78bdf6d2a4461184a2b56d474dce6aa1680a09c/LumaChart.Application/Playtest/PlaytestSession.cs)、[Rework 4K 限制](https://github.com/cux-rn/LumaChartStudio-Client/blob/b78bdf6d2a4461184a2b56d474dce6aa1680a09c/LumaChart.Core/Analysis/ReworkDifficultyAnalyzer.cs)。

## 8. 工程可维护性

存在 Core/Application/Online/UI 的分层，也有许多纯策略/渲染类；这是复用的正面基础。与此同时，按非空行统计，MainWindow 约 24k 行、TimelineSurface 约 4.3k 行，交互与 UI 仍高度集中。

改造时按功能提取 InputRouter/GestureController、工作区操作、面板视图模型和 command dispatcher，每次有针对性的回归；不一次性把整份大窗口改写。旧 tests 中的“WPF parity”约束是上游产品目标，osu profile 不能被旧行为锁死；保留 legacy profile 用例，并为 osu profile 建新语义测试。

部分 UI Specs 硬编码作者机器的 D:\\osu!\\Songs 等 fixture 路径和哈希，不能在干净 checkout 假定完整 UI 测试可运行。需迁移为可分发自制 fixtures 或可配置、明确跳过的私有样本测试。README/功能矩阵还存在过时的服务器或平台说明，以固定源码与实际构建为准。

## 9. 许可与联网边界

客户端自有源码为 MIT，可在保留版权与许可声明的条件下修改、复制和分发。第三方依赖仍按各自许可处理；尤其 SoundTouch.Net 和选择 LGPL 的 LibNMeCab，不因为客户端 MIT 就消失。字体、字典和派生算法的 notices 保留。

依据：[LICENSE](https://github.com/cux-rn/LumaChartStudio-Client/blob/b78bdf6d2a4461184a2b56d474dce6aa1680a09c/LICENSE)、[第三方 notices](https://github.com/cux-rn/LumaChartStudio-Client/blob/b78bdf6d2a4461184a2b56d474dce6aa1680a09c/THIRD-PARTY-NOTICES.md)。

公开仓库是客户端；作者服务端代码不包含在内。基本离线编辑可以复用，账号/联网房间不能直接宣布实现了我们的可自托管局域网协作。首发不要求账号、作者服务或自动访问生产服务器；联网扩展保持 TODO。

建立独立项目名与数据目录，保留上游出处；不沿用作者生产更新地址、账号配置或覆盖其原应用安装/用户资料。不因为建立本地分支就自动创建远程仓库或向作者发送消息。

## 10. 修订后的开发路线

| 阶段 | 工作 | 验收点 |
| --- | --- | --- |
| F0：固定上游基线 | 独立分支、构建复现、原测试、可移植 fixtures、独立数据目录/离线入口 | 干净环境可启动；已验证与待验证范围明确 |
| F1：osu 操作与安全读写 | osu profile、主要鼠标/键盘路径、原生 `.osu` 保留、7K 试用流程 | 开图→写 tap/LN→框选移动→粘贴→撤销→试听→另存→osu 打开 |
| F2：可靠的编辑底座 | 增量 command、落盘日志、恢复、时间语义、模式和网格 presets | 崩溃/断电近似故障注入通过；M1/M2 原要求对应补齐 |
| F3：音频和试玩补齐 | 全部倍率、独立 pitch、hitsound 继承、osu 判定、频谱 | 原需求默认预设全部通过；设备延迟和官方行为有记录 |
| F4：大库与迁移 | SQLite 增量索引、多 root、标签/备注/筛选、双向事务迁移 | 50k/200k 基准及失败恢复 |
| F5：创作工作台 | 多轨道/对照/多文档、中心对车、面转米、排列库与文本 | 草稿隔离、跨 BPM/轨道粘贴、分享往返 |
| F6：历史与分析 | 版本/diff/可选 Git、官方/7K Sunny、BPM/offset、候选预览 | 固定 oracle、资源回溯、过时分析与性能测试 |
| F7：正式交付收尾 | mania skin/UI presets、官方完整功能矩阵、Design/事件、发布升级 | 原非 TODO 需求全部逐项验收 |
| F8：TODO 扩展 | AI 元数据/check、米转面、自托管协作 | 每项单独设计与验收 |

F1 是最先给 mapper 判断“操作习惯是否合适”的版本，F2 后再作为正式日常创作工具。F0/F1 的测试只用副本；语义保留和恢复通过之前不开放正式 Songs 覆盖流程。

原 [完整规划](PLAN.md) 中的需求追踪、工具语义、存储目标和 TODO 范围继续作为需求基线；Python/Web 实现细节和 M0 从零实现步骤被本方案替代。原 [验收计划](ACCEPTANCE.md) 的核心不变量仍适用，跨语言/浏览器专项改为 C# 命令一致性、Avalonia 输入和原生音频测试。

## 11. 本轮验证记录

- SDK：官方 .NET SDK 8.0.425 与 10.0.401，下载 ZIP 后分别校验发布元数据的 SHA512，临时目录隔离；没有改动系统 SDK 安装。
- `dotnet run --project LumaChart.Specs/LumaChart.Specs.csproj -c Release`：退出码 0，**48/48 specs passed**。不包含未公开服务端的测试。
- Specs 的 `--normalize-osu`：执行合成 7K 文件，证实上述 round-trip 资料损失。源样本未指向用户真实 Songs。
- Windows Release 构建：`dotnet build LumaChart.Avalonia/LumaChart.Avalonia.csproj -c Release -warnaserror`，SDK 10.0.401，退出码 0，**0 warnings / 0 errors**。首次使用 SDK 8.0.425 因 Avalonia 生成器所需编译器版本更高而失败，切换 SDK 后通过；没有禁用分析器或放宽 warnings-as-errors。
- 完整 Avalonia UI Specs、音频设备及真实 osu!：尚未执行，硬编码私有 fixtures 需先处理。

本轮验证不等于现有客户端全面符合本项目目标；尤其通过原 tests 的 writer 仍在探针中丢失 osu 资料，后续必须增加独立格式保留回归。
