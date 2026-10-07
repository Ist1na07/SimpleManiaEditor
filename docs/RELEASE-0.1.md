# 0.1.0：首个 Windows 体验版

这是基于 Luma 的第一个 osu!mania 改造增量，覆盖打开、新建 7K、tap/LN、选择/拖动、复制粘贴、撤销重做、试听与原生保存的体验流程。完整原需求仍按后续阶段推进。

交付日期：2026-10-08（Asia/Taipei）。

## 本次实现

- 默认 osu!mania 工具键 1/2/3、Ctrl 多选/框选、拖动保持批选择、右键删除、Alt 滚轮缩放。
- 支持切换并持久化 osu!mania / Luma legacy 方案，快捷键仍可自定义。
- 默认新建和音频创作为原生 7K `.osu`，普通打开不再默认弹出 Malody 网格校准选择。
- 保留 `.osu` 原始文本与音符、timing 属性；编辑后保留 ID、Tags、Source、书签、事件、声音继承和未知资料。
- Recovery 工作副本保留原生 osu envelope 和 OD/HP；修改 timing 时维持未移动音符的绝对时间。
- 检测外部文件修改；覆盖保存前备份上一份 `.osu`；外部改动与未保存工作并存时保留脏工作副本。
- 约 2 秒恢复 checkpoint，与已有自动备份/保存流程兼容。
- 独立应用、数据目录与单实例范围；默认离线，移除上游生产账号/更新初始化。
- 自包含 Windows x64 发布包、生成音频的 7K 演示谱面、一键启动入口。

## 本次验证

- 客户端/领域规格测试 **49/49 通过**，包含新的保真/恢复/备份/外部修改回归。
- Avalonia headless：真实控件鼠标放键/LN、Ctrl 多选、Alt 缩放、单事务撤销/重做通过。
- 完整窗口：默认 7K 原生新建、1/2/3 路由、鼠标编辑、复制粘贴、撤销重做、checkpoint、保存/重载复核通过。
- 实际渲染首页与编辑器截图，检查中文、主题、7K 轨道及应用识别。
- Windows Release 构建零警告、零错误；自包含 payload 的 Skia 与 7K LN 读写 smoke test 通过。
- 真实音频设备、真实 osu! 客户端与完整上游私有 fixture UI suite 尚未执行；不将 headless 测试当作硬件延迟验收。

## 尚未完成

- 完整持久化 command 历史、版本树与可视化 diff。
- 多种独立轨道、排列库/文本分享、完整大 Songs 库增量索引与迁移。
- 官方判定/计分与完整 hitsound 编辑/试听继承；内置试玩目前沿用上游。
- 7K Sunny、官方 strain、实时假设难度、复杂变速 BPM 质量验收。
- 0.1x～2x 等完整倍率与独立 pitch；目前播放沿用上游 0.2x～1x 范围。
- 官方 mania skin 全兼容、完整 Design/故事板编辑与预览。
- 视频/复杂故事板资源的跨目录另存/迁移与完整 `.osz` 资源清单；当前先使用整首歌曲的目录副本。
- AI 与联网协作等原 TODO 功能。

源资料保留不表示这些事件都已能在 UI 中编辑/预览；改动 timing 的导出将以当前模型排序写回，极端重复 timing/不规范对象仍需进一步兼容审计。真实 osu! 和设备延迟由用户体验确认。

## 开发与构建

SDK 基线：.NET 10.0.401；运行目标 net8.0-windows。第三方依赖保持显式版本。

```powershell
./scripts/Test.ps1
./scripts/Publish.ps1 -Zip
```

若 SDK 与测试运行时在不同位置，可传 `-Dotnet <SDK的dotnet.exe>` 和测试脚本的 `-RuntimeDotnet <运行时的dotnet.exe>`。源码与 SDK/依赖缓存、构建物、用户资料分开管理。

上游来源、版权与第三方许可见 `docs/UPSTREAM.md` 和发布包内 notices。发布版本保持全部依赖许可文本。
