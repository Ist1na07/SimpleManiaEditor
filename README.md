# SimpleManiaEditor

面向 osu!mania mapper、优先优化 7K 工作流的本地谱面编辑器。

当前为 0.1.31 Windows 体验版，编辑器基于 LumaChart Studio 的 C#/.NET + Avalonia 客户端；试玩直接运行官方 osu! 游戏包和 mania 规则集。默认 osu!mania 编辑操作与原生 7K 新建，保留原始 `.osu` 资料，离线可用。

[下载 Windows 分发包](https://github.com/Ist1na07/SimpleManiaEditor/releases) · [问题反馈](https://github.com/Ist1na07/SimpleManiaEditor/issues)

下载后完整解压，双击包体根目录 `Start-Editor.cmd`。创作谱面默认保存在包旁 `Maps`，应用设置保存在独立用户目录。开发目录中关闭旧窗口后双击 `Start-Editor.cmd` 启动，`Start-Demo.cmd` 保留旧版 7K 演示入口。干净分发包位于 `dist/SimpleManiaEditor-0.1.31-Distribution/`，包含编辑器和官方游戏宿主各自的运行时。

- [0.1.31 曲库与版本历史](docs/RELEASE-0.1.31.md)：持久化索引、标签备注组合搜索、双向事务迁移、原生 Git 和音符可视化 diff。
- [曲库整理、迁移和版本工作流](docs/LIBRARY-VERSIONS.md)。
- [0.1.30 切速连续性与同步编辑](docs/RELEASE-0.1.30.md)：实际倍速时钟、缓冲切换、中心对称覆盖和主轨网格导航。
- [0.1.29 曲库快捷编辑与 GitHub 更新](docs/RELEASE-0.1.29.md)：双击进入编辑、项目关于/反馈和 Release 检查。
- [0.1.28 小屏界面、曲库与文件隔离](docs/RELEASE-0.1.28.md)：自动/手动 UI 大小、包体旁创作目录、多目录只读曲库与原件保护。
- [使用说明](docs/QUICKSTART.md)：启动、键鼠操作、保存与恢复。
- [0.1.27 慢放时间对齐、频谱与界面](docs/RELEASE-0.1.27.md)：修复 Albemuth 起音提前、缩短慢放粒度、保留反相声道频谱、全曲时间轴和直接设置分类。
- [当前功能矩阵](docs/FEATURES.md)。
- [0.1.26 倍速音质与播放优化](docs/RELEASE-0.1.26.md)：统一保音高处理、各倍速预热与无停机跳转、多轨轻量同步、可见音频状态和稳定的倍速选项。
- [0.1.25 校准器与虚影粘贴](docs/RELEASE-0.1.25.md)：低速播放优化、校准跳转修复、音画校准器、虚影粘贴与草稿时间对齐。
- [0.1.21 osu 最小模式与操作优化](docs/RELEASE-0.1.21.md)：覆盖绘制、连续滑块、镜像/翻转/书签快捷键、工具栏整理和导出参数统一。
- [0.1.20 编辑交互与文本代码](docs/RELEASE-0.1.20.md)：精确暂停位置、中心对称框、多选右键删除、系统剪贴板音符分享。
- [0.1.19 中心对车](docs/RELEASE-0.1.19.md)：范围与参与列、单键/LN 实时预览与成对放置、选区补齐、一次撤销和独立工具设置。
- [0.1.18 网格与播放性能](docs/RELEASE-0.1.18.md)：默认导入校准、可撤销的批量吸附、空白取消输入焦点、OpenGL 渲染与绘制/慢帧诊断。
- [0.1.17 恢复与连贯播放](docs/RELEASE-0.1.17.md)：checkpoint 校验、旧版恢复保护、启动布局、显眼的全局栏恢复入口和播放回跳修复。
- [0.1.14 交互与选项控制](docs/RELEASE-0.1.14.md)：试玩 Shift/Esc、自由平移、默认值恢复与 OD/HP 编辑。
- [0.1.13 试玩窗口焦点](docs/RELEASE-0.1.13.md)：点击试玩后自动切换到游戏窗口。
- [0.1.12 试玩预热与设置](docs/RELEASE-0.1.12.md)：常驻游戏、资源缓存、快速进入及右侧设置入口。
- [0.1.11 官方试玩宿主](docs/RELEASE-0.1.11.md)：右下角可移动调整卡片、官方皮肤渲染与设置、独立游戏数据。
- [0.1.10 皮肤工作流修复](docs/RELEASE-0.1.10.md)：皮肤库与选择预览、重进持久化、官方格式兼容、工具栏合并。
- [0.1.7 绘制工具](docs/RELEASE-0.1.7.md)：绘制与移动分离、鼠标按键预览、LN 尾部重叠校验。
- [0.1.6 交互修正](docs/RELEASE-0.1.6.md)：最近格线吸附、底边时间锚点、空白取消选择与底板滚轮、常驻横向滚动条。
- [0.1.5 底板与精确交互](docs/RELEASE-0.1.5.md)：Songs 隔离、保音高、同等轨道和同步投影。
- [开发交接摘要](docs/DEVELOPMENT-HANDOFF.md)：压缩后继续开发所需的项目状态与构建方法。
- [0.1.4 工作台与轨道](docs/RELEASE-0.1.4.md)：固定命令、网格记法、刻度滑条、分类调整及独立轨道。
- [轨道操作和存储](docs/WORKBENCH-TRACKS.md)：五类轨道、复制/同步、osu/general 网格映射、恢复边界。
- [0.1.3 工作台更新](docs/RELEASE-0.1.3.md)：固定工具栏、osu 皮肤、空格与滚轮、网格/速度滑条和一键试玩。
- [未完成功能清单](docs/BACKLOG.md)：原始需求的当前状态、剩余工作与下一阶段顺序。
- [0.1.2 跳转优化](docs/RELEASE-0.1.2.md)：策略瓶颈、设备复用、后台 DSP 准备与实测范围。
- [0.1.1 修复说明](docs/RELEASE-0.1.1.md)：滚轮、网格、帧率、反向 LN、精确删除与启停音频。
- [0.1 发布说明](docs/RELEASE-0.1.md)：完成范围、验证与待完成事项。

- [当前改造方案与审计](docs/LUMA_ADOPTION.md)：Luma 的复用边界、实测结果、osu 交互与数据兼容改造、修订后的路线。
- [原完整规划](docs/PLAN.md)：需求、数据与功能语义基线；其中 Python/Web 技术方案已被当前改造方案替代。
- [验收与测试计划](docs/ACCEPTANCE.md)：自动化测试、性能基准、数据恢复与真实环境验收。
- [原始需求](初步设计.txt)：保留用户的初始功能清单。

开发分工：Codex 负责代码、自动化测试、构建与开发文档；用户把控功能、编辑体验和真实 osu! 环境验收。每个阶段交付可运行版本及验证记录。


## 构建与测试

Windows x64，安装 .NET 10 SDK（编辑器目标 .NET 8，官方试玩宿主目标 .NET 10）。

```powershell
./scripts/Test.ps1
./scripts/Package-Distribution.ps1
```

分发包包含所需运行时，不需要接收者安装 .NET。脚本从全新发布目录打包，已有输出目录拒绝覆盖。新克隆的源码需要先构建分发包，根目录启动入口才能运行。

维护者在测试、打包和推送 `main` 后，可运行 `scripts/Publish-GitHubRelease.ps1` 发布本版本。脚本使用已有 GitHub Git 登录，先上传到草稿并核验下载包 SHA256，再公开 Release；已发布版本拒绝覆盖。

## 许可证

本项目源代码采用 MIT，署名 **Ist1na**，保留 LumaChart 上游版权声明，见 [LICENSE](LICENSE)。[上游](https://github.com/cux-rn/LumaChartStudio-Client) 是本项目的基础。

第三方组件各自的许可证继续有效，见 [第三方声明](src/THIRD-PARTY-NOTICES.md)。官方游戏资源包含 CC BY-NC 等条款，BASS/FFmpeg 等有独立条件；源代码的 MIT 不代表整个分发包可任意商用。
