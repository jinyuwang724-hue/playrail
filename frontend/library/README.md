# 游戏库墙面（原型）

全屏游戏库的前端，照 folia（chthollyphile/folia-major）Bravais「无限书架」的参数和行为**重写**，不含 folia 代码。
纯 TypeScript + 浏览器原生动画（WAAPI / CSS），没有运行时依赖；将来由 WebView2 外壳加载。

- `src/engine/`：块模板与让位表（`tools/gen-blocks.mjs` 生成）、slot 几何、无限循环与排名、方向导航、缝、动画计划、动效参数。
- `src/stage/`：舞台（相机、虚拟化、聚焦卡、翻牌、整墙波次）、卡片、缝的内容。
- `src/input.ts`：手柄（十字键 / 左摇杆导航，右摇杆平移，A 打开，B 返回，LB / RB 换页签，视图键开性能读数）、键盘、鼠标拖动。
- `src/data/mock.ts`：假数据（封面是生成的渐变）。

```
npm install
node build.mjs --single   # dist/index.html，以及内联的 dist/library.html
node tools/gen-blocks.mjs # 重新生成块模板与让位表
```

键盘：方向键、Enter、Esc、Q / E 换页签、F 性能读数、R 切换降低动态效果。
