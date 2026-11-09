# Playhub CSS Loader 配置记录

2026-10-07 已安装 CSS Loader 2.1.2（Decky 版），并导入本机 Playhub 2.1.0 自带的 47 项样式。安装时启用了 `Playhub` 默认配置；随后为解决 Art Hero 的首页背景冲突，改用自定义配置。当前沿用用户选择的 `css` 配置。原有 `Bigger Focused Game` 主题保留。

## 处理的问题

- Playhub 下载日志反复报告 GitHub 请求限制。使用官方发行包手动安装，SHA-256 与 Playhub 固定的校验值一致。
- 本机 Playhub 的 Windows 兼容补丁无法匹配发行包的换行及缩进。已从本机 Playhub.dll 提取其原有的 11 处修复内容并应用到 CSS Loader。
- 中文 Windows 默认使用 GBK 读取文件，造成 Playhub Design 主题解析失败。已将主题清单、CSS 和配置读取改为 UTF-8。

## 验证

Decky 前后端均已加载 CSS Loader。48 个主题被识别（47 个 Playhub 自带主题加 1 个原有主题），主题加载错误为空。Playhub 配置的依赖全部存在并启用。Steam 大屏幕页面已注入 116 项样式，包含 Playhub Design；无需重启 Steam。

## 后续使用

Playhub 的 Big Picture Style 页面负责安装／移除官方配置；具体选项在 CSS Loader 中调整。如果页面仍显示未安装，重新打开该页面以刷新状态。重新点击安装 Playhub 配置会覆盖它自带主题的设置。CSS Loader 更新或重新安装可能覆盖此次 Windows 兼容修复；遇到问题时先检查加载错误。

Art Hero 自定义修复及验证见 `CSS_LOADER_ART_HERO_FIX.md`。

插件位置：`C:\Users\13304\homebrew\plugins\SDH-CssLoader`

样式位置：`C:\Users\13304\homebrew\themes`

安装校验及运行验证：`artifacts\css-loader-setup\install-report.json`

编码修复前的文件备份：`artifacts\css-loader-setup\backup-20261007-034324\before-utf8-fix`

Playhub 原有的“移除配置”功能可通过已写入的 `playhub-css-themes.txt` 清单移除其 47 个自带样式。若要停用此次配置，可先在 CSS Loader 中关闭 Playhub 配置，或使用 Playhub 的移除配置功能。

自动登录是否能跳过重启后的登录页面，仍需实际重启验证；本次 CSS Loader 安装没有执行重启。
