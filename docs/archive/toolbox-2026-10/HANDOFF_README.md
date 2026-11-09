# Claw 前置键双映射：Codex 开发交接包

**本机开发进度：Claw 掌机工具箱 0.12.4 已安装。** 接管期间增加 Game Bar 窗口保护；0.12.2 单独屏蔽快捷键仍失败，0.12.4 真实进程关闭、退出恢复及实体键复测通过，用户确认“只出现 Steam 左侧栏”，见 [按键修复](GAME_BAR_BUTTON_FIX_20261010.md)。大屏幕接管逻辑继续保留，验证见 [接管记录](STEAM_OWNERSHIP_20261010.md)。Magpie 134 个效果、七个默认方案和管理员游戏捕获继续保留，见 [Magpie 验证](MAGPIE_REAL_GAME_20261010.md)。使用说明见 [CLAW_TOOLBOX.md](CLAW_TOOLBOX.md)，安装包为 `artifacts/claw-button-router-0.12.4.zip`。

整理日期：2026-10-05，Asia/Shanghai。

以下是原始开发交接资料，描述开发前的目标和未知项；当前实现与实测结论以 DEVELOPMENT.md 为准。

## 开始使用

1. 将压缩包解压到掌机上的一个开发目录。
2. 在掌机上的 Codex 打开该目录。
3. 告诉 Codex：

> 请先完整读取 CODEX_HANDOFF.md，按其中的目标开发 MSI Claw 8 EX AI+ 的前置键双映射工具。先做只读诊断，核对实际按键信号、Playhub 模式接口和 ClawTweaks 的现有响应，再实现映射。需要保留 MotionAssistant 的性能功耗控制，以及 ClawTweaks 的手柄／桌面鼠标切换、RGB、小黄鸭快捷操作和背键映射。不要直接增加 Handheld Companion 或第二套虚拟手柄。SESSION_CONTEXT.md 是此前对话摘要，SOURCE_EVIDENCE.md 是已核对的源码线索。请持续推进到可运行、可验证的结果，并明确主页与实际游戏中的验证范围。

## 文件

- [CODEX_HANDOFF.md](CODEX_HANDOFF.md)：完整目标、环境、实现方向、约束、未知项和验收条件；下一台 Codex 应优先读这一份。
- [SESSION_CONTEXT.md](SESSION_CONTEXT.md)：开发相关的会话记录摘要，包含用户原话和此前建议的修正。
- [SOURCE_EVIDENCE.md](SOURCE_EVIDENCE.md)：版本固定的上游链接、关键代码摘录和事实边界。

新增源码提供编译、测试和安装脚本，不包含驱动。Switch 固件、封面下载等早期无关话题没有纳入本项目资料。

如掌机上的软件已升级，请保留本包中的历史结论，同时重新核对新版本。报告里的路径和进程状态是历史线索，不能替代本机检测。
