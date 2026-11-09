# Claw · 叠页

2026-10-21 用户选定的正式图标。黑色圆角底，两片冷白、冷灰封面；不使用黄色。

`claw-pages-master.png` 是获选方案的原始 RGBA 图像，1254 × 1254。来源为本机的 `local/design/claw-icon-v2/03-pages.png`，由内置 ImageGen 生成，形状和配色按用户选定稿保留。

运行 `tools/Export-BrandIcons.ps1` 可从母版重新导出高清 PNG、Windows 多尺寸 ICO，以及游戏库和 Game Bar 工具箱的图标、磁贴和启动画面资源。所有导出保留透明背景并按目标大小等比居中，不拉伸图形。

完整生成提示词保存在 `prompt.txt`。

Windows 的单个徽标资源上限为 200 KiB。导出时先做无损压缩，仍超限的 400% 徽标使用 256 色 PNG；母版、高清图和 ICO 保留完整色深。压缩工具需要 Node.js 和 sharp，可使用本机 Codex 随附的运行时。
