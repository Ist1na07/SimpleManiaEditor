# 0.1.27 慢放时间与频谱调查

2026-10-13，Windows Release；解码／DSP／模拟消费测试，未测声卡到扬声器或屏幕的端到端延迟。

## 用户歌曲复现

只读 `Albemuth - 白夜にて.mp3`，原文件SHA256 `C2C8ECF00F10802879D1A1F5074D8E84263A23271CB52AF0F8C6BBD899EF681F`，测试前后相同。

以任一声道绝对幅值超过0.001为首声，原ACM PCM为207.94ms。旧100/28/20 SoundTouch输出在0.1/0.25/0.5x对应119.06/138.32/179.02ms。频谱约210ms是原PCM的位置。问题不是把播放器clock调一个固定延迟就能解决：伸缩会非线性挪动内部瞬态，输出总长度正确仍可错位。

新24/28/12、原PCM静音包络约束、慢速半search窗口前置与原长度输出上限：0.1/0.25/0.5/0.75x从0开始首声207.53ms，从100ms跳转约207.76–207.77ms；1x保持207.94ms。前置是DSP搜索策略，不写入offset、可视化或设备校准。

## 官方思路与参数选择

固定上游框架 [TrackBass](https://github.com/ppy/osu-framework/blob/59220cc25c394eddd78cdae9d3dfdee0a5caa0b3/osu.Framework/Audio/Track/TrackBass.cs) 使用BASS_FX、30ms sequence/4ms overlap、quick search以及OLDPOS。官方 [EditorClock](https://github.com/ppy/osu/blob/98fb49876c0242fcf649e0250d6f6b3458769a9e/osu.Game/Screens/Edit/EditorClock.cs) 的Timing模式移除tempo调整。只照搬参数到本项目的线性clock仍有瞬态误差。

只读对照原生BASS发现同MP3首声182.88ms，与ACM相差约25ms；不能只换播放器而保留原频谱decoder。SoundTouch极短4/8ms窗、Signalsmith及FFmpeg默认RubberBand的试验也未同时满足音色和时间要求，没有纳入产品。

本版使用24/28/12 full correlation search，缩短原100ms大粒度，保留较长相位重叠。谐波测试不足以检验音乐：极短4ms窗非谐波音色杂能可超过50%；quick search在部分倍率也超过本轮阈值。因此保留完整搜索，CPU开销高于0.1.26，不能宣称本轮所有性能指标都更快。

## 回归边界

- 10倍率单／双声道、不同读块、输出时长、相同声道／单侧静音、复杂打击与扫频。非谐波233/997/1373/2287Hz四音新增频谱回归，原峰之外能量要求低于2.5%，本轮约0.06%–0.84%。
- 210/1000/2000ms延迟脉冲，8倍率。慢倍率首声回到原位置，内部短音的最长低于0.001区间：0.1x约5.7–6.4ms输出、0.25x约1.4–4.4ms、0.5x为0；没有声明波形逐样本无震荡。
- 额外首音源0、末尾50ms短音、10ms短文件、seek进入持续声。慢倍率没有新增明显首声延迟，正常末尾有声到距EOF约0.7–1.5ms，原输出总时长保持。非慢速内部/尾部声音仍可能有约15ms局部扭曲，属于后续音频质量验收范围。
- 频谱每声道FFT功率合成，相同／反相双声道的结果逐字节相同；保留原FFT中心时间定义，不挪动频谱来遮盖DSP错误。
- 核心59/59、完整headless UI、空谱真实音乐导入后的全曲时间轴、设置分类直接显示均通过。未重新运行官方GPU试玩，官方游戏逻辑没有修改。

## 跳转与连续输出

原20秒48k双声道WAV／MP3／Ogg，11种模式各8次，共264次跳转。低于0.3x持续消费2秒，其他500ms；输出设备复用、暂时空块和意外停止检查通过。[原始数据](AUDIO-SEEK-0.1.27.json)。P95字段仍取8次中的最大值，不能当成可靠的尾延迟估计。

0.1x跳转中位数MP3/Ogg/WAV约23.6/28.3/24.1ms，真实DSP输出读取最大约16.1/22.9/20.8ms。该成本比0.1.26增加，现有模拟检查通过，低延迟设备仍需实机验收；后续应进一步减少相关搜索计算在输出回调上的峰值，而不是用更差的音色换数字。

复现：`scripts/Test.ps1`；UiSmoke `--audio-026`、`--audio-song-027 <只读歌曲路径>`、`--seek-benchmark report.json fixture-directory`。
