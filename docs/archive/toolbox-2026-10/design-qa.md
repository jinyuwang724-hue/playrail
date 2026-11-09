# Claw 工具箱 0.8.1 布局检查

final result: passed

本次按用户要求修订既有插件：固定四宫格改为可继续添加模块的纵向折叠列表，三个状态改为普通文字。旧界面是功能与状态的比较来源，布局变化是有意重新设计，不宣称逐像素复刻旧版。

## 来源与比较证据

- source visual truth path: `artifacts/ui-layout-revision/01-before.png`（本轮修改前的 0.8.0 游戏界面）；`artifacts/ui-redesign/06-final-mapping.png`（0.8.0 按键状态卡）。
- implementation screenshot path: `artifacts/ui-layout-revision/02-games-default.png`、`03-module-list.png`、`04-mapping.png`、`05-haptics.png`、`06-proxy.png`、`07-game-draft-retained.png`。
- viewport: 本机 Steam 大屏幕 1280 × 720；侧栏根区域 300 CSS px，clientWidth 与 scrollWidth 均为 300。四个模块标题均约 268 CSS px。
- source and implementation pixel dimensions: 原始图均 1280 × 720，密度一致；比较统一裁取 x=994..1280、y=38..681，得到 286 × 643，不缩放。
- state: 游戏前后比较均未选择文件、C 盘；按键前后比较均为桌面模式、相同绑定、接管开关开启、按键模块带原生白色焦点。图标、标题和状态的排列变化属于要求；Steam 开关的主题动态色不表示修改设置。
- full-view comparison evidence: `artifacts/ui-layout-revision/before-after-layout.png`、`before-after-status.png`。两张比较图都把来源和实现放在同一个输入中，已实际打开并检查。
- focused region comparison evidence: 上述图保留完整侧栏，状态、焦点和字段足够清晰，不需要进一步放大。`toolbox-preview.png` 同时呈现收起后的目录和三个状态模块。

## 五项设计检查

- 字体：继续使用 Steam 字体；模块名称 16px 粗体、副标题 12px、状态 13px；标题、辅助文字和控件各有层级。白色焦点上使用深色文字与图标。
- 间距与布局：模块以整行标题依次排列，内容在标题下展开，边界仅使用横向分隔线。无两列、固定四个位置、左侧竖线、状态卡背景或重复模块标题。游戏默认展开，空表单的分类及最终按钮在当前屏幕可见；较长内容仍使用原生侧栏滚动。
- 色彩：沿用暗色 Steam 界面，区别色仅用于原生功能图标；状态为普通辅助文字。焦点使用原生浅色背景，文字与图标均可读；不是独立状态框。
- 资产：使用 Steam 的 Library、Globe、Controller、Haptics、Chevron。没有自行绘制图标或替换字体，没有新增装饰位图。Chevron 使用原生 direction 参数。
- 文案：移除“当前状态 / 反馈状态 / 代理状态”这三个重复标签，实际状态文字保留；订阅、网络设置、节点选择及手动验证功能保留。游戏表单内没有增加展开入口或返回按钮。

## 发现与修正

- [P2，已修复] 首次实机捕获的展开箭头指向左侧，容易误解为返回。移除外部旋转，改为原生 Chevron 的 down/right 参数。最终 `02-games-default.png` 与 `03-module-list.png` 分别显示向下/向右箭头，`04-mapping.png`、`05-haptics.png`、`06-proxy.png` 进一步确认三个模块一致。
- 本次最终比较没有剩余 P0/P1/P2。原生下拉框对长磁盘、节点和分类名称的截断仍为 P3；完整选项由 Steam 原生列表显示。

## 行为与限制

- 在实际安装插件中查看四个模块、收起整个目录、依次展开按键/反馈/代理，再以 Up/Return 返回游戏表单。展开其他模块会收起此前模块；标题和对应 region 提供 aria-expanded/aria-controls/aria-labelledby。
- 真实表单选择 D 盘；经过收起、按键、反馈、代理和键盘返回游戏后，D 盘仍保留（07-game-draft-retained.png）。未选择启动文件、添加游戏、修改分类或启动游戏。
- 16 项已有游戏表单、面板生命周期、菜单路由检查通过；构建与分发入口语法通过。
- 实际 Decky 加载对象和活动对象版本均为 0.8.1，且为同一对象。
- 仅查看代理 UI，没有导入/刷新订阅、操作代理模式或开关、切换节点、执行联网验证。
- 检查使用本机一套屏幕尺寸和键盘导航；没有单独验证实体手柄、读屏器或其他屏幕密度。状态原生角色和标题关系已提供，但不据此宣称完整无障碍合规。

## 完成项

- [x] 数据列表顺序渲染，取消固定网格槽位。
- [x] 状态文字融入模块，移除竖线与卡片。
- [x] 默认展开游戏完整表单，保留模块选择和草稿。
- [x] 原生焦点、箭头、方向键与确认键检查。
- [x] 同尺寸前后比较、构建、安装与版本核对。
