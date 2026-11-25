# 跨端落地与组件库工程化指南（Implementation Guide）

本文档说明如何以 [tokens.json](tokens.json) 为单一事实源，把 Cocoon Design System 落地为多端可复用组件库，以及 Playrail 仓库内部的渐进式抽离路线。

---

## 1. 单一事实源流水线（Token Pipeline）

建议所有技术栈**禁止手抄色值和动效常量**，而是通过轻量转换脚本（或 Style Dictionary）从 [tokens.json](tokens.json) 自动生成各端常量文件：

```text
docs/design/cocoon/tokens.json
 ├──► frontend/cocoon-ui/Tokens/CocoonTokens.g.cs       (C# WinUI 3 / Win2D, NativeAOT 零反射)
 ├──► packages/cocoon-tokens/dist/tokens.css            (Web CSS Custom Properties)
 ├──► packages/cocoon-tokens/dist/tailwind.preset.js    (Tailwind CSS 主题预设)
 └──► mobile/cocoon-compose/CocoonTokens.kt             (Android Jetpack Compose / Flutter Dart)
```

---

## 2. 目标端落地指南

### 2.1 Windows 原生组件库（`CocoonUI` — C# WinUI 3 + Win2D + Composition）
这是 Playrail 自身与后续 Windows 掌机/桌面原生应用的主力组件库形态。

#### 推荐目录结构
```text
frontend/
├── cocoon-ui/                           # 独立组件库项目（零 Playrail 业务依赖，NativeAOT 友好）
│   ├── CocoonUI.csproj
│   ├── Core/
│   │   ├── CocoonTokens.cs              # 由 tokens.json 映射的强类型色板/尺寸/弹簧参数
│   │   ├── CocoonContext.cs             # 封装 Compositor / CanvasDevice / RasterScale / Reduced / onCue
│   │   ├── CocoonSurfacePool.cs         # CompositionDrawingSurface 尺寸复用与释放管理
│   │   ├── CocoonSpring.cs              # 临界阻尼弹簧 + RwJump 瞬时覆盖 + 橡皮筋
│   │   ├── CocoonCursor.cs              # 唯一焦点引擎（四角括号 + 断开圆环 + 1.8s 周长流光 + 缓存）
│   │   └── CocoonInputController.cs     # 手柄/触控自动切换 + 惯性滚动 + 越界回弹 + 边缘返回让出
│   ├── Primitives/
│   │   ├── Card3DPainter.cs             # Card3D 6 层绘制 + 凹槽分隔线 + SoftShadow
│   │   ├── RowControlsPainter.cs        # Toggle / PickPill / OptionCard / Slider / Stepper / OrderPill
│   │   ├── CapsulePainter.cs            # B← / Y✓ / Y▶ 动作胶囊 & ToastPill
│   │   └── CocoonTypography.cs          # Poppins + PingFang/YaHei 中英混排自动字重选择
│   ├── Charts/
│   │   ├── GaugeRingPainter.cs          # 270° 粗圆环仪表
│   │   ├── ActivityBarsPainter.cs       # 固定分桶圆头柱状图
│   │   ├── CurveEditorPainter.cs        # 可编辑折线图
│   │   └── MetricChipPainter.cs         # 状态读数胶囊
│   └── Templates/
│       ├── CocoonRowItem.cs             # 统一列表行数据契约
│       ├── TwoColumnPageView.cs         # 双栏全屏设置/监控页模板
│       ├── ModalSheetView.cs            # 多层压栈二级页 + 局部重排动效
│       ├── SideDrawerView.cs            # 左滑入导航抽屉 + 右胶囊变形抽屉
│       ├── ContextMenuView.cs           # 上下文白卡菜单 + 封面起飞
│       └── ConfirmDialogView.cs         # 确认小窗
└── cocoon-gallery/                      # 独立可运行组件展厅（桌面版 Storybook + 视觉回归探针）
    ├── CocoonGallery.csproj
    └── Scenarios/                       # 控件展示、手柄/触屏实测、自动录屏截帧基准
```

#### 关键工程约束
- **NativeAOT 零反射**：全部 API 使用强类型回调与泛型接口，不依赖运行时反射或 XAML 绑定反射，保证冷启动 `< 500 ms` 与 60/120 Hz 满帧。
- **按内容键脏检查**：保留 `CpView` 的 `HeadKey / LeftKey / ListKey` 内容指纹机制，连按方向键时仅驱动合成线程的焦点框与滚动弹簧，不触发 CPU 侧 Win2D 重绘。
- **硬件独立性**：组件库不引用任何 `AgentClient`、`Steam`、`PawnIO` 或系统服务代码，业务方只通过 `CocoonRowItem` 与回调注入数据。

---

### 2.2 Web 组件库（CSS Variables + Tailwind + React / Vue）
Cocoon 的视觉语言在现代浏览器中几乎可以 **100% 还原**，适合用于 Web 控制台、文档站、Electron / Tauri / WebView2 应用：

- **3D 立体卡片（`Card3D`）CSS 映射**：
  ```css
  .cocoon-card-3d {
    border-radius: var(--cocoon-radius-card); /* ~17px @ 1x dp 或 30px @ 1920 */
    background: linear-gradient(180deg, #FDFDFE 0%, #F0F1F4 100%);
    border: 1px solid transparent;
    background-origin: border-box;
    box-shadow:
      0 7px 14px -2px rgba(0, 0, 0, 0.20),          /* drop */
      0 1px 3px rgba(0, 0, 0, 0.10),                /* contact */
      inset 0 1px 0 #FFFFFF,                        /* top specular */
      inset 0 -12px 14px -10px rgba(20, 20, 40, 0.08); /* bottom inner shade */
  }
  .cocoon-divider-groove {
    height: 1.5px;
    background: #E1E2E7;
    box-shadow: 0 1px 0 #FFFFFF;
  }
  ```
- **四角括号焦点引擎（`CocoonCursor`）**：
  - 使用独立浮层 `<svg>` 计算四段圆角折线路径（直接复用 [motion-and-focus.md §2.1](motion-and-focus.md#21-同心几何公式照抄即可像素对齐) 公式），配合 CSS `conic-gradient` + `@property --angle` 驱动 `1.8s linear infinite` 旋转流光，再用 SVG `filter: drop-shadow` 或双层 `<path>` 实现紫光晕与 40% 白边光。
  - 位移与缩放交给 **Motion / Framer Motion**（`type: "spring", stiffness: 600, damping: 48.99`）或 **React Spring**，自动获得可打断弹簧与橡皮筋效果。
- **双模输入**：监听全局 `pointerdown`（切触控态隐藏括号）与 `keydown` / `Gamepad API`（恢复焦点框），用 `prefers-reduced-motion: reduce` 媒体查询自动降级静态流光。

---

### 2.3 移动端与跨端框架（Jetpack Compose / Flutter / SwiftUI）
- **单位天然契合**：`tokens.json` 中除 `1.75` 即为标准 `dp` / `pt`（`1920×1200` 下的 `112 px` 行高 = `64 dp`，`30 px` 圆角 = `17 dp`，`4.8 dp` 焦点线宽）。
- **弹簧原生支持**：
  - **Compose**：`spring(dampingRatio = Spring.DampingRatioNoBouncy, stiffness = 600f / 1500f)`，与 Cocoon 物理定义 1:1 吻合。
  - **Flutter**：`SpringDescription(mass: 1, stiffness: 600, damping: 2 * sqrt(600))`。
  - **SwiftUI**：`.spring(duration: ..., bounce: 0)`（临界阻尼 `bounce = 0`）。
- **绘制**：`Card3D`、四角括号与 270° `GaugeRing` 分别用 `Canvas`（Compose）、`CustomPainter`（Flutter）或 `Canvas / Shape`（SwiftUI）直接绘制，避免平台自带控件样式污染。

---

## 3. Playrail 仓库内部的渐进式抽离路线图

文档定稿后，建议按以下 4 个可独立编译验证的阶段把现有代码迁移为组件库，全程保持 Playrail 正常可用、不推倒重来：

| 阶段 | 目标与交付物 | 验证标准 |
| --- | --- | --- |
| **Phase 1：令牌统一** | 编写脚本从 [tokens.json](tokens.json) 生成 `CocoonTokens.cs`；消除 `WallStage` 中 `Rw/Rm/Rs/Sp/Cp` 六套前缀里的重复颜色、字体与阴影常量。 | Playrail 编译通过，界面像素级无回归。 |
| **Phase 2：独立类库与内核抽离** | 新建 `frontend/cocoon-ui/CocoonUI.csproj`；迁出 `CocoonContext`、`CocoonCursor`、`CocoonSpring`、`Card3DPainter`、`RowControlsPainter`、`Charts/*`；让 `RowSheet` 和 `RowMenu` 统一改用 `CocoonCursor`。 | `CocoonUI` 可独立 `dotnet build` 且零业务引用；Playrail 引用它后功能完整。 |
| **Phase 3：统一列表模型与容器模板** | 用统一的 `CocoonRowItem` + `TwoColumnPageView` / `ModalSheetView` / `SideDrawerView` 替换现有重复绘制代码，`WallStage` 退化为纯「数据绑定 + 业务回调」薄壳。 | 代码量显著精简，新增设置页/弹窗只需写声明式数据模型，不再手写 Win2D 坐标。 |
| **Phase 4：组件展厅与跨端导出** | 搭建 `CocoonUI.Gallery` 桌面展厅（集成现有 `--state=rec*` 自动化录屏回归）；按需补充 `tokens.css` / Tailwind preset 生成脚本供 Web 端直接复用。 | 可脱离游戏库和后台 Agent 单独运行展厅，完整演示全部组件与双模交互。 |
