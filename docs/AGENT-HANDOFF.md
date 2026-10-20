# SimpleManiaEditor 新 Agent 交接说明

交接日期：2026-10-20（0.1.37 收尾）。当前开发基线：**0.1.37 本地版**。本文件汇总有效需求、当前实现、验证边界和接手方法；详细技术沿革见 DEVELOPMENT-HANDOFF.md，完整功能矩阵见 FEATURES.md / BACKLOG.md。

## 1. 首先要知道的状态

- 工作目录：`E:\Files\Developing\SimpleManiaEditor`，Windows / PowerShell。
- 仓库：`https://github.com/Ist1na07/SimpleManiaEditor`，当前分支 `main`。
- 交接时 HEAD：`e60a1a6`，2026-10-20，`Add 0.1.37: cached library entry, tab overflow paging, stretch noise fixes`。这次提交把自 0.1.33 起积压的全部源码、测试、脚本和文档一次性入库（81 个文件，+28,408/−23,057），**工作树已提交且干净**；只提交源码，没有 push、没有 tag、没有 Release。提交者身份沿用本仓库既有历史 `Codex <codex@local>`（只写在仓库级 user.name/user.email，未改全局配置）。
- **仍然不要 reset、checkout 覆盖、git clean 或 stash/drop。** 上一轮正是 25k 行主文件在工作树里长期没有快照才损失惨重；从现在起每完成一块可运行改动就小步提交，不要把几轮工作再堆在一起。
- **0.1.37 源码事故（必须知道）**：收尾时一个 Agent 用默认限行的 read（约 950/25,257 行）读了 `src/LumaChart.Avalonia/MainWindow.cs` 后整文件写回，导致该文件被截断；当时工作树未提交，git 里没有任何快照。已用 ilspycmd 反编译 20:47 编译的 0.1.37 程序集恢复：保留 16 个未受损的 `MainWindow.*.cs` partial 的 313 个成员，重建主文件 1,437 个成员，并补回原文件末尾的 3 个辅助类型（WindowsAvaloniaEditorInputPolicy / WindowsOnlineRoomEditorAccess / WindowsOnlineRoomPermissionPolicy），把 ILSpy 的只读集合辅助类型还原成普通数组，清掉两处反编译状态变量。**代价：该文件的注释、原始格式和 `#if LUMACHART_SHARED` 分支代码已丢失（本 checkout 不构建该配置），并加了 `#nullable disable` 与告警抑制 pragma。其余源文件均为原件。** 证据在 artifacts/decompiled、artifacts/MainWindow.split.cs、artifacts/MainWindow.truncated.cs、artifacts/splitter。该文件已通过 -warnaserror 零警告编译并重跑全套回归；后续可逐步补注释、恢复 nullable。**教训：read 有默认行数上限，修改大文件必须指定 offset/limit 或改用 edit；工作树长期未提交本身就是风险（0.1.37 已提交为 e60a1a6，此后应小步提交，勿再堆积）。**
- 接手先看 `git status --short` / `git diff` / 未跟踪文件。不要 reset、checkout 覆盖、git clean、自动 stash/drop 或将这些文件当残留删除。`docs/历史会话.md` 是最早期的需求与规划对话记录，现已入库，保留它（含用户最初的功能清单和工作方式约定）。
- 正常入口：根目录 `Start-Editor.cmd`。当前指向：
  `dist/SimpleManiaEditor-0.1.37-Local-20261020-212306/app/SimpleManiaEditor.exe`。
- 当前版本在 `SimpleManiaProduct.cs`、Avalonia csproj 与 OsuHost csproj 中均为 0.1.37（宿主版本号此前滞后，本轮同步）。最近公开分发版记录为 0.1.31；本地 .32～.37 不能当作已公开发布。
- 0.1.37 已修正：曲库进入不再扫描（只读缓存＋手动刷新）、标签页溢出可达、非 1x 播放每音符爆音。真实设备听感、真实 46k 目录刷新耗时、标签条真机手感仍未验收。
- 若换电脑/工作目录：必须携带全部本地已修改与未跟踪源码；作品/个人资料另外完整备份。不要只带 GitHub 或只带 .osu。

### 0.1.37 本轮变更（交接必读）

用户这轮提了三件事，均已完成编码、构建与 headless 验证；结论与边界如下。

1. 曲库进入要“一进去就是好的，只读缓存，给按钮手动刷新”。现在曲库三个入口（品牌图标／顶部“曲库”／启动默认页）都不再触发目录扫描，只调用幂等的 EnsureLibrarySnapshotLoadedAsync() 读持久化索引；启动也不再等待扫描。唯一全量扫描入口是「刷新索引」按钮（新建／切换工作区、添加只读曲库仍会扫描）。watcher 批量变化（>256 路径／overflow）只写“目录有变化，点刷新索引”提示，不再自动扫；保存／新建／删除仍立刻反映。上次刷新时间持久化在 SQLite index_metadata 表并显示在曲库头部与状态行。
2. 标签页溢出。标题栏标签条改为可滚轮横滚（1 格 120 DIP，仅溢出时消费），溢出时两端才出现 ‹ › 箭头（点击一屏 85%，按住 380ms 后每 70ms 连滚），激活标签在激活／新建／关闭邻居／缩放后自动入视，手动滚动的偏移会保留；长标题改为省略号，修掉两个标签前缀相同看不清区别的问题。未溢出时不占位，编辑器时间轴与曲库滚轮不受影响。实现在新的 MainWindow.DocumentTabs.cs。
3. 非 1x 播放噪声。实测根因：分段 flush 的瞬时 SoundTouch 空读被当作 EOF，剩余预算补零，波形一次跳变 0.30 满量程；输出静音保护又按样本阶跃增益。现在：瞬时空读有界重试、短段尾部按 3ms 余弦衰减而非补零、每个段接缝 3ms 升余弦淡变且淡入等到第一个真实样本、攻击边界至少间隔 80ms（release 仍立即切开，保证短 burst 不被合并）、输出增益静音保护从链路停用。普通素材（鼓点＋底音）九倍率最大台阶 0.006–0.014（改前 0.30，未分段参考 0.005–0.011）；数字静音间隙仍有约 0.082、每音符一次的残余台阶；持续单音不比未分段差。真实 MP3 只读起音 206.68–207.70ms（原始 207.94ms，0.1.36 为 207.53–207.76ms），最多早 1.3ms，仍在 8ms 判据内，原文件 SHA256 未变。**这是设备前 PCM 证据，不代表真实耳机/声卡听感；残余数字静音台阶与 WSOLA 音色仍需用户耳朵验收。**

本轮验证：核心 59/59；--mapper-034/035/036、--regression-030、--library-037、--tabs-037、--audio-026、--audio-gate-036、--audio-noise-037 全 PASS；真实 Albemuth MP3 只读起音/跳转 PASS（SHA256 未变）。--mapper-033 在 MapperWorkflow033Specs 第 183 行（排列虚影后按 Space 等待编辑播放变为 active）间歇超时：同一份二进制本轮既出现过失败也出现过通过，与第 10 节早已记录的同类 flake 一致；本轮未给出根因，只保留日志（artifacts/agent-final033isolated-*、agent-diag033*、agent-flake033-*），不能宣称已修复，也不能未经复现就归咎于某次改动。最终本地包 dist/SimpleManiaEditor-0.1.37-Local-20261020-205925，smoke 0.1.37 与 storage 双 PASS，入口已更新；未创建 Release/tag/ZIP。
## 2. 与用户协作的约定

用户是实际 7K mapper，负责功能方向、真实环境测试和手感验收；Agent 负责设计、编码、构建、工程测试和交付可运行版本。不要把编译、测试和重复排错工作推给用户。

用户最不满意的是：功能虽然存在，但交互不合直觉，随后长时间逐个纠正。必须先在脑中走完实际 mapper 工作流，再改界面。按钮是否常驻、是否属于当前工具层级、焦点和快捷键冲突、取消是否会误操作、只读查看是否会阻止退出，都属于功能本身。

- **2026-10-20 起的分工（用户明确要求）**：主会话/Lead 只做协调、把关和转述，具体的编码、构建、测试、排查交给子代理或智能体团队分片执行（提交源码这类极简单动作可以主会话自己做）。原因：单个会话上下文已到 500K，长上下文会明显削弱判断力。派活时必须写清文件范围与写作用域，避免两个 agent 同时改同一文件；构建与 suite 用 `scripts/Agent-Verify.ps1` 串行，不要让多个 dotnet build 抢 bin/obj。
- 有明确要求就直接完成；日常可逆实现不要重复请求许可。
- 不自行改写已确定的操作习惯；新需求优先于早期记录。
- 不随意扩大范围，不因为“已有代码”就把多余概念暴露给用户。
- 进展说明简洁，长任务持续说明真正发现和剩余不确定性。
- 测试通过不等于用户手感通过。未复现的实际问题要明确写“未复现/未定位”，不要将防护补丁说成根因已解决。
- 用户允许过子代理排查；新接手 Agent 是否使用子代理按当次任务/自身工具规则执行。旧 Agent 的内存和会话不能当作可靠交接载体。
- 用户曾建议使用本机 ZCode Desktop 的 commandcode / deepseek-v4.1-flash-fast 加速开发，这只是可选偏好，不表示已有可调用集成，也不是必须依赖。

## 3. 不可破坏的工程和资料边界

**首先阅读根目录 AGENTS.md，它是实际项目工作流约束。**

1. 日常交付运行 `scripts/Build-Local.ps1`，完成相关测试并更新 `Start-Editor.cmd`。不要只改源码或只更新版本号。
2. **没有用户明确要求本次发布，就不创建 Release ZIP、tag、GitHub Release，也不上传分发包。** 创建仓库/获准推源码不等于批准 Release。
3. 不能丢失用户谱面、音乐、皮肤、设置、标注、历史、备份、checkpoint 和恢复资料。
4. 真实 osu! 安装（过去调查为 `E:\osu!`）及 Songs 是只读输入。唯一允许写入的产品路径是有明确预览和确认的整套谱面迁移事务；不能用修复、重置数据库、改权限/注册表/文件关联等操作作为诊断捷径。
5. 合成测试放独立 Temp / 显式隔离资料目录，不能使用真实资料做破坏性测试。
6. 清理只走 `scripts/Clean-LocalBuilds.ps1`，每次生成新计划，保护当前/演示/公开运行目录并校验保留文件哈希。不能复用旧计划；**不能递归删旧 dist：里面确实有用户作品。**
7. 不自动上传私人日志、曲库数据库、原皮肤、音乐、截图或作品。分发包不得包含用户资料。
8. 保留 Ist1na 的项目署名、上游 LumaChart Studio 与第三方许可证；不是抹掉上游版权换自己的名字。

## 4. 产品定位和技术路线已经变更

最初想 Python 后端 + Web 前端，但用户明确接受改技术栈，优先复用成熟代码。因此当前实际是：

- 基于 `cux-rn/LumaChartStudio-Client` 改造的 C#/.NET + Avalonia 桌面编辑器。
- 面向 osu!mania，优先 7K 和 osu mapper 的操作习惯；默认 osu 最小模式。
- 暂不以 Malody、多平台、多端兼容为产品目标。保留旧格式/代码用于内部缓存或回归，不意味着应把相关入口重新放出来。
- 上游账号/线上服务不作为当前产品依赖，`SimpleManiaProduct.OnlineEnabled` 为 false；离线本地可用。
- 主编辑器 .NET 8；官方试玩宿主 .NET 10；构建 SDK 固定 10.0.401。
- **试玩已使用真正的官方 lazer 游戏/mania/皮肤组件，独立进程运行，不是旧自制渲染器。** 用户早期要求 stable，后续明确接受全面 lazer style。不要再为还原 stable 重写整套玩法/皮肤。
- 官方 NuGet 版本/对应提交、薄适配层和历史限制见 PLAYTEST.md；不能随意升级依赖或关闭审计绕过锁定问题。
- 完整 Git 快照历史已有；逐鼠标操作的持久化 command journal 尚未做，不能把当前历史描述成“所有操作已持久化”。

## 5. 已确定的交互契约，后续不要退回旧方案

### 编辑器命令与布局

- 顶部是文件/播放/停止/撤销/重做/视图/试玩等命令；左侧是实际 note 编辑工具与变换。
- 基本工具只有选择、单键、长条、擦除。**中心对称、排列库、拉伸是扩展功能，三者并列在“排列与变换”，不与四个基本工具并列。**
- 常驻“放入排列轨”按钮已经删去；加入主轨选区的操作在对应排列轨内。
- 顶部固定工具栏是一栏，试玩入口只保留一个，不恢复“测试/从此处/试玩”重复入口。
- 顶部文档标签可横向滚动：滚轮只在标签条溢出时消费（1 格 120 DIP），溢出时两端出现 ‹ › 箭头（点击一屏、按住连滚），激活标签自动滚入可视区；未溢出不得出现箭头或占位，也不得劫持编辑器时间轴/曲库列表的滚轮。长标题用省略号，不要硬裁。
- 顶部网格、倍速为下拉；网格/缩放/倍速的带刻度滑条在右下可移动“调整”卡片。
- 设置按编辑/操作/外观/音频/试玩/存储/通用分类并可搜索，统一文字层级。分类内重复的“快捷键/外观”折叠壳已移除，不能只是改为默认展开后留下无意义壳。
- “显示全局密度/难度”只保留顶部视图入口。分组功能暂时隐藏，不是待重新展示的主要工具。
- 1080p/1440p 有自动 UI 大小和手动覆盖；不要只在 2K 大窗验收。

### 放置、选择与手势

- 音符实际时间在图形最底边；底边贴格线上边缘。选中效果不能增加外形大小。Tap、LN 头尾同形，LN 身略窄。
- 单键工具不能拉 LN；长条工具点按放 Tap、拖动放 LN，支持两个方向。
- 单键/长条工具左键在旧头位置是覆盖绘制，不是移动。原移动交互在中键；这两工具的中键不再平移轨道，其他工具保留相应导航。
- 鼠标预览按键的底部中点对应指针尖部，按下期间也保持显示，避免“粘滞”。
- LN 尾图形整个尾盖可触发长度调整，但不能把过大的尾拖动命中范围用于右键删除。
- 拖动创建 LN/调整尾部时连续跟手，松手再吸附最近网格。
- 绘制吸附判断使用实际鼠标时间，不按图形中心位移；编辑操作不得改变原本非网格上的精确暂停位置。
- 右击目标属于多选区时删整组选区；目标不在选区时删指针目标，不能因为刚放的按键仍选中而误删它。空白右击取消选择。
- **框选和 LN 拖动仅在明确拖出编辑区后边缘滚动；画面内靠底部不能自动跑时间。** 播放中框选起点固定在音乐时间，选区随播放继续扩大。
- 背板空白右击才显示新增轨道；菜单出现后第一次左击只关闭菜单，不能顺带放键。
- 空白单击释放输入焦点。输入文本时不应被普通编辑快捷键打断。

### 导航、快捷键和网格

- 普通编辑 Space 播放/暂停，不能变成“点击当前焦点按钮”。排列起终点键改为 Enter，不能再次用 Space。
- 当前默认基本工具 Q/W/E/R；快捷键可自定义，已有用户设置优先。
- F5 试玩；Ctrl+H 左右镜像；Ctrl+G 时间翻转；Ctrl+B 在当前位置加跳转标记。
- 暂停时滚轮按网格导航；播放时固定歌曲时间步长（当前约 500ms/滚轮格）、与网格无关，并立即继续播放。背板空白也生效。
- Ctrl+滚轮调常用网格；Shift+滚轮调编辑倍速；Alt+滚轮调缩放，同步草稿轨也必须正确路由。
- 1/5、1/7、1/9、1/10、1/64 只在顶部下拉手动选，不进入快捷滚轮/调整卡片的常用序列。
- osu 网格记法是音乐实际切分的四倍，例如 osu 1/4 = general 1/16；另有 general/Malody style 记法选择。
- 编辑倍速含 0.1x，不是 0.2x；试玩预设不含 0.2x、2x。编辑/试玩倍率分开持久化，支持 0.5x 写图、0.75x 测图。
- 倍速默认保音高。滑条拖动连续变化、点击映射常用刻度，网格滑条离散。恢复默认按钮只给全局设置等需要的地方，不给日常网格等选项堆按钮。

### 中心对称和拉伸

- 中心对称：点击功能→点击开始→点击结束，两个时间吸附网格，生成可调整时间/轨道范围的框；永远实时对称，无额外启用开关。
- 对称框的时间上下边仅选择工具可拖，不阻碍单键/LN在边缘放置。
- 放置/覆盖/删除/擦除/剪切同步；同位置不重复加键。
- 同列相交或相接的源 LN/反射 LN 应合成连续长条；不同列/不相交保持两份。
- 0.1.36：调整源 LN 尾部时同步调整反射对象的头部，双侧预览、松手吸附、单次撤销，保留 ID 和音效字段；自对称合并 LN 也有处理。
- 拉伸先选键再点功能；框内拖动平移时间/列，上下边改变时间范围，内部 Tap 和 LN 两端按比例变换；预览不写文档，松手一次提交，Esc取消；不得产生负长度/归零 LN。

## 6. 曲库、多轨、排列和版本的实际使用流

详细操作文档：MAPPER-WORKFLOW.md、LIBRARY-VERSIONS.md、WORKBENCH-TRACKS.md。

### 曲库

- 左上角品牌图标进入曲库。左栏是个人创作区（几十/上百图），右栏是选定只读 Songs（可几万图），独立搜索。
- 进入曲库＝只读持久化索引缓存，不扫描磁盘；刷新目录只能由用户点「刷新索引」或明确操作触发。缓存可能不是最新，状态行/头部必须写清“只读缓存”和上次刷新时间；不要把“后台核对”又加回来。
- 左栏包含旧版已收录的个人工作区，不能只用最新包的 Maps 路径筛选，否则又显示 0 张。
- 左栏双击难度编辑；右栏双击只读查看原件，不自动克隆。需要编辑时显式“克隆并编辑”，复制并校验整套资源到工作区。
- 只读必须允许播放、定位、框选、复制、提取排列、切换标签、关闭标签/程序；只阻止真正修改/写原件。曾因元数据侧栏的隐式“应用”导致无法退出，已修复，不要重引入。
- SQLite/FTS、头缓存、后台更新/搜索、文件监听、虚拟化行已经实现；没有 Mode 的 .osu 默认 standard，应排除。watcher 只做增量（保存/新建/删除）；overflow 或 >256 路径只提示“点刷新索引”，不得自动全量扫描。刷新时间存 index_metadata。
- 启动先显示持久化快照，后台核对，返回页面不重复完整扫描。首次添加 Songs 仍需扫描，不能承诺冷启动无扫描“秒开”。
- 真实索引隔离副本 46,599 条：首快照约 3.2s、热约 2.0s；20k 合成 UI 约15可见行。不是整程序启动耗时。
- 个人收藏/标签/状态（例如未发布）/备注、批量整理、组合/排除搜索已有；不写 .osu。

### 多轨

- 主轨与草稿、排列、参考等共享底板和横向滚动。底部滚动范围允许轨道全移出画面，便于自由摆主轨。
- 新增轨道只在右击的左右位置插入，保持已有主轨的屏幕位置，不重排整面板。
- 持久化轨道列表/顺序/宽度/隐藏/独立缩放/同步/播放位置/底板位置等。
- 全局 density/diff 固定左侧、可隐藏，通过顶部视图恢复；新增的是随主轨滚动缩放的局部轨，不能复用全曲稀疏显示逻辑。当前“难度”有实验性成分，不能冒称已完成官方7K算法。
- 参考谱面从曲库索引挑选，不默认弹文件选择；参考轨带自己的密度时间索引、毫秒或分秒定位；独立定位取消同步，重选同步回共享歌曲时间。
- 草稿放入主轨保持原时间，不以播放位置作粘贴锚点；有可撤销“清空”。
- 草稿/排列继承主轨 Timing。0.1.36 同时投影当前文档和 undo/redo 快照，避免主轨变速/offset 后派生轨失配；参考轨保留独立 Timing。

### 排列

- 典型流程：只读 Songs 查看→框选→底板新增排列轨→轨内“加入主轨选区”→整理→命名保存（有默认名称）。多个排列轨必须把选区送入**点击的轨道**，不是最近/最后聚焦轨。
- 排列存相对 musical beat 和相对列，不依赖歌曲音频/绝对毫秒，才可跨 BPM 使用；原图不修改。
- 左栏排列库搜索/形状列表→取出鼠标虚影→横向定位→点击放置；Ctrl+滚轮多级缩放，Enter 标起点/终点，Esc/右击取消，整次一次撤销。
- 拷贝 .osu HitObjects 文本和 Ctrl+V 鼠标虚影已有；Note 栏文本“应用”按代码原始时间导入，不与属性应用混淆。
- 排列库和版本 diff 的形状是同宽实心矩形，Tap无装饰，LN不是细身加头尾的编辑皮肤。
- 0.1.36 修复整个提取/JSON/伸缩/粘贴链：native .osu 整数ms量化在已知常见拍点1ms内保守恢复，统一 BigInteger 分数运算，先约分再判断存储精度上限。不能简单每步转 double 后硬吸当前网格。
- **旧版已经保存的偏格排列 JSON 未自动改写**，它缺少足够原始 Timing 来可靠还原；从源谱重新收录。不能声称旧资料已全部修复，也不能擅自批量吸附破坏有意偏移。
- 暂无无歌曲独立排列文档、库内重命名/删除/分享完整界面；JSON格式可读、损坏文件保留跳过。

### 事务迁移与版本

- Songs ↔ 创作目录：整套预览、默认复制、唯一目标目录、SHA256校验、持久化事务、可选移动/备份和回退；不能覆盖合并同名目录。
- 复制出 Songs 可在游戏运行时进行；移动源或写回 Songs 仍需退出游戏并预览/确认。回退遇到新编辑应停止，不抹掉它。
- `Versions/<身份>.git` 是内置 libgit2 的裸 Git 文档/资源快照；命名版本、保存自动快照、去重、音符/Timing/元数据 diff、恢复一次可撤销、独立导出旧资源已有。
- 标签备注不进入 .osu；历史不自动推作品到远端。当前 Git 历史不是逐 command journal，也没有分支合并/远程同步/合作写谱。

## 7. 音频和试玩：本轮最需要谨慎接续的技术点

### 编辑播放 0.1.36 / 0.1.37 的修复及边界

先读 DEVELOPMENT-HANDOFF.md 最前面的 0.1.36，以及 Audio/ 下这些文件：

- `PreparedMusicPipeline.cs` / `TransientAnchoredStretchSampleProvider.cs` / `SourceSilenceGuard.cs`
- `WindowsAudioDeviceEngine.cs` / `WindowsAudioPlaybackService.cs`
- `SourceTimedSampleProvider.cs` / `WindowsKeySoundPlaybackCoordinator.cs` / `WindowsKeySoundService.cs`
- `ViewModels/WindowsAudioTransportViewModel.cs`

过去用户反复报告慢放震荡、音乐早于频谱且早于按键音，尤其 0.1x。不能只加全局10ms经验补偿：已经发现不同层的实际问题。

1. 原 SoundTouch/WSOLA 会从未来 grain 提前借到瞬态；有非静音底音时，仅静音门限无法保护。合成实际 WASAPI 输出前 PCM 中，0.1x 本应1000ms的攻击音出现在约791ms。
2. 旧按键音按估算设备队列延迟排程，另有约15–30ms级误差。
3. 倍速/seek准备占住操作gate时旧音乐仍播，但按键音因gate忙被拒绝且事件已消费。
4. DSP flush补零会在极慢短音的有效尾部被取用，造成断续静音；短读也有尾音丢失和游标偏移问题。

现在的处理：

- 1x旁路；保音高变速仍用SoundTouch，但以三频带能量攻击/静音释放切分source，每段输出以绝对sample预算结束，防止借入下一攻击和逐段累计舍入。
- 分段尾部补有界的过去音频上下文，避免flush零进入有效尾音；源静音保护保留；旧经验14ms搜索padding移除。使用有界流式缓冲，没有全曲PCM，也没有新native库依赖。
- 按键音携带绝对source毫秒，经过采样率转换与epoch guard后，由mixer的实际sample游标/实际rate决定延迟。
- 后台prepare期间旧音乐/旧epoch继续排程，实际source commit前才失效旧epoch；异常路径恢复commit标志。
- 非EOF短/零读有界重试，source-timed音效只随真实music PCM推进；保留末段partial PCM与显式终止静音。

0.1.37 追加（针对“非 1x 有明显噪声”）：瞬时 SoundTouch 空读改为有界重试（不再把 flush 中段当 EOF 补零）；分段的实际数据短于预算时用 3ms 余弦衰减补尾（不再瞬时归零）；每段接缝 3ms 升余弦淡出/淡入，淡入等到第一个真实样本（避免 SoundTouch 前导静音后的突跳）；攻击边界最小间隔 80ms，避免 8ms 超短段和重复 attack 检测，但 release 边界仍立即切开（否则短 burst 会被合并出 10ms 空洞）；输出增益的 SourceSilenceGuard 已从 PreparedMusicPipeline 停用——它的按样本增益台阶本身就是爆音来源，攻击边界已由分段保证。合成鼓点＋底音九倍率最大采样台阶 0.006–0.014（改前 0.30）；数字静音仍残 0.082/音符；持续单音优于未分段参考。真实 MP3 起音 206.68–207.70ms。不要把这组数字说成真实设备听感验收。

已验证：十倍率 mono/stereo、exact相同双声道/单侧静音、块大小一致性、长度/EOF/峰值、谐波/非谐波纯度、短脉冲连续性；实际WASAPI输出前PCM的1/.5/.25/.1合成music-key差约0.15ms；完整自定义44.1k按键音→48k音乐输出路径也通过。

真实 Albemuth MP3 只读起音/seek测试：raw约207.94ms；五倍率伸缩起音207.53～207.76ms（**source时间**），1x约207.94ms；原SHA256不变。

**这些不是扬声器/耳机声学延迟、屏幕Present或任意歌曲主观音质测试。0.1.36还没有用户实机反馈，不能先宣布所有音画问题根治。** 全局音画校准独立于谱面offset且持久化；不要重置用户校准。不同设备/音乐、极慢音质、长时间切速、复杂瞬态仍需验收。

曾试验外部native伸缩实现但已拒绝，没有产品引用/分发；artifacts下失败实验/旧日志不是最终实现。不要把旧实验DLL顺手打包进产品。

### 官方试玩

- `SimpleManiaEditor.OsuHost` 用真正官方 lazer Player/mania/skin/设置/BASS，独立SDL/GPU窗口；`PlaytestBridge` 与编辑器交接工作副本/起点/IPC。不要重新启用旧自制skin渲染。
- 常驻后台预热，退出后隐藏、静音、低帧运行，热启动复用；首次shader/皮肤/资源仍需初始化，不承诺完全零延迟。
- 进入试玩立即中断编辑音乐和按键音并锁编辑；退出保持编辑暂停，不能两重声音。
- F5/顶部单一试玩入口，从当前位置；独立试玩倍率、默认保音高。
- Shift应等于原官方Esc暂停面板；Esc直接返回编辑器。加载期间按键消费，释放后不带入；Windows默认SDL2，历史Space提示音还需真实验收。
- 皮肤/按键/落速用官方设置，从右侧设置栏直接进入；支持只读导入osu cfg、皮肤目录和.osk。选择后重入持久化，不用旧设置覆盖官方状态。
- 固定编辑器自己的宿主storage，忽略standalone storage.ini重定向，禁独立客户端目录迁移；阻止绝对路径/traversal/junction写出。原素材和原游戏目录只读。

## 8. 资料位置：备份要覆盖哪些东西

具体以 `AvaloniaAppPaths` 和用户保存的路径为准，不能仅凭下面默认值就迁移/清空。

| 资料 | 默认/实现位置与要求 |
| --- | --- |
| 创作谱面与资源 | 包体旁 Maps，已设置的创作目录优先；旧 dist 的 LumaChart Workspace 等确有作品，需保留 |
| 开发体验配置 | `%LOCALAPPDATA%\SimpleManiaEditor\Preview`，有覆盖环境变量时另计 |
| 干净分发配置 | Distribution配置；不能与Preview互相覆盖 |
| 编辑缓存/恢复/轨道工作台 | PersistentEditingRoot及desktop-workspace等；通过路径类和配置发现，不能归为可删缓存 |
| 索引、标签备注身份 | `mapper-library.sqlite3`及WAL/SHM，个人可读备份 `library-personal.json`；关闭连接后用SQLite正确备份 |
| 排列库 | 资料根目录 `Patterns/*.json` |
| 版本 | 资料根目录 `Versions/<身份>.git`，含资源对象 |
| 迁移 | `Transfers/`事务JSON和源备份，不自动删 |
| 自动备份/设置/恢复 | 对应资料目录完整保存，备份不只带.osu |
| 官方试玩 | 默认Preview下 `osu-host`，含官方配置/realm/files/皮肤/偏好；实际以启动request/data root为准 |
| 诊断与合成资料 | artifacts及独立Temp，可能含本机路径，默认不上传；清理仍应按规则识别 |

隔离测试常用环境变量：`LUMACHART_AVALONIA_LOCAL_DATA_ROOT`、`LUMACHART_AVALONIA_EDITING_ROOT`、`LUMACHART_AVALONIA_WORKSPACE_ROOT`。在测试子进程里设置，不要把真实用户默认设置永久改到Temp。

## 9. 尚未定位的真实 osu! 问题，不能漏交接

用户多次报告运行/测试SME之后打开stable会触发修复界面；另外在官方editor编辑时出现「无法创建新的堆栈防护页面」，并确认后者发生时SME与试玩已完全退出。两者可能是不同问题。

- **尚未确定根因，也没有证据可宣布与SME无关或已修好。**
- 已做整安装树监听以及190个程序/配置/数据库/所用皮肤文件的大小/hash/时间/属性对照：那几次未观察到变化；这不能外推所有路径安全。
- 已修复的确凿隔离缺口是官方storage.ini目录重定向/迁移入口，不代表已证明它导致用户stable故障。
- Windows较早的AccessViolation记录、NoUpdate日志、采样时内存状态均不等于截图的栈溢出根因。不得以猜测要求重装/改页文件/修数据库。
- `Capture-OsuDiagnostics.cmd` / `scripts/Collect-OsuDiagnostics.ps1` 可做只读进程/提交内存/事件/更新日志采集。详见MAINTENANCE.md；不自动挂调试器、改WER或结束游戏。进一步转储/系统变更要按具体授权处理。
- 不要再回问已经知道的信息：栈错误时SME和试玩已退出；并行运行时用户常见修复；稳定复现步骤/精确修复提示仍未知。

## 10. 构建和测试，复制即可开始

以下是这台机器上已使用的路径；换机器先验证实际位置。构建SDK和运行时不是同一套：

```powershell
Set-Location 'E:\Files\Developing\SimpleManiaEditor'
$env:NUGET_PACKAGES = 'D:\CodexBuildTools\SimpleManiaEditor\nuget'
$env:DOTNET_CLI_HOME = 'D:\CodexBuildTools\SimpleManiaEditor\cli'
$buildDotnet = 'D:\CodexBuildTools\SimpleManiaEditor\sdk\dotnet.exe'
$runtimeDotnet = 'C:\Program Files\dotnet\dotnet.exe'

& $buildDotnet build src/LumaChart.Specs/LumaChart.Specs.csproj -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Core build failed' }
& $runtimeDotnet src/LumaChart.Specs/bin/Release/net8.0/LumaChart.Specs.dll
if ($LASTEXITCODE -ne 0) { throw 'Core specs failed' }

& $buildDotnet build tests/SimpleManiaEditor.UiSmoke/SimpleManiaEditor.UiSmoke.csproj -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'UI build failed' }
$uiSpecs = 'tests/SimpleManiaEditor.UiSmoke/bin/Release/net8.0-windows/SimpleManiaEditor.UiSmoke.dll'
& $runtimeDotnet $uiSpecs --mapper-036
if ($LASTEXITCODE -ne 0) { throw 'Mapper specs failed' }

# 每次按改动范围选相关suite，不必无条件全部重复
# --mapper-035 / --mapper-034 / --mapper-033 / --mapper-031
# --regression-030 / --audio-gate-036 / --audio-026
# --library-037 / --tabs-037 / --audio-noise-037（0.1.37 新增）
# --audio-alignment-036 会使用实际音频输出，可能听到合成音
# --audio-onsets-036 快速单独跑既有起音回归
# 并行 agent 或需要串行构建时用 scripts/Agent-Verify.ps1 -TestArgs <flag> -Name <名字>
# （跨进程锁包住 build+run；不要手写并行 dotnet build，会抢 bin/obj）

& scripts/Build-Local.ps1 -Dotnet $buildDotnet
```

- `global.json` 固定SDK10.0.401。不要直接用只有.NET8 runtime的C盘dotnet去build；D盘SDK若没有.NET8 runtime，用它 `dotnet run` 也可能因缺framework失败，编译后用上面的C盘runtime运行net8测试。
- net10官方宿主运行/测试使用D盘dotnet或self-contained已构建EXE，不用旧C盘runtime。
- `scripts/Test.ps1` 提供基础Core + 默认headless UI；它不代替本轮专门suite，不要因为脚本过了就遗漏audio/mapper针对性测试。
- 不同时build到同一输出，不在测试DLL仍运行时重build它，避免文件锁和互相覆盖。性能测量避免和重DSP测试竞争CPU。
- .NET Framework/旧Avalonia.Specs有历史测试/实现，可用于查语义，但不能把未执行的整个旧套件称为本轮全绿。
- Build-Local建立新目录，publish编辑器/host，自包含runtime检查和固定storage验证通过后才改入口。不覆盖用户旧包。
- 修改版本时同时检查产品常量、csproj、README/FEATURES/BACKLOG；不要创建RELEASE文档暗示已发布。

### 上次实际通过的结果和日志

| 验证 | 结果/日志 |
| --- | --- |
| 核心 | 59/59，`artifacts/core-036-tests.txt` |
| .36新交互/精度 | `mapper-036-final036.txt`，画面外框选、对称拖尾真实指针、继承Timing/历史、多排列轨目标、跨BPM/JSON/2x/锚点精度 |
| 已有交互回归 | `mapper-035-final036.txt`、`mapper-034-final036.txt`、`mapper-033-rerun036.txt` |
| 速率/同步/绘制回归 | `regression-030-final036.txt`，44.1/48k、慢decoder、连续输出、原生alias缓冲、对称覆盖 |
| 音频质量 | `audio-036-final-quality.txt`，--audio-026同时跑026/027；不是那些带spectral/window名的失败实验日志 |
| 真正输出路径对齐 | `audio-036-final-alignment.txt`，WASAPI endpoint前PCM；不是物理loopback/声学测量 |
| gate/commit/短读 | `audio-gate-036-tests.txt`，native mixer+decoder barrier、epoch、失败恢复、短读/EOF |
| 实际MP3起音 | `audio-036-albemuth.txt`，原文件SHA256不变 |
| 本地自包含包 | `build-local-036.txt`及当前包的smoke.txt/storage.txt |
| 0.1.37 曲库/标签页 | `agent-finallibrary-037-*`、`agent-finaltabs-037-*`（缓存优先进入、手动刷新、36 标签可达、截图在临时 fixture） |
| 0.1.37 音频 | `agent-lead-audio-*`（九倍率接缝台阶矩阵）、`agent-finalaudio-026-*`、`agent-finalaudio-gate-036-*`、真实 MP3 只读起音 PASS |
| 0.1.37 包 | dist/SimpleManiaEditor-0.1.37-Local-20261020-212306，smoke 0.1.37 / storage PASS（由恢复后的源码重建） |

一次.33测试在并行DSP压力下Space播放等待超时，原样隔离重跑通过；两个日志均保留，不能隐去第一次失败，也不能未经复现就将其定性成已修复的产品bug。最后包的smoke是0.1.36，宿主固定storage检查通过。

## 11. 代码导航，避免在巨型文件里盲改

路径均相对仓库根目录。

| 区域 | 优先看 |
| --- | --- |
| 核心模型/原生格式 | `src/LumaChart.Core/Models/BeatPosition.cs`、`BeatArithmetic.cs`、`IO/OsuManiaSerializer.cs`、`OsuBeatCalibrator.cs`、`OsuSourceEnvelope`、TimingMap |
| 会话/操作/变换 | `src/LumaChart.Application/Editing/EditorSession.cs`、BasicChartEditingService、CenterSymmetryService、SymmetricHoldTailResizeCommand、NoteStretchService、InheritedTrackTimingService、PatternLibrary |
| .osu安全/工作区 | Application的Workspaces/DesktopDocumentWorkspace、Avalonia的Services/AvaloniaWorkspaceController、`OsuFileSafetyPolicy`（搜索定义） |
| 曲库/搜索/迁移/历史 | Application/Libraries、MapperLibraryStore、DesktopChartLibraryService、MapsetTransferService及版本实现；MainWindow.LibraryBrowser/MapperLibrary |
| 指针和绘制 | Avalonia/Controls/TimelineSurface.cs及Paste/Symmetry/HoldTailSymmetry/Stretch等partial；Rendering/TimelineEdgeScroll |
| 工作台布局/资料 | MainWindow.TrackBoard/Tracks/Patterns/Stretch，Infrastructure/EditorTrackWorkspace/AvaloniaAppPaths |
| 设置/输入/帧循环 | MainWindow.cs及WorkbenchControls/SimpleMania等partial，ViewModels和Editing快捷键策略 |
| 波形频谱/音频 | Avalonia/Audio目录，WindowsAudioTransportViewModel；编辑DSP与官方试玩BASS是两条不同路径 |
| 曲库缓存/标签条 | `MainWindow.LibraryBrowser.cs`＋`ViewModels/MainWindowViewModel.LibraryUpdates.cs`（缓存载入/刷新时间/增量）; `MainWindow.DocumentTabs.cs`（标签条滚动/箭头/自动入视） |
| 官方试玩 | `src/SimpleManiaEditor.OsuHost`、`src/SimpleManiaEditor.PlaytestBridge`及Avalonia启动/常驻监督相关实现 |
| 测试 | `src/LumaChart.Specs`、`tests/SimpleManiaEditor.UiSmoke`，维护用PowerShell测试见tests目录 |

MainWindow.cs/TimelineSurface.cs已有较大体积，新功能优先清晰服务/partial边界，避免继续堆事件回调；复用同一音符轨组件、时间变换和命令语义，不能再创建一套行为相近但不同步的草稿/参考实现。原生osu源字段保留、内部.mc缓存与UI模型分工要先看代码，不要删除看似无用的UnknownData。

## 12. 未完成目标及接手后的优先级

完整列表以FEATURES.md / BACKLOG.md为准；最初需求在PLAN.md / LUMA_ADOPTION.md / 早期会话.md，历史方案不能覆盖后来的用户决定。

**优先等用户对0.1.36真实反馈并修复回归，不应因为下面有建议就自动开发全部。**

1. 真实设备/歌曲的慢放音质、音画/按键音、连续切速与跳转；0.1.36合成结果不是最终验收。
2. 流畅度/帧率：已有高刷新设置与诊断，但稳定180fps、1% low180和“2000帧”没有完成证明；早期4000键绘制约144fps，不等于GPU Present。
3. 原版osu修复/栈错误调查：未定位，保留只读诊断边界。
4. 排列库管理（重命名、删除、分享）、无歌曲独立排列文档，旧不精确排列的有依据恢复方案。
5. 官方编辑器补齐：更多Metadata/General/Editor属性编辑、原生书签/break/视频/故事板、资源/导出预检、复杂Timing继承/重复事件/批量编辑、完整hitsound bank/index/additions/LN采样。
6. 7K正式难度：官方strain与Sunny接入、逐键局部难度/放置预估/热力图；不能将上游4K Rework或实验性负荷包装成正式7K难度。
7. 变速BPM/offset估计、倍率选择/置信度；新建导入音乐不自动测，用户主动启动。需区分文件offset和全局设备校准。
8. command journal、逐操作回溯与故障恢复；再考虑协作广播/权限/冲突，不等同现有Git文档快照。
9. 最小RC模式、完整循环平移/转换、可编辑音频轨、多版本并排编辑、编辑器外部skin/布局方案等。
10. AI元数据agent、自定义API、general check/mapsetverifier、局域网合作仍属后续/TODO。

### 新 Agent 的第一步

读AGENTS和本文件→盘点保留本地未提交内容→看FEATURES/BACKLOG与最新技术交接→确认Start-Editor指向0.1.37→根据用户接下来的具体反馈复现→做范围清楚的修改和针对性测试→Build-Local更新入口→说明改动、证据与仍未验证的真实环境问题。

交接无需先清理/重构/重装/重建一切，也不需要让用户重新复述已经记录的操作要求。
