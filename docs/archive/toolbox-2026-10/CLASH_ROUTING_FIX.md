# Clash 分流调整记录

更新时间：2026-10-07（北京时间）。已保存并热加载，无须重启电脑。

## 调整目的与发现

优先让 Steam 游戏下载直连，避免消耗代理节点流量；为本机的绝区零、鸣潮增加直连规则。

原配置中，`steampipe.akamaized.net` 先匹配到 Steam 商店/社区规则，后面的下载直连规则无法生效。本机 Steam 下载记录还出现了 `gstore.val.manlaxy.com` 等地址，需要补充优先直连规则。

## 当前设置

- 27 个 Steam 下载域名规则置于原有规则之前，包含本机实际使用过的国内下载地址和常见内容服务器。
- 绝区零与鸣潮的主程序按完整路径直连，避免通用程序名误匹配其他游戏。
- 米哈游、库洛相关域名及 `.cn` 域名优先直连；原有国内 IP 直连规则保留。
- 相关下载、游戏域名优先使用国内 DNS，并加入当前黑名单模式的假 IP 排除列表。
- Steam 商店和社区保留原有分流；订阅来源、节点、分组及原有选择保留。

游戏路径：

```text
C:\Game\ZenlessZoneZero Game\ZenlessZoneZero.exe
C:\Game\Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe
```

规则保存在 Clash Verge 的全局扩展中，生成配置、更新订阅时会重新应用：

```text
C:\Users\13304\AppData\Roaming\io.github.clash-verge-rev.clash-verge-rev\profiles\Script.js
```

请保持 Clash 的“规则模式”。切换为“全局模式”后，不再按这些规则分流。

## 验证结果

核心配置校验通过，实际加载 1264 条规则；规则模式及 TUN 状态保留。

通过本机 Clash 端口建立少量连接，并查询核心的实际连接出口：

| Steam 下载地址 | 连接结果 | 实际出口 |
| --- | --- | --- |
| st.dl.eccdnx.com | 成功 | DIRECT |
| dl.steam.clngaa.com | 成功 | DIRECT |
| gstore.val.manlaxy.com | 成功 | DIRECT |

此次验证为服务器连接测试，没有下载完整游戏，也没有启动游戏验证登录。已确认的下载连接均未经过代理节点。

检查记录：`diagnostics/clash-routing-result.json`、`diagnostics/clash-routing-live-verification.json`。

## 备份与回退

本次生效前的全局扩展和运行配置备份位于：

```text
C:\Users\13304\Desktop\Claw-Playhub-ButtonRouter-Handoff\diagnostics\clash-routing-backup\2026-10-06T19-05-17.656Z
```

如需撤销本次调整，在当前工作目录执行：

```powershell
node .\tools\Restore-Clash-GameRouting.mjs
```

回退工具会先确认配置未被后续修改，再恢复并热加载。如果订阅更新或其他修改使检查不通过，应先检查最新配置，再恢复全局扩展并让 Clash Verge 重新生成配置，避免覆盖后来的修改。

## 参考

- [Mihomo 规则及匹配顺序](https://wiki.metacubex.one/config/rules/)
- [Mihomo DNS 配置](https://wiki.metacubex.one/config/dns/)
- [Clash Verge 扩展配置](https://www.clashverge.dev/guide/extend.html)
- [Clash Verge 全局脚本](https://www.clashverge.dev/guide/script.html)
- [MetaCubeX Steam 国内地址规则](https://github.com/MetaCubeX/meta-rules-dat/blob/meta/geo/geosite/steam%40cn.list)
