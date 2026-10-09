# 0.1.18 编辑播放绘制测量

测试入口：`SimpleManiaEditor.UiSmoke --render-benchmark <report.json>`。使用真实 Windows 窗口和产品渲染配置，所有文件与设置隔离在临时工作区；不使用玩家的 Songs、缓存或皮肤。默认 50000 键，测试可用 `SME_RENDER_NOTES=4000` 改变规模。使用无音乐编辑时钟，不能将这些数据称为音频设备端到端延迟。

采样条件：1440×960 窗口，2560×1440 / 180 Hz 单屏，7K，120 BPM，主轨、全局密度/实验难度和底部时间轴；音符只查询当前视野（最多访问 18 个）。窗口在前台，单次稳态采样约 6 秒。旧版样本预热 1.5 秒，新版 3 秒；后台工作和系统负载差异未完全控制，结果不是严谨的基准排名。

| 样本 | 绘制 FPS | 帧间隔 P50 | P95 | P99 | 主轨绘制耗时 P95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0.1.17，50000 键 | 43.9 | 21.07 ms | 36.90 ms | 51.10 ms | 0.760 ms |
| 0.1.18，4000 键 | 144.0 | 6.15 ms | 12.17 ms | 16.64 ms | 0.490 ms |
| 0.1.18，50000 键 | 109.6 | 8.56 ms | 15.97 ms | 23.54 ms | 0.783 ms |

同一开发过程中 4000 键 ANGLE/WinUI 样本约 83.3，直接 WGL 样本约 143.5；默认配置据此优先 WGL，并提供兼容路径。没有通过改时钟标签把 1000 次采样称为 1000 画面帧。

绘制 FPS 是控件构建绘制内容的频率，尚未采集 GPU Present/扫描输出；P99 的倒数只是状态栏的慢帧参考。本轮尚未满足稳定 180 或 1% low 180。后续需要 PresentMon/设备测量、音频加载时的测试、不同窗口尺寸/驱动/多轨/频谱负载和更长的重复采样。

定位依据：Avalonia 12.1.0 的 [WinUI 合成时钟实现](https://github.com/AvaloniaUI/Avalonia/blob/12.1.0/src/Windows/Avalonia.Win32/WinRT/Composition/WinUiCompositorConnection.cs)以合成提交完成驱动循环。Windows 等待精度的请求必须[配对释放](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod)；本项目仅在播放采样线程活动期间申请。
