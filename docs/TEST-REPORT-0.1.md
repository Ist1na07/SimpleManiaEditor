# 0.1.0 验证记录

日期：2026-10-08（Asia/Taipei）。环境：Windows x64，SDK 10.0.401，Avalonia 12.1.0；自包含运行时为 .NET 8.0.31。

## 通过的检查

| 检查 | 结果 |
| --- | --- |
| 客户端、领域、工作区与同步规格 | 49/49，退出码 0 |
| 保真回归 | UTF-8/BOM/LF/CRLF、未知字段与事件、音符移动/LN、声音继承、BPM/offset 绝对时间、4K→7K 列转换、MC 恢复、备份、外部修改保护 |
| Headless 时间轴输入 | 左键 tap/LN、Ctrl 追加与取消、Alt 缩放、单手势撤销/重做 |
| Headless 完整窗口 | 原生 7K 新建、真实快捷键路由、鼠标编辑、Ctrl+C/V/Z/Y、恢复 checkpoint、保存/重载复核 |
| 渲染 | 首页、时间轴、编辑器输出 PNG；检查中文、暗色主题、7K 网格与默认浮窗收起 |
| Release 构建 | warnings-as-errors，0 warnings / 0 errors |
| Windows 自包含 payload | 无 GUI smoke test：Skia native DLL 加载和 7K LN 保存/读取，退出码 0 |
| ZIP 解压运行 | 在含空格的新目录解压，显式等待 EXE 结束，smoke test 退出码 0 |

执行入口：`scripts/Test.ps1` 和 `scripts/Publish.ps1 -Zip`。测试数据均为自制谱面或系统临时目录副本，没有修改用户真实 Songs。

## 本轮测试发现并修复

- MC 恢复副本没有保留 osu OD/HP，导致保存后 HP 被替换。
- 源文件外部改变时，重启路径可能用源替换未保存工作副本。
- 新的 source envelope 参与旧 MC fingerprint，导致正常保存被误报为结构不同。
- 从 4K 复制到 7K 时保留旧 x 坐标，导致目标列错位。
- 首启动浮窗遮挡部分按键轨道，改为默认收起。

## 未验证范围

未运行依赖作者私有 Songs/设备的完整上游 UI suite，未验证真实 WASAPI 设备端到端延迟、键盘 rollover、真实 osu! 加载/试玩与全部 skin。Headless 输入/渲染验证不等于硬件实测。完整未来功能与限制见 [发布说明](RELEASE-0.1.md)。
