# dsh-app 架构文档

> 本文档描述 dsh-app 的整体架构、调用链路与关键设计决策。
> 更新时间:2026-08-14 · 版本 v1.0.0(正式版) · 本次更新:主题系统、接管身份验证、单文件发布、菜单/设置/关于

## 1. 架构定位:纯壳(Wrapper)

dsh-app 是 DeepSeek Harness Web GUI 的**桌面壳**,不包含任何 Harness 逻辑。它做五件事:

1. 把 `dsh web` 服务器拉起来(若未运行)
2. 用 WebView2 在独立窗口里渲染 Harness UI
3. 管理服务器生命周期(关窗即停,仅停自己拉起的 + 接管验证过的)
4. 提供启动状态、错误提示与断连检测
5. 增值功能:Harness 更新(菜单检查 + 后台自动检查,经用户确认后 npm 安装)与顶栏余额显示(API Key 经用户确认授权后读取 dsh 凭据,属 §6 记录的红线例外)

壳与 Harness 的唯一接触面是 `http://127.0.0.1:3080`(HTTP 边界,零侵入)。
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
                   │  静态前端 + API + SSE 流式
                   └─────────────┘
```

**模块职责:**

| 模块 | 文件 | 职责 |
|---|---|---|
| App 层 | `App.xaml(.cs)` | 入口、单实例 Mutex、全局异常兜底、`ActiveServer` 托管、主题初始化 |
| 窗口层 | `MainWindow.xaml(.cs)` | 自绘顶栏、WebView2 渲染、覆盖层状态机、心跳、窗口记忆、菜单（日志/检查更新/设置/关于）、**顶栏余额显示** |
| 服务层 | `Server/ServerController.cs` | 并发端口探测、接管身份验证、进程拉起、就绪轮询、退出清理 |
| 更新层 | `Server/HarnessUpdater.cs` | Harness（npm 包）版本检查（npm view）与更新（npm install），semver 比较，超时兜底 |
| 余额层 | `Server/BalanceMonitor.cs` + `Helpers/CredentialsReader.cs` | DeepSeek 余额轮询（60s）、Key 来源链（凭据文件→环境变量→手动 DPAPI）、`~/.dsh/.credentials.yaml` 读取（仅授权后） |
| 设置层 | `Helpers/AppSettings.cs` | 共享设置（主题/自动检查更新/余额开关/授权标记/加密 Key），settings.json 持久化 |
| 主题层 | `Helpers/ThemeManager.cs` + `Resources/Colors.*.xaml` | 深/浅/跟随系统三模式、持久化、系统主题监听 |
| 安全层 | `Helpers/DpapiHelper.cs` | DPAPI 加解密（CurrentUser），密钥类字段存储 |
| 控件层 | `Helpers/CustomScrollBar.cs` | 自定义迷你滚动条(四档过渡,自 Toolbox 移植) |
| 视图层 | `Views/SettingsWindow` / `Views/AboutWindow` / `Views/ConfirmDialog` | 设置（主题/更新/余额）、关于、通用确认弹窗 |
| 资源层 | `Resources/Theme.xaml` | 按钮/滚动条等公用样式(全部 DynamicResource) |
| 被托管层 | dsh web(node 进程) | Harness UI + API,与本项目完全解耦 |

## 2. 启动调用链路

```
双击 dsh-app.exe
  → Main() → App.OnStartup()
      → Mutex 单实例检查
          ├─ 已有实例 → FindWindow 激活旧窗口 → Shutdown() 退出(退出码 0)
          └─ 无实例 → 订阅全局异常 → ThemeManager.Initialize()(读设置+应用配色+订阅系统主题)
                      → new MainWindow().Show()
  → MainWindow 构造
      → new ServerController()
      → 创建 %LOCALAPPDATA%\dsh-app\ 目录
      → _server.Log / StepChanged / ServerDied += Dispatcher.Invoke(...)
      → App.ActiveServer = _server
      → 订阅 Loaded / Closing / Closed / StateChanged
  → Window.Loaded → OnLoaded (async)
      ① ShowLoading() → 覆盖层显示(首帧反馈)
      ② 并行执行:InitWebViewAsync() 与 _server.EnsureServerAsync()(Task.WhenAll,互不依赖)
           → InitWebViewAsync:CoreWebView2Environment.CreateAsync(null, %LOCALAPPDATA%\dsh-app\WebView2)  ← 与 Edge 隔离
             → DefaultBackgroundColor=#0D1117(消白闪) → EnsureCoreWebView2Async()
             → Profile.PreferredColorScheme=Dark(页面滚动条/表单深色)
             → 订阅 NavigationCompleted / ProcessFailed
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
      ③ 双双就绪 → WebView.CoreWebView2.Navigate("http://127.0.0.1:3080")
  → WebView2 渲染 Harness 前端 → NavigationCompleted(IsSuccess)
      → WebView.Visibility=Visible → HideOverlay(150ms 淡出)→ 用户看到可对话界面
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
| 服务器中途崩溃(自家拉起) | `Exited` 事件 + `_ready` 标志 → 就绪后触发 `ServerDied` → 收起 WebView(R1)→ 覆盖层"服务连接已断开" + 重试 |
| 服务器中途停止(接管的外部 dsh) | 页面就绪后 30s 心跳 `CheckAliveAsync`,连续 2 次失败 → 同上错误卡;重试/重启/关窗时停止心跳 |
| 渲染进程崩溃 | `ProcessFailed(RenderProcessExited/Unresponsive)` → 自动 `Reload()` 一次,再崩溃 → 错误卡 + 重试 |
| 页面进程退出 | WebView2 `ProcessFailed(BrowserProcessExited)` → 覆盖层"页面进程已退出" + 重试 |
| 导航失败 | `NavigationCompleted(!IsSuccess)` → 错误卡片 + 重试(重试先 Shutdown 再重新 Ensure) |
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

## 6. 可移植性设计

- 壳内**零绝对路径硬编码**:不引用 `C:\Agent Space`、不引用 `~/.dsh`(服务器环境由 `dsh web` 按本机 profile 解析)
- **唯一例外(用户特批)**:余额显示功能在用户经确认弹窗**显式授权**后读取 `~/.dsh/.credentials.yaml` 的 `DEEPSEEK_API_KEY`(仅内存使用、不落盘、可随时在设置页撤销);未授权一律回退环境变量/手动填写,绝无隐式读取
- **端口可配置/容错**:默认 3080,并发探测 3080~3090
- 日志与 WebView2 数据均在 `%LOCALAPPDATA%\dsh-app\`,不写安装目录,避免权限问题

## 7. 日志与数据位置

- 壳日志 + 服务器输出:`%LOCALAPPDATA%\dsh-app\app.log`
- 服务器进程自身日志:`%LOCALAPPDATA%\dsh-app\server.log`(由 dsh web 输出,经 Log 事件落盘)
- 窗口位置/尺寸/最大化状态:`%LOCALAPPDATA%\dsh-app\window.json`(还原前校验与虚拟屏有交集,防外接屏拔除后窗口不可见)
- 主题设置:`%LOCALAPPDATA%\dsh-app\settings.json`(深/浅/跟随系统)

## 8. 排除的备选方案(决策记录)

| 方案 | 排除原因 |
|---|---|
| 浏览器装 PWA / `--app=` | 实测独立窗口不被 Edge 识别为已安装应用 |
| Tauri 2 | 目标机与本机均无 Rust 工具链,为一件事装整套工具链不划算 |
| Electron | 体积 100MB+,对纯壳过重 |
| 修改 Harness 本体 | 纯壳零侵入原则,拒绝耦合 |
