# dsh-app 窗口架构与 UI 规范

> 统一窗口架构与视觉规范的权威文件。所有对 MainWindow 的改动必须先读这里。
> 更新时间：2026-08-14

---

## 1. 窗口分层架构（铁律）

```
┌────────────────────────────────────────────────┐
│  Window (WPF 渲染层)                            │
│  ┌──────────────────────────────────────────┐  │
│  │  WebView2 (HwndHost 原生子窗口)          │  │  ← 永远在最上层！
│  │  · 独立 HWND，WPF 的 ZIndex 管不住它      │  │
│  │  · 初始 Collapsed，页面加载完成才显示      │  │
│  └──────────────────────────────────────────┘  │
│  ┌──────────────────────────────────────────┐  │
│  │  Overlay (覆盖层, Panel.ZIndex=10)        │  │
│  │  · 纯 WPF 元素，可自由叠放                │  │
│  │  · 中央：品牌区 / 错误区（二选一）          │  │
│  │  · 左下：启动进度卡片                      │  │
│  └──────────────────────────────────────────┘  │
└────────────────────────────────────────────────┘
```

**规则 R1（airspace）**：WebView2 是 `HwndHost`，其原生子窗口**永远盖在 WPF 层之上**，`ZIndex`、`Opacity`、透明背景都无法改变。

- 因此 WebView2 **初始必须 `Visibility="Collapsed"`**，只在 `NavigationCompleted(IsSuccess)` 时才 `Visible`（先显示 WebView2，再隐藏 Overlay，避免空窗）。
- 禁止把覆盖层内容直接叠在可见 WebView2 之上（会看不见）。
- 若未来必须与页面内容同屏交互，唯一正解是**独立置顶窗口**（绕开 airspace），而非试图在 WPF 内叠放。

---

## 2. 顶栏规范（WindowChrome 自绘标题栏）

**规则 R6（标题栏铁律）**：系统标题栏是原生区域，WPF 控件放不进去——加任何按钮都必须走 `WindowChrome` 自绘。

| 项目 | 规范 |
|---|---|
| 窗口 | `WindowStyle="None"` + `WindowChrome`（CaptionHeight=36 / CornerRadius=0 / GlassFrameThickness=0 / ResizeBorderThickness=6 / UseAeroCaptionButtons=False） |
| 标题 | `Icon="{x:Null}"` 去图标，纯文字 "DeepSeek Harness"（13px SemiBold），Margin 16,0,0,0 |
| 工具按钮 | **纯文字**（12px，Padding 10,0，高 26，圆角 6），hover `#21262D`；`WindowChrome.IsHitTestVisibleInChrome="True"`（否则被当拖动区） |
| 系统按钮 | 46×36 标准尺寸；最小化 `─` / 最大化 `□` / 关闭 `✕`；**关闭 hover 必须红 `#F85149`** |
| 布局 | 左：标题 → 工具按钮；右：系统按钮；顶栏背景 `#0D1117` 与窗口同色 |
| 拖动 | 顶栏空白区由 CaptionHeight 提供拖动与双击最大化，禁止手动 `DragMove()` 破坏该行为 |
| **resize 热区** | `ResizeBorderThickness=5`；**内容区四周必须留出热区边距**（普通状态 `Margin 5,0,5,5`，最大化切 0）——WebView2 HWND 会拦截客户区边缘命中测试，不留边则侧/底无法调窗（airspace 的另一表现） |

**工具按钮固定清单（4 个，勿删减，纯文字）**：刷新 / 浏览器 / 重启服务 / 菜单（下拉：关于、设置、日志、诊断信息、检查 Harness 更新、检查应用更新）。日志入口在菜单内（2026-08-14 自顶栏移入）。

**下拉菜单公共控件**（v1.2.0）：菜单项渲染统一走 `Views/AppMenuPanel`（卡片容器 + `MenuItemButton` 项，点击经 `ItemClicked(Tag)` 路由）；**顶栏下拉、托盘右键、余额左键三处菜单全部同源**（均为 Popup + AppMenuPanel；v1.2.1 起托盘弃用 ContextMenu——系统 ContextMenu 的弹出动画/阴影/触发与 APP 内 Popup 菜单差异明显）。菜单项数据模型 `AppMenuItem(Tag, Content, Foreground?, Enabled?)`，高亮（更新可用蓝色）与禁用（检查/更新中）由 `MainWindow.UpdateMenuItems()` 重建列表驱动（托盘项经 `UpdateTrayMenuItems` 联动）。余额菜单（左键弹出）：刷新余额 / 打开充值页，刷新结果经余额按钮下方状态卡反馈；点击外部自动关闭（WH_MOUSE_LL 钩子，见 MainWindow 实现）。**两个更新入口**（v1.3.0）："检查 Harness 更新"（npm 包）与"检查应用更新"（壳自身，GitHub Releases）状态机完全拆分，任一检查/更新进行中两个入口均禁用（统一守卫），高亮文案统一 "更新可用：vX → vY" / "更新就绪：vX → vY"。

**规则 R7（按钮区互斥）**：顶栏按钮在错误态也必须可用（日志、刷新、重启都是排查入口）——因此 Overlay 必须在内容区（Row 1），**不得覆盖顶栏**。

---

## 3. 覆盖层布局规范（三区）

| 区域 | 位置 | 内容 | 何时显示 |
|---|---|---|---|
| 品牌区 `CenterBrand` | 窗口正中 | "DeepSeek Harness"（40px）+ 副标题 | 启动中 |
| 错误区 `CenterError` | 窗口正中 | ⚠ 标题 + 详情 + 重试/日志按钮 | 启动失败（替换品牌区） |
| 进度卡片 | **左下角** | 步骤列表 + 进度条 + 日志 + 版本号 | 启动全程 |

**规则 R2（主次原则）**：品牌/错误放正中（视觉焦点），进度放左下（信息辅助，不喧宾夺主）。
- 进度卡片：半透明底 `#E6161B22`、宽 400、圆角 10、边距 20——低调但不失存在感。
- 卡片标题"启动进度"小号加粗；步骤行、日志、版本号依次递减。
- 品牌区与错误区**互斥**：`ShowLoading()` 亮品牌灭错误，`ShowError()` 反之。

---

## 3. 启动状态机契约

### 3.1 步骤定义（`ServerController` 与 UI 共有）

| 枚举值 | 名称 | 驱动方 |
|---|---|---|
| `Detect` | 探测已有服务 | ServerController |
| `Resolve` | 定位运行环境 | ServerController |
| `Launch` | 启动 dsh web 进程 | ServerController |
| `WaitReady` | 等待服务就绪 | ServerController |
| `Load` | 加载界面 | MainWindow（NavigationCompleted 时完成） |

### 3.2 状态流转

```
○ Pending ──▶ ⋯ Running ──▶ ✓ Done
                        └──▶ ✗ Failed（终结态）
```

- 事件契约：`event Action<ServerStep, StepStatus, string>? StepChanged`
- UI 侧：`UpdateStep()` 按步骤查找行更新；`ResetSteps()` 全部回 Pending（重试时）。
- 每步从 Running 起计时，进入 Done/Failed 时自动附带耗时（`· 0.3s`）。
- 状态只允许前向流转；**失败为终结态**，重试必须走完整 `ResetSteps()`。

**规则 R3（时序铁律）**：窗口弹出**第一帧**必须可见反馈（`ShowLoading()` 放在 `OnLoaded` 最前，先于 `InitWebViewAsync`）。任何 await 之前 UI 必须已有"进行中"呈现。

**规则 R4（线程）**：ServerController 全程 async（含端口探测 `SendAsync`），**禁止在 UI 线程同步阻塞**。后台线程 → UI 统一 `Dispatcher.Invoke`。

---

## 4. 视觉规范

### 4.1 配色（GitHub Dark 系）

| Token | 值 | 用途 |
|---|---|---|
| 窗口背景 | `#0D1117` | 窗口底色 |
| 卡片背景 | `#161B22` / `#E6161B22`（半透明） | 进度卡片等 |
| 边框 | `#30363D` | 卡片描边 |
| 主文字 | `#E6EDF3` | 标题、步骤名 |
| 次文字 | `#9DA7B3` | 详情、日志 |
| 弱文字 | `#6E7681` | 版本号等最弱信息 |
| 蓝 | `#58A6FF` | 进行中、进度条、强调 |
| 绿 | `#3FB950` | 完成 ✓ |
| 红 | `#F85149` | 失败 ✗、破坏性操作按钮 |
| 橙 | `Orange` | 警告 ⚠ |
| 告警黄 | `BalanceAlertBrush`（深 `#D29922` / 浅 `#9A6700`） | **余额告警状态色**（见下）；深底对比度 ~8:1、浅底 ~4.5:1 均达标（WCAG AA） |
| 蓝按钮 | `#1F6FEB`（浅色 `#0969DA`） | **主操作按钮**（弹窗确定/更新/授权等；深色下白字对比度 4.6:1 达标，勿用 `#58A6FF` 作按钮底） |
| 绿按钮 | `#238636` | **恢复/重点操作**（错误卡"重试"等唯一恢复入口） |

**余额状态色规范**（顶栏余额文字，v1.2.1 定型）：

| 余额 | Token | 含义 |
|---|---|---|
| ≥ ¥5 | `AccentBlueBrush`（公共蓝） | 正常（常驻蓝色） |
| ¥2 ≤ 余额 < ¥5 | `BalanceAlertBrush`（告警黄） | 偏低，建议关注 |
| < ¥2 | `AccentRedBrush`（危险红） | 严重不足，尽快充值 |

> 阈值固定（5 / 2，`MainWindow` 常量 `BalanceWarnThreshold` / `BalanceDangerThreshold`），与设置页"余额告警通知阈值"（可调、仅通知）相互独立；失败/无值状态回退按钮默认弱色（`TextWeakBrush`）。新增状态色必须双套字典同 key（R8）。

### 4.2 字号阶梯与行高（**对齐规则核心**）

| 级别 | 字号 | 行高 | 用途 |
|---|---|---|---|
| 品牌 | 40 | — | "DeepSeek Harness" |
| 标题 | 18 | 28 | 错误标题 |
| 正文 | 13 | 20 | 步骤名、卡片标题、副标题 |
| 辅助 | 12 | 20 | 步骤详情 |
| 日志 | 11 | 16 | 日志、版本号 |

**规则 R5（对齐铁律）**：
1. **同一行的所有 TextBlock 必须设置相同 `LineHeight`**——字号可以不同（如详情 12/步骤名 13），行高必须统一（20px），否则基线错位。
2. **固定高度控件（按钮等）必须显式 `VerticalAlignment="Center"`**——Horizontal StackPanel 内子元素默认 Stretch，显式 Height 后 Stretch 失效会**贴顶对齐**，这是顶栏按钮错位的经典陷阱。
3. **单行垂直居中文本不要设 LineHeight**：设了 LineHeight 的 TextBlock 内 glyph 靠行框顶部渲染，视觉中心偏上约 (LineHeight-字号)/2；不设时行高=字号，控件居中即文字居中。顶栏标题/按钮文字一律走"不设 LineHeight + 控件居中"。
4. 图标（○✓⋯✗）用字符实现时，字号与所在行文本一致，不再单独加大。
5. 左对齐为主：步骤行 Icon（18px 固定列）→ 名称 → 详情（`TextTrimming=CharacterEllipsis`）三列左对齐；中央品牌/错误区允许居中。
6. 日志区、版本号用 `LineHeight` 固定行高，避免多行文本参差。

### 4.3 间距与圆角

- **间距用 4 的倍数**：4 / 8 / 10 / 12 / 14 / 16 / 20 / 24。
- 圆角：主卡片 12、进度卡片 10、按钮 6。
- 进度卡片 Margin `20,0,0,20`（左、下贴边）；Padding `20,16`。

### 4.4 主题系统

**规则 R8（主题铁律）**：主题由三层构成，任何 UI 改动必须遵守：

| 层 | 文件 | 职责 |
|---|---|---|
| 配色字典 | `Resources/Colors.Dark.xaml` / `Colors.Light.xaml` | 同名 key 的双套颜色（画刷 + 滚动条 Color），**新增颜色必须两套同 key 都加** |
| 样式字典 | `Resources/Theme.xaml` | 按钮/滚动条样式，颜色一律 `DynamicResource` 引用配色字典 |
| 管理器 | `Helpers/ThemeManager.cs` | `AppTheme.Dark/Light/System` 三模式；切换时替换 App 合并字典 `[0]`；持久化 `settings.json`；`System` 模式读系统注册表（AppsUseLightTheme）+ `SystemEvents.UserPreferenceChanged` 监听 |

- **所有 UI 颜色必须 `DynamicResource`**（`StaticResource` 只解析一次，无法随主题切换）。
- 主窗口/设置窗/关于窗的 DWM 标题栏深色随 `ThemeManager.IsDarkNow` 联动。
- 设置窗口（`Views/SettingsWindow.xaml`）：深色/浅色单选 + 跟随系统开关（开启后禁用单选）；变更即生效即保存。
- 关于窗口（`Views/AboutWindow.xaml`）：产品信息展示，无逻辑。

### 4.5 WebView2 与过渡

- **WebView2 `DefaultBackgroundColor` 固定 `#0D1117`**（与窗口同色，消除导航/刷新白闪）。
- **`Profile.PreferredColorScheme = Dark`**：页面滚动条/表单控件走深色（仅偏好提示，不改页面内容）。
- 覆盖层显示/隐藏统一走 150ms Opacity 淡入淡出（`FadeInOverlay` / `HideOverlay`）；淡出是动画完成后才 `Collapsed`，期间不可交互。
- 窗口位置/尺寸/最大化状态由 `WindowPlacementStore` 记忆（`window.json`）；最大化按钮图标随 `StateChanged` 在 `□` / `❐` 间切换。

---

## 5. 生命周期与错误处理规范

| 场景 | 规范行为 |
|---|---|
| 启动中 | 中央品牌 + 左下步骤推进，日志实时滚动到底 |
| 启动失败 | 中央错误区（⚠ + 详情 + 重试按钮）；**步骤行红色标记保留**，左下日志可查 |
| **环境缺失（未装 node/dsh）** | 错误区直接展示安装指引（`ServerController.StartupErrorHint`）：提示安装命令 + 重启应用，不提供自动安装 |
| 页面加载完成 | `WebView.Visibility = Visible` → `HideOverlay()`（顺序不可反；HideOverlay 为 150ms 淡出，完成后才 Collapsed） |
| 服务中途停止（自家进程崩溃） | `ServerController.ServerDied` → 先 `WebView.Visibility = Collapsed`（R1：WPF 覆盖层盖不住 HWND）→ 错误区"服务连接已断开" + 重试 |
| 服务中途停止（接管的外部 dsh） | 页面就绪后 30s 心跳（连续 2 次失败判定），同上错误卡；重试/重启/关窗停止心跳 |
| 渲染进程崩溃 | `ProcessFailed(RenderProcessExited/Unresponsive)` → 自动 `Reload()` 一次，再崩溃 → 错误区 + 重试 |
| 重试 / 顶栏重启服务 | **先 `WebView.Visibility = Collapsed`（R1：覆盖层盖不住 HWND）** → `ShowLoading()`（重置步骤）→ `Shutdown()` → 重新 `EnsureServerAsync()` → `Navigate` |
| 顶栏刷新 | `CoreWebView2.Reload()`（页面卡死时用） |
| 顶栏浏览器打开 | `Process.Start(url, UseShellExecute=true)`——用户原有的浏览器工作流后路 |
| 关窗（默认） | **最小化到托盘**：`OnClosing` 中 `e.Cancel + Hide()`（服务继续运行）；首次隐藏弹托盘气泡提示；设置 `MinimizeToTrayOnClose=false` 恢复关窗即退 |
| 托盘退出 | 托盘菜单"退出"置 `_trayExitRequested=true` 再 `Close()` 放行；更新中断确认（`_abortUpdateConfirmed`）同样放行——两者都必须排除在托盘拦截之外 |
| 托盘化后清理 | `ServerController.Shutdown()`：清理**自己拉起的**进程；**接管的外部服务经身份验证（进程命令行含 dsh/bin.js 特征）确认是 dsh 后一并停止**（防"关不掉"残留），非 dsh 程序绝不误杀 |

---

## 6. 资源与代码约定

1. **颜色在 `Resources/Colors.Dark.xaml` / `Colors.Light.xaml`（双套同名 key），样式在 `Resources/Theme.xaml`**（App.xaml 合并：`[0]` 配色、`[1]` 样式）。**新增颜色两套同 key 都加；样式一律 `DynamicResource` 引用**；禁止 XAML 内联写死（C# 代码内颜色常量除外，如 `StepRow` 的状态色）。
2. **滚动条统一用 `helpers:CustomScrollBar`**（自 `Toolbox` 移植，`Helpers/CustomScrollBar.cs`）：ScrollViewer 设 `VerticalScrollBarVisibility="Hidden"`，与 CustomScrollBar 并列在 Grid 中（宽 16），`TargetScrollViewer` 绑定——四档过渡（拖拽 > 悬停 Thumb > 悬停 Bar > 常态）由控件内部驱动，禁止叠系统滚动条。
3. 覆盖层元素命名：`Overlay` 前缀（`OverlayProgress` / `OverlayDetail` / `OverlayActions`）或分区名（`CenterBrand` / `CenterError`）。
4. 后台事件 → UI 一律 `Dispatcher.Invoke`，禁止直接触碰控件。
5. 日志双通道：`AppendLog`（UI 展示 + app.log 落盘）由 `_server.Log` 驱动，新增诊断信息走同一通道。
6. 新增 UI 区域必须先过本规范：**R1（airspace）→ R2（主次）→ R3（首帧反馈）→ R4（线程）→ R5（对齐）**。
