# Art Hero 设置不生效的修复

2026-10-07 已完成。当前使用用户选择的 `css` 自定义配置。

## 原因

实际观察到 Art Hero 的 Hero Appearance、Gradient Settings 等修改正确传入 CSS Loader，并写入主题配置和当前预设。没有发现这些修改被自动恢复为 Playhub 默认值。

首页同时启用了 Playhub Design 和 Proper Hero Scaling。Playhub Design 用高优先级规则固定背景的高度、裁切位置和渐变遮罩；Proper Hero Scaling 另外设置首页图片尺寸和遮罩。这些规则与 Art Hero 同时作用，让 Art Hero 的部分选项看起来没有改变。

CSS Loader 的预设本来就支持修改后自动保存，见[官方 Profiles 文档](https://docs.deckthemes.com/CSSLoader/Profiles/)。

## 已调整

- 建立了 `Playhub Custom` 配置供后续选择；用户后来选择了 `css` 配置，本次验证继续使用 `css`。
- 关闭 Playhub Design。
- Proper Hero Scaling 的 Home Background 设为 No，Home Hero Transition 设为 Default，让 Art Hero 接管首页背景。
- Proper Hero Scaling 的 Gameview Background 保持 On。
- 通过 CSS Loader 自带保存功能更新当前自定义配置。

## 验证结果

渐变从 Linear Gradient 切换到 Subtle Gradient 时，实际显示的遮罩即时从线性渐变变为径向渐变。重新加载主题后，选项和值保持一致，显示效果仍正确。

背景从 Full Screen Hero 切换到 Wide Hero 时，实际首页容器高度从约 758 像素变为 379 像素。测试后恢复了用户原有的 Full Screen Hero 和 Linear Gradient，并保存至 `css` 配置。主题加载错误为空。

完整验证数据：`artifacts\css-loader-setup\art-hero-verification.json`

调整前的预设和各主题配置备份：`artifacts\css-loader-setup\preset-edit-backup-20261007-040959`

## 使用方式

在 CSS Loader 的 Selected Profile 中使用 `css`，直接修改 Art Hero 的选项，当前预设会自动记录设置。保持 Playhub Design 关闭，以及上述 Proper Hero Scaling 首页选项，避免再出现相同背景冲突。

Playhub 的“安装配置”按钮会重新解压官方预设和主题设置；再次使用它会恢复官方包的内容。
