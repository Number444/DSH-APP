# 方向文件：壳原生会话完成通知（SSE 方案）

> 写给 compact 后的新会话。本文件是一次已拍板但尚未实施的功能方向，含全部必要背景与技术锚点。
> 创建于 2026-08-20 晚，当时 main HEAD = `d8175b0`（另有 7 个未推送 commit：dcbafae → 53208ad → b0f0859 → 0859b2a → 0efb7ff → d677c9a → d8175b0，push 须 Four 明确授权）。
> **本文件未 commit**（Four 要求先 commit 已有改动、方向文件留待实施时一并入库）。

## 实施结果（2026-08-20 深夜补记，功能已落地并经 Four 实测通过）

- **协议纠正**：`/api/events.host` 在实际部署的 dsh 版本上**只收 WebSocket upgrade**（普通 GET 返回 426，官方 README 明确无 SSE 回退——SSE 编解码只服务进程内同构载体）。实现改为 `ClientWebSocket` 直连，方向设计（边沿表/退避/静音/开关）不变。帧格式经实抓验证：`{type:"server-request", rpcId, method, payload}`，payload 即 host 帧
- 已交付：`Server/CompletionNotifier.cs` + `MainWindow.Notify.cs` + 设置开关（默认开）+ 气泡点击按种类路由；审查后修复端口跟随（重启换端口自动重连）与 Close 帧握手
- 审查存疑项处置：子代理帧时序（added 先于 status）经实抓证伪不误报；边沿判定"连接前就在跑的会话只见收尾帧也通知"经 Four 实测确认
- **未完成**：~~插件退役清单~~ **已执行完毕（同日）**：壳内 `OnWebMessageReceived` 桥 + `OnPermissionRequested` 权限放行 + `ResetStaleNotificationPermissionsAsync` 自愈已删（无消费者）。本文件使命完结，留档备查
- **退役踩坑记录（挂载链真相）**：bundle 挂载链 = **profile `package.json` 的 `dsh.profile.bundles` + `dependencies` 声明** + `node_modules` 包本体，三处一体。只删包目录不删声明 → dsh 启动解析不到 bundle 直接 exit 1（本次实测踩中，Four 手动删声明 + 重装依赖修复）。**今后退役任何 bundle：声明与包本体必须同删**

## 决策（Four 已确认，直接执行，不必再问方向）

1. **壳侧实现会话完成通知**：直连 dsh 服务 SSE，检测 running→idle 边沿，托盘气球弹系统 toast
2. **设置页加开关**（默认开）
3. **删除浏览器插件 dsh-plugin-notify**——Four 原话"插件就再也没有必要了"。注意顺序：**先实现并验证 SSE 方案，再删插件**，不留功能空窗期

## 背景速览（通知功能演进，已发生）

- Four 写了浏览器插件 `dsh-plugin-notify`（在 `~/.dsh/profiles/web/node_modules/`），通知不弹
- 排查出三层问题并已修：① 壳 WebView2 未处理 PermissionRequested（静默拒权）；② 旧版本拒权被持久化进 profile 短路事件（壳加了启动自愈 `ResetStaleNotificationPermissionsAsync`）；③ 插件检测依赖的 `completed` 字段只对非选中会话置位（插件改为自维护 running→idle 边沿）
- WebView2 自绘通知非 Win11 样式 → 加了页面→壳 WebMessage 桥（`OnWebMessageReceived`，托盘气球弹系统 toast）
- **然后发现 SSE 直联通路，决定用壳原生方案取代插件**（本文件）

## 核心技术锚点（已实地查证，可直接照做）

**数据源**：`GET http://127.0.0.1:{port}/api/events.host`（dsh web 服务器内置，无需鉴权）

- 协议：SSE，`data: {json}\n\n` 分帧；每帧 JSON 是信封 `{rpcId, payload}`，**只取 `payload`**
- 关键帧（schema 锚点：`dsh\node_modules\@deepseek-ai\dsh-host-apiproxy\lib\index.js:5133`）：
  ```json
  { "type": "host/session-status", "sessionId": "xxx", "running": true }
  ```
- 同流还有 `host/session-added`（含 blank/parentSessionId/origin/cwd/agentPreset）、`host/session-removed`、`host/workspace-*`、`stream/error` 等——**不认识的帧一律跳过**（宽容解析）
- 客户端参考实现：同文件 `readSse`（:5354）——streaming fetch + `\n\n` 分帧 + 坏帧跳过不杀流

**壳侧落点**：

- 新类建议 `Server/CompletionNotifier.cs`：服务就绪后连接 SSE；维护 `Dictionary<string,bool>` 边沿表；`running: true→false` 边沿 → DispatchUi → `TrayIcon.ShowBalloonTip(...)`（MainWindow 已有现成用法，OnClosing 里"已最小化到托盘"气泡）
- 重连：流断/解析全挂 → 指数退避重连（如 2s/5s/15s 封顶）；服务重启（EnsureServerAsync 重跑）后重建连接；Dispose 时停
- 设置：`AppSettings` 加 `SessionCompletionNotify`（默认 true，settings.json 原子写已有惯例）+ `SettingsWindow` 加开关（仿余额开关的胶囊/滑块行），改动即时生效（参照 `SyncBalanceFromSettings` 模式）
- 文案第一版：`DSH 输出完成` / `会话 {sessionId 前 8 位} 的模型输出已完成`（host 流无标题；想要标题需再调 RPC 拿会话列表，列为可选二期，别一开始就做）
- 无 `pendingInteraction` 信息 → 没有"等待确认"文案分支（已知取舍）

**相比插件方案的独占优势**：页面关闭/托盘化 WebView2 挂起时照常工作（插件在挂起期间会失灵、恢复时补弹）。**验收亮点：窗口最小化到托盘后发条消息，完成时仍弹通知。**

## 插件退役清单（SSE 验证通过后执行）

1. 删 `C:\Users\Administrator\.dsh\profiles\web\node_modules\dsh-plugin-notify\` 整个目录
2. 编辑 `C:\Users\Administrator\.dsh\profiles\web\cordis.patch.yml`，移除 dsh-plugin-notify 的 bundle 挂载条目（旁边有注释"notify 已由 bundle 自动挂载"）
3. 壳里 `OnWebMessageReceived` 桥随之成为死代码——**一并删除**（含 InitWebViewAsync 里的订阅行与 CHANGELOG/ARCHITECTURE 对应条目改写）
4. `OnPermissionRequested` + `ResetStaleNotificationPermissionsAsync` 的去留：插件删后无消费者。**建议删除**（减少面； Four 若表态保留则留）；删则同样同步文档
5. 重启 dsh web 使插件卸载生效（会断我的会话，sessions 服务端持久化可恢复；提前告知 Four）

## 实施流程要求（dsh-app 项目铁律，细节见 skill dsh-app）

- 未经 Four 明确授权不 commit/push；commit 前必须 `skill` 工具加载 dsh-app 技能（INDEX.md 规则 9）
- UI 改动前读 `docs/UI-GUIDELINES.md`；文档同步 ARCHITECTURE.md / README.md / CHANGELOG.md（SSE 是第二个接触面，§1 接触面段落已为先前的通知桥改过措辞，实施时改为登记 SSE 流）
- 构建验证用隔离 OutDir；铺新 exe 用 rename-腾位法（运行中 exe → `*.oldHHmmss` 再 publish），绝不要求 Four 退壳（会断 dsh 服务=断我的会话）
- Four 当前从 `C:\Agent Space\dsh-app\bin\Release\net9.0-windows\win-x64\dsh-app.exe` 启动壳（桌面那份 8/19 的已过期）；当前壳构建已含「退出APP（保留服务）」菜单，换壳用它退可保服务不断
- commit message：单行中文、「」引号、分号分段、结尾带文档同步说明

## 遗留小尾巴（与本功能无关，别忘）

- `bin\Release\net9.0-windows\win-x64\` 下有多个 `.oldHHmmss` 腾位备份文件待清理
- 桌面 `dsh-app.exe` 是 8/19 旧版，若 Four 还在用桌面入口需同步替换
