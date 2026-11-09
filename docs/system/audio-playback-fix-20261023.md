# 界面音效破音：播放缓冲断流修复（游戏库 0.1.74.0）

用户已试听安装版原文件并确认素材正常。本次修复播放器，素材和音量设置不变。

## 原因与现场证据

安装版 0.1.72 / 0.1.73 的 `SoundFx.cs` 使用四个 528 样本的 WinMM 缓冲。44.1 kHz 下，总长度只有 47.89 ms。该机的完成通知存在批量到达，录制时常隔约 60 ms 才补充一批；四块数据已经耗尽，输出插入静音后才继续。原实现也没有在第一次写入前暂停设备、预填缓冲。

以相同 `soft/startup.wav` 和 100% 应用音效音量，在真实扬声器输出上通过 WASAPI loopback 录音（未使用麦克风）：

| 项目 | 修复前 | 修复后 |
| --- | --- | --- |
| 连续声音内部检查窗口 | 首次非静音后 0.06–1.95 秒 | 同左 |
| 大于等于 1 ms 的全静音缺口 | 31 次 | 0 次 |
| 该窗口缺失的声音 | 332.625 ms | 0 ms |
| 典型单次缺口 | 10.729 ms，约每 60 ms 出现 | 无 |

另一次带队列时间戳的原播放器录音再次复现。录音对应的源代码与安装版源码 SHA256 为 `A38877D3F06CFB7A5F4EC878C1410B3899D378BED1DD5882933B234FAFA3F28B`（来自已部署版本对应工作树）。上述“修复前源码”指添加诊断记录之前的版本。生产安装包为 NativeAOT；回归程序直接编译同一个 `SoundFx.cs` 调用真实 WinMM 输出。

现场原始录音、旧版队列记录和分析工具在主工作区 `artifacts/audio-playback-investigation-20261022/`。第一次修复录音可能混入同期重新打开的游戏库声音，因此最终判断采用关闭其他音频后的独立集成测试，见本工作树 `artifacts/audio-fix-20261023/integration/results.json`。

## 改动

- 每块改为 882 样本，即精确 20 ms；四块提供 80 ms 余量，以覆盖现场的通知批次间隔。
- 队列为空、首次播放或空闲后恢复时，先暂停设备，填好队列，再启动，避免第一块立即播放时尚未填好后续块。
- 驱动完成通知改用手动复位事件；播放命令使用独立自动复位事件，防止复位驱动事件时丢掉新请求。线程空闲时等待事件，不定时轮询或持续写静音。
- 检查准备/写入/暂停/恢复的错误码；退出等待线程完成后再释放事件，已退出的输出线程允许重新建立。
- 增加只读的 `Underruns_` 计数，记录长音仍待播放时输出队列耗尽的情况，用于实际播放回归。

依据：[Microsoft 的 waveOutWrite 文档](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutwrite)说明未暂停的设备会在第一块写入后立即播放；[事件回调文档](https://learn.microsoft.com/en-us/windows/win32/multimedia/using-an-callback-to-process-driver-messages)要求使用手动复位事件并检查已经完成的缓冲块。

80 ms 是队列最大容量，增加了混音前瞻余量；这不是输入到扬声器的实测总延迟。没有使用影响整机的计时器分辨率修改。

## 验证与部署

- `dotnet run --project tests/LibrarySoundPlayback/LibrarySoundPlayback.csproj -- artifacts/audio-fix-20261023/integration`：32 项通过，669 块输出，0 次队列断流。三套风格、两个负载线程并发分配和计算、快速连按并混合长音、空闲、后台禁声、0 音量、关闭音效、销毁与重建。五次连续长音录制全部无内部静音缺口。
- `dotnet run --project tests/LibraryAudio/LibraryAudio.csproj -- .`：285 项通过。
- Release / NativeAOT / 签名 MSIX 构建成功，升级至 0.1.74.0。已有未使用字段、GamepadPicker 裁剪/AOT 警告和缺少符号包工具警告未由本次引入。
- 修复基于 0.1.73 提交 `457ce38`，保留最新手柄恢复、屏保、背景和系统主页注册。
- 原位升级，没有卸载。升级前后 `ui.ini` SHA256 均为 `CE7A2050A2DC3DBA5723CCFB791F4ED14927112276256C45DB233BD51C58B3E6`；安装后的 EXE SHA256 为 `98823393BC2D3757F603C6B9B36207B6E0C8A50D15189511B1A255ABDA71977C`。

安装记录和设置备份在本工作树 `artifacts/audio-fix-20261023/`。测试依赖 NAudio.Wasapi 仅用于 Windows 回环录音，不进入应用依赖。现场检查覆盖本机当前输出设备；用户耳听体验仍需在平时操作时复核。
