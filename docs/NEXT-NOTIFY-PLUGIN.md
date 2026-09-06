# 方向文件：会话完成提醒 v2（harness 插件方案）

> **状态：已实施并随 v1.7.1 发布（2026-09-08）**。插件包 `scripts/dsh-notify/` v0.1.2（内嵌资源，含 cordis.patch.yml；0.1.1 的 minBusySeconds 去抖经 Four 拍板删除）+ 壳安装器 `Server/NotifyPluginInstaller.cs`（先于服务拉起执行）；v1（CompletionNotifier）整体删除。首轮 0.1.0 缺 `dsh.bundle.patch` 致 harness 启动 exit 1（见下「安装」节第 0 件），已修；真机验收通过（主服务实测完成弹窗，Four 肉眼确认）。下文留档为设计依据。
>
> 写给 compact 后的新会话。本文件是已拍板方向、尚未实施的功能档案，含全部必要背景与技术锚点。
> 创建于 2026-09-06，当时环境：harness 0.1.2-rc.1（`node bin.js web --no-open`，dsh-app 壳拉起），GUI 127.0.0.1:3080。

## 决策（Four 已确认，不必再问方向）

1. **只做通道 A：Windows 系统通知（Toast）**；手机推送（通道 B：ntfy/Server酱/App 轮询）**明确放弃**
2. 实现形态：**宿主侧 cordis 插件**，订阅 harness 官方事件检测任务完成，插件内直接弹 Toast
3. 安装方式：**壳（dsh-app）首启幂等写入**插件到 profile，不每次注入

## 背景：旧功能为何归档

- 旧实现（2026-08-20 落地，详见同目录 `NEXT-NOTIFY-SSE.md`）：壳原生直连 `GET /api/events.host`（WebSocket），抓 `host/session-status` 帧检测 running→idle 边沿
- **该通路已死**：新版 harness（0.1.2-rc.x）移除了 `dsh-host-apiproxy` 包，`/api/events.host` 端点不复存在（已在当前安装全量 grep 证实零匹配）
- 壳侧 `Server/CompletionNotifier.cs` 等代码因此归档；本方案是其精神续作，但通知源从「壳连服务器」改为「服务器内插件」

## 核心技术锚点（已实地查证，可直接照做）

**事件源**：新版 harness 是 cordis 插件体系，官方事件广播在 `dsh-api-session-controller`：

```js
// node_modules/@deepseek-ai/dsh-api-session-controller/lib/index.js:2621
ctx.emit("api-session/status", agent.id, status === "running")
// 配套：api-session/added | removed | error | activity
```

- `running: true→false` 边沿 = 任务完成；按 agentId 维护边沿表（与旧方案同构）
- 备选事件 `agent/status`（`dsh-agent-loop`，payload `{status:'idle'|'running'}`，maintenance 视为 idle）——不带会话 id，优先用前者
- 宽容处理：不认识的参数/事件一律跳过

**插件形态**（参照 `dsh-remote-web-ui` 的宿主半面，源码在 `%TEMP%\dsh-web-review` 或 npm 包）：

```js
// index.js 骨架（0.1.2 定稿：去抖已按 Four 拍板删除，秒回也通知）
exports.name = "dsh-notify";
exports.apply = function (ctx) {
  const lastRunning = new Map(); // agentId -> 最近状态（仅防重复 idle 帧）
  ctx.on("api-session/status", (agentId, running) => {
    const id = String(agentId);
    if (running) { lastRunning.set(id, true); return; }
    if (lastRunning.get(id) === false) return;
    lastRunning.set(id, false);
    if (!loadConfig().enabled) return;         // 完成边沿重读配置：开关即时生效
    toast(`会话 ${id.slice(0, 8)} 的模型输出已完成`);
  });
};
```

**Toast 落点**：插件在 Node 进程里，`child_process` spawn PowerShell 弹 WinRT Toast（`Windows.UI.Notifications.ToastNotificationManager` + ToastXml 配方，免 BurntToast 依赖；AppID 用 PowerShell 自有标识即可）。实施时定稿具体配方，注意 toast 文本 XML 转义。
- 独占优势继承自旧方案：窗口托盘化/页面挂起期间插件仍在 harness 进程内，照常弹通知

**安装（壳侧首启，幂等）**：bundle 挂载链**四要件**（原记"三处一体"系旧时代经验，首轮实施被加载器打脸后补正）：

0. **插件自身 package.json 必须有 `dsh.bundle.patch` 声明**，指向包内 cordis.patch.yml 补丁文件——bundle 靠补丁挂载而非 main 入口直载；缺此项 harness 启动抛 `declares no dsh.bundle in its package.json` **exit 1**（dsh-app-boot/lib/index.js:861 硬校验，2026-09-08 实测踩中致 dsh 三连退；cordis.patch.yml 格式照抄 remote-web-ui：`- insert: [{id, name}]` 一行挂载）
1. 插件包本体 → `~/.dsh/profiles/web/node_modules/dsh-notify/`（壳内嵌资源写出，版本不一致或关键文件残缺才覆写）
2. profile `package.json`：`dependencies` + `dsh.profile.bundles` 各加一行（参照 remote-web-ui 的手动加法，本次已有成功经验）
3. 写完后**需重启 harness 生效**——下次自然重启或随发布流程；不主动断服务

**免断线验证法**（本轮实测有效）：`dsh --profile <测试profile> --dump-default-config` 只组层不开服务，可验 bundle 被接受；`dsh --profile <测试profile> --port 3399 --no-open` 起第二实例，stdout 见 `[dsh-notify] 已加载` 即插件真挂载——全程不动主 3080 服务

**配置**：`$DSH_HOME/settings.yaml` 加 `dsh-notify:` 命名空间（仅 `enabled`，默认 true；0.1.1 的 `minBusySeconds` 去抖经 Four 拍板删除，安装器同步开关时会清掉遗留行）。插件在完成边沿重读配置——开关改动即时生效免重启（比原设计"启动读一次"更进一步）。

## 验收要点

- 任意任务（含秒回）完成即弹 Toast——已于 2026-09-08 在主 3080 服务实测通过（Four 肉眼确认本对话完成提醒弹出）
- 壳最小化到托盘 / GUI 关闭场景下仍弹（插件在 harness 进程内，与壳存亡无关）
- 卸载路径：四要件同删（node_modules 目录 + package.json 两行，包内声明随包同灭），不留解析残骸

## 实施流程要求（dsh-app 项目铁律，细节见 skill dsh-app）

- 未经 Four 明确授权不 commit/push；commit 前必须 `skill` 工具加载 dsh-app 技能（INDEX.md 规则 9）
- 文档同步 ARCHITECTURE.md / CHANGELOG.md（通知源登记为插件方案，旧 SSE 通路标注已死）
- 铺新 exe 用 rename-腾位法，绝不要求 Four 退壳（会断 dsh 服务）
- commit message：单行中文、「」引号、分号分段、结尾带文档同步说明
