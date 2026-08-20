# dsh-app 架构文档

> 本文档描述 dsh-app 的整体架构、调用链路与关键设计决策。
> 更新时间:2026-08-20 · 版本 v1.4.0+未发布 · 本次更新:菜单/弹窗动画体系（PopupAnimator：菜单默认档 / 状态卡轻量档 / 启动页入场编排）、像素点阵进度条（MainWindow.Progress）、Kimi 双窗口配额 + 刷新失败闭环（RefreshFailed）、整体审查加固（竞态/解析/防抖/资源十余处）、菜单新增「退出APP（保留服务）」（退壳不停服）、**壳原生会话完成通知（CompletionNotifier：WebSocket 直连服务事件流，托盘化期间照常通知）**

## 1. 架构定位:纯壳(Wrapper)

dsh-app 是 DeepSeek Harness Web GUI 的**桌面壳**,不包含任何 Harness 逻辑。它做五件事:

1. 把 `dsh web` 服务器拉起来(若未运行)
2. 用 WebView2 在独立窗口里渲染 Harness UI
3. 管理服务器生命周期(关窗默认最小化到托盘,服务继续;菜单「退出APP（保留服务）」只退壳、服务成孤儿待下次启动接管;"退出"才停服,仅停自己拉起的 + 接管验证过的)
4. 提供启动状态、错误提示与断连检测
5. 增值功能:Harness 更新(菜单检查 + 后台自动检查,经用户确认后 npm 安装)、应用自更新(菜单检查 + 后台自动检查 GitHub Releases,下载校验后更新器覆盖 exe 自动重启,失败自动回滚,确认弹窗展示 Release 说明)、顶栏余额显示(API Key 经用户确认授权后读取 dsh 凭据,属 §6 记录的红线例外)、余额告警与充值入口、**会话完成通知（壳直连服务事件流检测模型输出完成,弹系统通知,托盘化/页面挂起期间照常）**、系统托盘常驻、诊断信息面板(一键收集环境状态可复制)、设置页维护入口(打开数据目录、页面缓存清理)

壳与 Harness 的接触面：`http://127.0.0.1:3080`（HTTP 边界,零侵入）+ 一条 WebSocket 下行事件流（`/api/events.host`，只收不发，供会话完成通知），外加一条窄桥：页面 → 壳的 `WebMessageReceived` 通知上报（仅收 loopback + 当前服务端口源的 `{type:"dsh-notify"}` 消息，壳弹系统样式 toast；单向、只读消息体，无页面能力暴露；**浏览器插件过渡期待退役，届时此桥一并删除**）。
这也是它对"已安装 dsh 的任意电脑"零适配可用的原因。

```
┌─────────────────────────────────────────────────────────┐
│  dsh-app.exe (.NET 9 WPF, self-contained 单文件)         │
│                                                         │
│  ┌─────────────┐   ┌──────────────────────────────────┐ │
│  │ App 层      │   │ MainWindow 层                     │ │
│  │ 单实例 Mutex │   │ 自绘顶栏(WindowChrome)           │ │
│  │ 异常兜底     │   │ WebView2 + 覆盖层状态机          │ │
│  │ 主题初始化   │   │ 心跳/断连检测/窗口记忆            │ │
│  └──────┬──────┘   └──────────┬───────────────────────┘ │
│         │ 注册/清理            │ 订阅事件                │
│  ┌──────▼──────────────────────▼──────────────────────┐ │
│  │ ServerController 层                                 │ │
│  │ 并发探测 → 接管身份验证 → 拉起进程 → 就绪轮询 → 清理 │ │
│  └──────────────────────┬─────────────────────────────┘ │
│                         │ Process.Start(node.exe bin.js)│
└─────────────────────────┼───────────────────────────────┘
                          │ 隐藏窗口, stdout/stderr 重定向
                   ┌──────▼──────┐
                   │ node 进程    │  ← dsh web 服务器(完全独立)
                   │ 127.0.0.1:3080
                   │  静态前端 + API + 事件流（WebSocket）
                   └─────────────┘
```

**模块职责:**

| 模块 | 文件 | 职责 |
|---|---|---|
| App 层 | `App.xaml(.cs)` | 入口、单实例 Mutex、全局异常兜底、`ActiveServer` 托管、主题初始化、`App.AppVersion`（壳版本）、启动清理 update 下载残留、**日志截断（`FileLog.TrimIfOversize`，单实例判定后执行）**、**缓存清理执行（`WebView2CacheCleaner.RunPendingCleanup`，WebView2 初始化前）** |
| 窗口层 | `MainWindow.xaml(.cs)` + **7 个 partial**（v1.4.0 拆分 5 个：`.Tray` 托盘 / `.Menus` 菜单+外部关闭钩子 / `.Updates` 双更新状态机 / `.Balance` 余额 / `.Native` DWM+P/Invoke+窗口记忆+StepRow；后续新增 `.Progress` 像素点阵进度条+彗尾光柱、`.Notify` 完成通知接线+气泡点击路由） | 自绘顶栏（4 工具按钮）、WebView2 渲染、覆盖层状态机、心跳、窗口记忆、菜单（日志/诊断信息/检查 Harness 更新/检查应用更新/设置/关于/退出APP（保留服务）/退出）、**启动页入场动画（品牌区/进度卡片抛出回弹编排）**、**顶栏余额显示（点击动效 + 刷新状态卡）**、最大化钳制（WM_GETMINMAXINFO） |
| 服务层 | `Server/ServerController.cs` | 并发端口探测、接管身份验证、进程拉起、就绪轮询、退出清理、`IsManaged`（更新前置） |
| 更新层 | `Server/HarnessUpdater.cs` | Harness（npm 包）版本检查（npm view）与更新（npm install），semver 比较（共用 `Helpers/SemVer.cs`），超时兜底，装后版本验证，`LastError` 透出，更新中关窗拦截确认（`AbortRunningNpm`） |
| 自更新层 | `Server/AppUpdater.cs` | 壳自身（dsh-app.exe）更新:GitHub Releases 检查（tag/资产校验 fail-closed）、**Release 说明存取（`LatestReleaseNotes`，截断 1000 字符供确认弹窗展示）**、流式下载 + 同遍 SHA256、磁盘预检、进度节流、更新器脚本生成（内嵌模板提取）;网络策略直连优先 + 7890 代理兜底重试 |
| 诊断层 | `Helpers/Diagnostics.cs` + `Views/DiagnosticsWindow` | 环境与运行状态并行采集（node 版本/服务状态/代理/GitHub 连通性/设置项）,敏感边界:绝不含凭据;纯文本一键复制 |
| 版本层 | `Helpers/SemVer.cs` | semver 比较（提取自 HarnessUpdater,Harness 与应用自更新共用;pre-release 规则,不误报） |
| 余额层 | `Server/BalanceMonitor.cs` + `Server/BalanceProviders.cs` + `Helpers/CredentialsReader.cs` | 余额/额度轮询（60s）、**双来源 provider（DeepSeek ¥ 余额 / Kimi for Coding 配额，设置页切换）**、Key 来源链（凭据文件→环境变量→手动 DPAPI）、`RefreshAsync` 返回是否实际发起、**失败闭环（来源快照防热切换竞态 + `RefreshFailed` 事件 + 单调时钟防抖）**、`~/.dsh/.credentials.yaml` 读取（仅授权后） |
| 通知层 | `Server/CompletionNotifier.cs` | 会话完成通知：WebSocket 直连 `/api/events.host`（只收不发——客户端发消息属协议违规被 1008 关闭；本机 loopback 无 Origin 即过信任栅栏；显式禁用系统代理防 Clash 劫持握手）；`host/session-status` running→idle 边沿 → 回调弹系统 toast；边沿表 + 子代理静音（origin=subagent）+ 出错文案变体（host/agent-error 挂账）；宽容解析（坏帧跳过不杀流）；断流退避重连（2s/5s/15s 封顶，健康连接断开退避归零）；端口变化随服务重启自动跟随 |
| 设置层 | `Helpers/AppSettings.cs` | 共享设置（主题/自动检查更新/余额开关/授权标记/加密 Key/完成通知开关），settings.json 持久化（Lazy + 原子替换） |
| 主题层 | `Helpers/ThemeManager.cs` + `Resources/Colors.*.xaml` | 深/浅/跟随系统三模式、持久化、系统主题监听；`ButtonBlueBrush`（主操作按钮，对比度达标） |
| 安全层 | `Helpers/DpapiHelper.cs` | DPAPI 加解密（CurrentUser），密钥类字段存储 |
| 控件层 | `Helpers/CustomScrollBar.cs` | 自定义迷你滚动条(四档过渡,自 Toolbox 移植) |
| 动画层 | `Helpers/PopupAnimator.cs` | Popup 打开/关闭动画公共实现：打开=锚点方向抛出+BackEase 回弹+放大+模糊渐清，关闭=收拢+模糊+渐隐；**`BeginAnimation` 直调**（Storyboard 对 Transform/Effect 目标静默丢弃）；Tier<2 禁逐帧模糊；菜单默认档 / 状态卡轻量档（320ms/220ms 可感知量级）/ 启动页入场档（1.2s + 250ms 压后编排） |
| 视图层 | `Views/SettingsWindow` / `Views/AboutWindow` / `Views/ConfirmDialog` | 设置（主题/更新/常驻/**界面缩放**/数据目录与缓存清理/余额，超高 620px 滚动兜底）、关于（品牌图标内嵌 PNG）、通用自绘弹窗（单/双按钮 + glyph + 破坏性红色模式 + 长文本 320px 滚动区）；**各窗统一 Esc 关闭** |
| 维护层 | `Helpers/WebView2CacheCleaner.cs` | WebView2 页面缓存清理：设置页写 `clear-cache.flag` 标记，下次启动在 WebView2 初始化前清 Cache/Code Cache/GPUCache（不动 Cookies/Local Storage，登录态保留） |
| 资源层 | `Resources/Theme.xaml` | 按钮/滚动条等公用样式(全部 DynamicResource)、**`FocusRingStyle` 共享焦点框**（键盘焦点可见） |
| 清单层 | `app.manifest` | PerMonitorV2 DPI 感知（混合 DPI 多屏修复） |
| 被托管层 | dsh web(node 进程) | Harness UI + API,与本项目完全解耦 |

## 2. 启动调用链路

```
双击 dsh-app.exe
  → Main() → App.OnStartup()
      → Mutex 单实例检查
          ├─ 已有实例 → FindWindow 激活旧窗口 → Shutdown() 退出(退出码 0)
          └─ 无实例 → FileLog.TrimIfOversize()(app.log 超 2MB 截断留尾 500KB)
                      → WebView2CacheCleaner.RunPendingCleanup()(有标记则清页面缓存)
                      → 订阅全局异常 → ThemeManager.Initialize()(读设置+应用配色+订阅系统主题)
                      → new MainWindow().Show()
  → MainWindow 构造
      → new ServerController()
      → 创建 %LOCALAPPDATA%\dsh-app\ 目录
      → _server.Log / StepChanged / ServerDied += DispatchUi(...)(BeginInvoke 异步派发)
      → App.ActiveServer = _server
      → 订阅 Loaded / Closing / Closed / StateChanged
  → Window.Loaded → OnLoaded (async)
      ① ShowLoading() → 覆盖层显示(首帧反馈)
      ② 并行执行:InitWebViewAsync() 与 _server.EnsureServerAsync()(Task.WhenAll,互不依赖)
           → InitWebViewAsync:CoreWebView2Environment.CreateAsync(null, %LOCALAPPDATA%\dsh-app\WebView2)  ← 与 Edge 隔离
             → DefaultBackgroundColor=#0D1117(消白闪) → EnsureCoreWebView2Async()
             → Profile.PreferredColorScheme=Dark(页面滚动条/表单深色)
             → 订阅 NavigationCompleted / ProcessFailed / PermissionRequested（仅对本机 Harness 源放行通知权限——WebView2 无询问 UI、未处理即静默拒绝，浏览器端通知插件依赖此放行；其余权限保持默认拒绝，授权持久化于壳 profile）；启动时经 `GetNonDefaultPermissionSettingsAsync` 复位 3080~3090 loopback 源的历史通知权限记录（旧版本持久化的"拒绝"会短路 PermissionRequested 事件，不复位则 handler 永远收不到请求）
           → EnsureServerAsync:
             → DetectRunningServerAsync(): 并发 HTTP GET 3080~3090(Task.WhenAll,最坏 ~1s,取存活最小端口)
                 ├─ 命中 → 记录监听 PID(netstat)+ 身份验证(CIM 命令行含 dsh/bin.js 特征)
                 │     ├─ 确认为 dsh → 接管模式:关窗时一并停止;页面就绪后启动 30s 心跳
                 │     └─ 非 dsh → 直连但不接管:关窗不清理(绝不误杀)
                 └─ 未命中 → selfStarted = true
                     → 解析 node.exe 与 dsh 入口 bin.js(进程 PATH → 注册表 PATH → 常见位置,绕开 .cmd 包装器)
                     → TryStartServer(): Process.Start(node.exe, bin.js web)
                          CreateNoWindow=true(无黑窗)
                          RedirectStandardOutput/Error → 异步读 → Log 事件
                          EnableRaisingEvents=true → 就绪后中途退出触发 ServerDied 事件
                     → 每 500ms 轮询 IsHttpAlive,30s 超时;中途进程退出 → 立即判失败
      ③ 双双就绪 → StartCompletionNotifyIfEnabled()（完成通知开启时连接事件流,不等页面加载）→ WebView.CoreWebView2.Navigate("http://127.0.0.1:3080")
  → WebView2 渲染 Harness 前端 → NavigationCompleted(IsSuccess)
      → WebView.Visibility=Visible → HideOverlay(150ms 淡出)→ 用户看到可对话界面
      → StartBalanceIfEnabled()(余额开启时启动 60s 轮询)
      → MaybeAutoCheckUpdate()(自动检查开启时后台执行:Harness npm view 与应用 GitHub 检查**两个 Task 并行**,各自独立防重入,发现新版菜单高亮)
```

## 3. 运行时数据流

```
用户在窗口内对话
  → Harness 前端 JS → HTTP/SSE 请求 http://127.0.0.1:3080/api/*
  → dsh web node 进程处理(调模型、读取 ~/.dsh 技能与记忆)
  → 流式响应推回 WebView2 渲染
  → 壳全程不参与:纯 HTTP 边界,零侵入
```

## 4. 关闭 / 故障链路

| 场景 | 路径 |
|---|---|
| 点 X 正常关闭 | `Closed → ServerController.Shutdown()`:自家拉起(`selfStarted`)→ `Kill(entireProcessTree)` → 3s 未退 `taskkill /T /F` 兜底;**接管且已验证的 dsh → `taskkill /T /F` 一并停止**(防"关不掉"残留)→ `Dispose` → `App.OnExit` 清理 + `ReleaseMutex`;`_ready` 先复位防误报崩溃 |
| 端口被非 dsh 程序占用 | 身份验证不通过 → 直连使用但**不接管、不清理**,绝不误杀 |
| 环境缺失(未装 node/dsh) | 错误卡直接展示安装指引（`StartupErrorHint`） |
| WebView2 Runtime 缺失 | `CreateAsync` 抛 `WebView2RuntimeNotFoundException` → 专用错误卡：说明 + 官方下载页按钮 + 重试（装完免重启应用） |
| 服务器中途崩溃(自家拉起) | `Exited` 事件 + `_ready` 标志 → 就绪后触发 `ServerDied` → 收起 WebView(R1)→ 覆盖层"服务连接已断开" + 重试 |
| 服务器中途停止(接管的外部 dsh) | 页面就绪后 30s 心跳 `CheckAliveAsync`,连续 2 次失败 → 同上错误卡;重试/重启/关窗时停止心跳 |
| 渲染进程崩溃 | `ProcessFailed(RenderProcessExited/Unresponsive)` → 自动 `Reload()` 一次,再崩溃 → 错误卡 + 重试 |
| 页面进程退出 | WebView2 `ProcessFailed(BrowserProcessExited)` → 覆盖层"页面进程已退出" + 重试 |
| 导航失败 | `NavigationCompleted(!IsSuccess)` → 错误卡片 + 重试(重试先 Shutdown 再重新 Ensure)；错误码经 `WebErrorText` 中文化 |
| 页面外链（target=_blank） | `NewWindowRequested` 接管：Handled=true，仅 http/https 抛系统浏览器，壳内不开第二窗口 |
| 最小化到托盘 | `Hide()` 后 `TrySuspendAsync` 挂起渲染进程（页面活动可拒绝，尽力而为）；恢复窗口 `Resume()`；真退出无需唤醒 |
| 菜单「退出APP（保留服务）」 | `_exitKeepServer`：真退出（跳过托盘化拦截）但 `OnClosed` 跳过 `Shutdown()`、仅 `Dispose()` 释放进程句柄；dsh 服务成孤儿继续运行，下次启动探测接管（「退出」行为不变：停服） |
| 更新中关窗 | `OnClosing` 拦截 + ConfirmDialog"中断并关闭/继续更新"；确认后 `AbortRunningNpm` 终止安装并关闭（半安装比跑完更危险）；`Dispose` 不杀进程 |
| 更新失败 | 先 `EnsureServerAsync()` 恢复旧服务 → 错误卡 + `LastError` 原因 + 手动安装指引 |
| 应用自更新下载中关窗 | 托盘化（Hide）不取消下载（后台继续，完成弹气泡 + 待安装，恢复窗口确认）；真退出（托盘"退出"/关窗直退）取消下载并清理；`_quitForAppUpdate` 放行更新重启的真退出 |
| 应用自更新回滚 | 更新器 45s 双条件验证失败 → 终止新进程 → 备份覆盖回 → 启动旧版 → 写 `rolled-back.flag`；新实例启动检测到标记 → 弹说明窗（回滚不可无声无息） |
| 更新器等待超时 | 旧实例 60s 未退出 → 放弃覆盖（.new 保留，日志说明），绝不半覆盖 |
| 余额刷新失败 | 点击余额 → 状态卡"正在刷新…"→ 成功 ✓绿 / 失败 ✗红 / 防抖忽略 ⚠橙（仅用户点击场景弹窗，轮询静默） |
| 事件流断连/服务重启换端口 | CompletionNotifier 退避重连（2s/5s/15s 封顶；健康连接断开退避归零）；重试/重启路径幂等重入 `StartCompletionNotifyIfEnabled`，端口变化先停旧监听再连新端口 |
| 未捕获异常 | `DispatcherUnhandledException` → 写日志 → 停服务器 → 提示 → 退出 |
| 重复双击 | 第二实例 Mutex 冲突 → 激活第一个 → 退出码 0 |

## 5. 关键设计决策

1. **进程所有权标志 `selfStarted` + 接管身份验证**——生命周期管理的地基:谁拉起的谁清理;外部服务经 netstat PID + CIM 命令行双重确认是 dsh 才一并停止,非 dsh 程序绝不误杀
2. **HTTP 探测而非端口探测**——3080 上只要是 HTTP 服务就直连,避免误判;端口被非 dsh 程序占用由错误卡片兜底
3. **不依赖 PATH 解析启动链**——npm 全局 CLI 是 .cmd 批处理包装器(CreateProcess 不执行),直接解析 node.exe 与 bin.js 真实路径(进程 PATH → 注册表 PATH → 常见位置),绕开 explorer 环境过期问题
4. **日志双通道**——`Log` 事件同时驱动覆盖层实时展示 + `app.log` 持久化,启动失败可排查
5. **WebView2 UserDataFolder 独立**——不污染 Edge 主 profile,双机/多用户隔离
6. **异常订阅在 Mutex 之前**——单实例分支本身的异常也能被兜底(修复退出码 0xE0434352 的经验)
7. **airspace 三重规避**——WebView2 初始 Collapsed(覆盖层可见)、页面完成才 Visible(无黑屏)、内容区四周留 5px resize 热区(可拖拽调窗)
8. **主题三模式**——深/浅/跟随系统;双套配色字典同名 key + 全 DynamicResource 引用,切换即全界面生效
9. **self-contained 单文件发布**——目标机免装 .NET,`PublishSingleFile` 单 exe 拷走即用
10. **最大化钳制（WM_GETMINMAXINFO）**——无边框窗口最大化时把位置/尺寸钳到所在显示器工作区（物理像素,系统层面生效）;不置 `handled` 让 WPF 继续写 MinWidth/MinHeight 约束;配合 `app.manifest` PerMonitorV2 修复混合 DPI 多屏错配与跨屏模糊
11. **更新流程安全**——停服前置（文件句柄 EPERM）、装后版本验证（防 npm 静默失败）、`IsManaged` 前置（非 dsh 占端口拒绝）、更新中关窗拦截确认、`Dispose` 不杀进程（让安装跑完）
12. **弹窗自绘体系**——`ConfirmDialog` 单/双按钮 + 类型 glyph（⚠/ℹ/✓）+ 破坏性红色模式;主操作按钮用 `ButtonBlueBrush`（深色 #1F6FEB 白字 4.6:1 达标,不用 #58A6FF 作按钮底）
13. **应用自更新安全链**——运行中 exe 被锁 → 更新器脚本（PowerShell，内嵌资源模板）在旧实例退出后覆盖；下载同遍 SHA256 + .sha256 严格比对（fail-closed 不装未校验文件）；tag/资产校验 fail-closed；磁盘预检 600MB；更新器 45s 双条件验证（进程 + 端口）失败自动回滚 + 标记文件可见性；备份/清理职责全归脚本（应用启动只清下载残留，防回滚竞态）；`_quitForAppUpdate` 放行托盘拦截
14. **两套更新状态机隔离**——Harness（_checking/_updating）与应用（_appChecking/_appUpdating）完全拆分 + 统一入口守卫（任一进行中两个入口均禁用），防互扰与弹窗叠加
15. **诊断敏感边界**——只采集环境与状态：凭据只报授权布尔、代理只报连通、绝不输出 Key/.npmrc/token 值（复制内容逐字不含敏感信息）
16. **事件派发异步化（DispatchUi）**——后台事件 → UI 统一 `Dispatcher.BeginInvoke`（v1.4.0 起）：npm 输出洪峰时同步 Invoke 会阻塞管道读取线程；同优先级 FIFO 保序，关闭期派发异常就地吞掉（同 `BalanceMonitor.Dispatch` 惯例）
17. **日志截断轮转**——`FileLog.TrimIfOversize`：单实例判定之后、首次写入之前执行（避开 FlushLoop 竞争，也避免第二实例截断第一实例的活跃日志）；超 2MB 留尾 500KB，字节级找 `\n` 丢残行（UTF-8 安全），保留 BOM
18. **缓存清理标记化**——运行中 WebView2 缓存目录被 Runtime 锁定，设置页按钮只写 `clear-cache.flag`；下次启动在 `InitWebViewAsync` 之前清 Cache/Code Cache/GPUCache（按目录名递归匹配、先快照再删），不动 Cookies/Local Storage
19. **MainWindow 拆分 partial**——2001 行单文件拆为 6 个 partial（纯移动零逻辑变更），职责：主体（启动/覆盖层/心跳/生命周期）+ Tray / Menus / Updates / Balance / Native
20. **托盘化渲染挂起**——隐藏到托盘后 `CoreWebView2.TrySuspendAsync()` 挂起渲染进程（页面活动可拒绝，尽力而为只记日志），恢复窗口 `Resume()`；长期驻留不空转，省电省 CPU
21. **键盘可达性成套**——各窗统一 Esc 关闭；菜单打开焦点入 Popup 首项（↑↓ 方向导航天然支持、禁用项自动跳过，Esc 双路：面板 KeyDown→DismissRequested + 窗口 PreviewKeyDown 兜底）；共享 `FocusRingStyle` 焦点框替换全量 `{x:Null}`
22. **Popup 动画公共化（PopupAnimator）**——菜单/状态卡/启动页共用一套打开/关闭动画（打开=抛出+BackEase 回弹+缩放+模糊渐清，关闭=收拢+模糊+渐隐）；必须 `Animatable.BeginAnimation` 直调（`Storyboard.SetTarget` 对 Transform/Effect 等 Freezable 目标静默丢弃，曾致缩放/位移/模糊定格在起始态）；`BeginAnimation` 同属性替换天然取消旧动画（被替换时钟不触发 Completed，孤儿动画回调 `ReferenceEquals` 防误清）；渲染 Tier<2 禁逐帧模糊；尊重系统"菜单动画"开关；短生命周期提示用轻量档（320ms/220ms——200ms/10% 量级肉眼不可感知，实测调参结论）
23. **像素点阵进度条（MainWindow.Progress）**——90 列×2 行像素屏（3px 格+1px 缝，熄灭态 12% 透明度保持点阵质感）+ 波式级联点亮（120ms 淡入 + 8ms/列）+ 亮白前沿列 + 彗尾光柱群按宽度比例锚定已点亮区域（固定种子，每次启动布局一致）；启动覆盖层与下载进度窗共用，真实步骤/下载进度驱动
24. **余额刷新失败闭环**——来源快照（刷新期间切换 Kimi/DeepSeek 来源，迟到的旧来源响应直接丢弃）；KeepOld 保留旧值时发 `RefreshFailed`（手动刷新状态卡红字"保留上次值"，防"正在刷新…"卡死与后续轮询误弹"已更新"）；防抖改 `Environment.TickCount64` 单调时钟（系统时间回拨不再卡死刷新）
25. **壳原生会话完成通知（CompletionNotifier，WebSocket 事件流）**——直连 `/api/events.host`（实测该端点只收 WebSocket upgrade，普通 GET 返回 426 无 SSE 回退——方向文件原按 SSE 设计，实施时纠正）；`agent/status` 仅 idle/running 两值且严格交替（重复状态是 host 硬断言），流只发真实翻转、无初始快照 → running:false 恒为一次运行的结束，壳启动前就在跑的会话只见收尾帧同样通知；子代理会话静音（session-added origin=subagent，实测帧序恒为 added→status）；取代浏览器插件的根本原因：托盘化/页面挂起期间插件失灵，壳进程事件流照常
26. **气泡点击按种类路由（`_balloonKind`）**——`TrayBalloonTipClicked` 是单事件多消费者（余额告警→充值页 / 完成通知→恢复窗口），记录最近气泡种类分派；系统 toast 可排队故为近似值，误判代价低（多点一次/少跳一次）

## 6. 可移植性设计

- 壳内**零绝对路径硬编码**:不引用 `C:\Agent Space`、不引用 `~/.dsh`(服务器环境由 `dsh web` 按本机 profile 解析)
- **唯一例外(用户特批)**:余额显示功能在用户经确认弹窗**显式授权**后读取 `~/.dsh/.credentials.yaml` 的 `DEEPSEEK_API_KEY` / `KIMI_CODING_API_KEY`(按显示来源取用其一;仅内存使用、不落盘、可随时在设置页撤销);未授权一律回退环境变量/手动填写,绝无隐式读取
- **端口可配置/容错**:默认 3080,并发探测 3080~3090
- 日志与 WebView2 数据均在 `%LOCALAPPDATA%\dsh-app\`,不写安装目录,避免权限问题
- 应用自更新目录 `%LOCALAPPDATA%\dsh-app\update\`:下载残留（*.part/*.new/*.sha256，启动清理）、backup/（更新器独占）、updater.log、rolled-back.flag、apply-update.ps1（运行时生成）

## 7. 日志与数据位置

- 壳日志 + 服务器输出:`%LOCALAPPDATA%\dsh-app\app.log`（超 2MB 启动时自动截断留尾 500KB）
- 服务器进程自身日志:`%LOCALAPPDATA%\dsh-app\server.log`(由 dsh web 输出,经 Log 事件落盘)
- 缓存清理标记:`%LOCALAPPDATA%\dsh-app\clear-cache.flag`(设置页写入,下次启动清理后删除)
- WebView2 用户数据:`%LOCALAPPDATA%\dsh-app\WebView2\`（与 Edge 隔离；缓存清理只动其中的 Cache/Code Cache/GPUCache）
- 窗口位置/尺寸/最大化状态:`%LOCALAPPDATA%\dsh-app\window.json`(还原前校验与虚拟屏有交集,防外接屏拔除后窗口不可见)
- 设置:`%LOCALAPPDATA%\dsh-app\settings.json`(主题/自动检查更新/自动检查应用更新/界面缩放/余额开关/凭据读取授权标记/手动 Key 的 DPAPI 密文,原子写入)

## 8. 排除的备选方案(决策记录)

| 方案 | 排除原因 |
|---|---|
| 浏览器装 PWA / `--app=` | 实测独立窗口不被 Edge 识别为已安装应用 |
| Tauri 2 | 目标机与本机均无 Rust 工具链,为一件事装整套工具链不划算 |
| Electron | 体积 100MB+,对纯壳过重 |
| 修改 Harness 本体 | 纯壳零侵入原则,拒绝耦合 |
