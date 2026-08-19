# dsh-app 后续任务方案（方向稿）

> 来源：2026-08-19 全量代码审查结论，经 Four 圈定范围。
> **状态：全部落地于 v1.4.0（2026-08-19 发布）**——Bug 修复 / 优化 2·4·6·7·8 / 功能 F1·F2·F3 均已实现；F3 采用推荐的「标记 + 下次启动清」。本文档留档备查。

---

## 一、确认的 Bug：托盘菜单抛出方向坐标系错误

**位置**：`MainWindow.xaml.cs` `OpenTrayMenu()` 末段（约 698 行）

**问题**：

```csharp
double y = pt.Y / scale;   // y 已是 DIP
...
_trayFlyFrom = new Point(0, offsetY + AppMenuPanel.AnimSafePad > y / scale ? -14 : 14);
//                                                                 ^^^^^^^^ 二次缩放
```

`y` 已换算为 DIP，比较时 `y / scale` 又除一次。DPI 100% 无感；**125%/150% 缩放下菜单向上反向展开时抛出方向判反**（菜单在光标上方却从上方抛出）。

**修法**：`y / scale` → `y`。一行。

**验证**：125%/150% DPI 屏右键托盘（图标在任务栏底部），菜单向上展开时动画应从下往上抛出。

---

## 二、可优化项（2 / 4 / 6 / 7 / 8）

### 2. app.log 无限增长 → 启动时截断轮转

- **现状**：`FileLog` 只追加不清理，托盘常驻长期运行后 app.log 持续膨胀（诊断面板已暴露文件大小）。
- **方向**：启动早期（`App.OnStartup`）检查大小，超阈值（暂定 2MB）截断保留尾部（如保留最后 500KB），截断动作本身记一行日志。
- **触点**：`Helpers/FileLog.cs`（加 `TrimIfOversize()`），`App.xaml.cs` 启动序列调用。
- **注意**：截断要在 FileLog 首次写入前做，避免与异步 FlushLoop 竞争；`TailText` 内存尾缓冲不受影响。

### 4. WebView2 Runtime 缺失的安装引导

- **现状**：Runtime 缺失时 `InitWebViewAsync` 抛异常，走通用 catch 报"初始化失败"，用户不知道要装什么。
- **方向**：捕获 `CoreWebView2Environment.CreateAsync` / `EnsureCoreWebView2Async` 的特定失败（`WebView2RuntimeNotFoundException` 及 ERROR_FILE_NOT_FOUND 类），覆盖层给出专用错误卡：说明 + Runtime 官方下载链接按钮（`https://developer.microsoft.com/microsoft-edge/webview2/`）+ 重试。
- **触点**：`MainWindow.xaml.cs` `InitWebViewAsync` / `OnLoaded` 错误分支；错误卡可复用 `ShowError` 扩展一个"打开下载页"按钮，或加专用动作位。
- **注意**：Win11 一般自带 Runtime，此项面向 Win10/精简系统；按钮点击用 `Process.Start(UseShellExecute=true)`，与现有"打开浏览器/充值页"同模式。

### 6. 日志回调同步 Invoke → BeginInvoke

- **现状**：构造函数三处订阅（`_server.Log` / `_updater.Log` / `_appUpdater.Log`）用 `Dispatcher.Invoke`（同步）。npm install 输出洪峰时每行阻塞管道读取线程等 UI。
- **方向**：三处改 `Dispatcher.BeginInvoke`。`StepChanged`、`ServerDied`、余额回调评估后一并对齐（事件语义无序依赖即可全改）。
- **触点**：`MainWindow.xaml.cs` 构造函数。
- **注意**：改后日志行到达 UI 的顺序仍由 Dispatcher 队列保证（同优先级 FIFO），但**步骤状态与日志的相对先后**可能错开——冒烟时重点看启动覆盖层的步骤行与日志区表现。关闭期派发异常已有兜底惯例（参考 `BalanceMonitor.Dispatch`），套用同款 try/catch。

### 7. 两个自动更新检查串行 → 并行

- **现状**：`MaybeAutoCheckUpdate` 中 Harness 检查（最坏 60s 超时）完成后才做应用更新检查。
- **方向**：两段检查拆成独立 Task 并行（各自保留防重入标志 `_autoCheckRan` / `_appAutoCheckRan` 与设置开关判断）。
- **触点**：`MainWindow.xaml.cs` `MaybeAutoCheckUpdate`。
- **注意**：两者都会调 `UpdateMenuItems()`（非线程安全，必须 Dispatcher 上执行）——回调已在 UI 线程时注意不要在后台线程直接触碰菜单构建；并行后同时完成的概率上升，确认 `UpdateMenuItems` 重入无副作用（目前是整体重建 ItemsSource，幂等，安全）。

### 8. MainWindow.xaml.cs 拆分（1928 行 → partial class）

- **方向**：纯移动不改逻辑，按职责拆 partial：
  - `MainWindow.Tray.cs`：托盘初始化 / 托盘菜单定位 / 托盘化关窗
  - `MainWindow.Menus.cs`：三个 Popup 菜单 / 点击外部关闭钩子 / 菜单项构建
  - `MainWindow.Updates.cs`：Harness 更新 + 应用自更新两条状态机 + 进度/取消
  - `MainWindow.Balance.cs`：余额显示 / 告警 / 状态卡
  - `MainWindow.Native.cs`：DWM / WM_GETMINMAXINFO / P/Invoke 声明 / WindowPlacementStore / StepRow
  - 主体保留：启动流程 / 覆盖层 / 心跳 / 生命周期
- **注意**：这是一次性大 diff，**必须单独成 commit，不与任何行为修改混合**；编译 + 完整冒烟（启动 → 加载 → 托盘化 → 恢复 → 菜单三处 → 退出）通过即收工。字段/事件全部类内私有，拆 partial 无访问性问题。

---

## 三、可新增功能

### F1. 设置里"打开数据目录"

- **方向**：设置窗口"常驻"分组下加一个链接式按钮，点击 `explorer.exe %LOCALAPPDATA%\dsh-app`。
- **触点**：`Views/SettingsWindow.xaml`（按钮）+ `.xaml.cs`（Process.Start）。
- **注意**：与"打开日志"（MainWindow.OpenLog）同模式；目录不存在时先 `Directory.CreateDirectory` 再打开。

### F2. 更新提示带 changelog

- **方向**：更新确认弹窗（Harness 与应用两处）展示版本变更摘要。
  - **应用更新**：GitHub Release 的 `body`（Release 由 Four 手动发，changelog 是发布材料之一，直接可取）。`AppUpdater.CheckAsync` 已请求 GitHub API，顺带存 body（截断到合理长度，如 1000 字符）。
  - **Harness 更新**：npm 无 changelog 通道，可降级为只显示版本号对比（或 `npm view` 拉 `description` 意义不大）——**此项主要服务应用自更新**，Harness 侧不做或仅展示版本号。
- **触点**：`Server/AppUpdater.cs`（CheckAsync 存 ReleaseNotes），`MainWindow.xaml.cs` 两个确认弹窗文案拼装；`ConfirmDialog` 可能需要支持更长文本（检查现有 TextWrapping/MaxHeight，必要时加滚动区）。
- **注意**：Release body 是 Markdown，**直接纯文本展示，不做渲染**；为空时回退现有文案。

### F3. WebView2 缓存清理入口

- **方向**：设置窗口加"清理页面缓存"按钮：清 `%LOCALAPPDATA%\dsh-app\WebView2` 下的 Cache/Code Cache/GPUCache（**只清缓存子目录，不动 Cookies/Local Storage**，保住页面登录态与设置）。
- **触点**：`Views/SettingsWindow.xaml(.cs)`。
- **关键约束**：WebView2 运行中缓存目录被锁定，清理前须 `WebView.CoreWebView2.Reload()` 前的**停渲染**或干脆提示"重启应用后生效"——推荐方案：按钮只做标记（删除动作放到下次启动时、`InitWebViewAsync` 之前执行），避免与运行中的 Runtime 抢文件句柄。实施前细案需定夺是"立即清（先 Dispose WebView 再重建）"还是"下次启动清"，后者简单可靠。
- **注意**：清理动作记日志；失败不影响启动。

---

## 执行约定

- 本文档为方向稿；每个批次动手前先出细案给 Four 确认（红线：未经授权不改文件）。
- Bug + 优化项建议合批 v1.3.3；三个新功能合批 v1.4.0（拆分优化 8 单独成 commit）。
- 发布前强制加载 `dsh-app` 技能（INDEX.md 约定第 9 条），走 `scripts\release-publish.ps1`。
- UI 改动前必读 `docs/UI-GUIDELINES.md`（R1~R8）。
