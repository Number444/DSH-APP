# 诊断信息面板（docs/DIAGNOSTICS.md）

> 状态：**已实现（2026-08-14）** · 版本 v1.3.0
> 用途：一键收集环境与运行状态，纯文本复制后发给 AI 排障。

## 入口

顶栏菜单 / 托盘菜单 → **诊断信息**（菜单项"日志"之后）。

## 采集项（`Helpers/Diagnostics.cs`，并行探测）

| 项 | 来源 | 状态语义 |
|---|---|---|
| dsh-app 版本 | `App.AppVersion`（内存） | — |
| 进程 ID | `Environment.ProcessId` | — |
| node 版本 | `ResolveNodeExe()` → `node --version`（3s 超时） | 未找到=红 |
| dsh 版本 | `HarnessUpdater.LocalVersion`（内存） | null=黄 |
| dsh 服务 | `CheckAliveAsync`（1s）+ 端口 + 模式（自启动/接管/非 dsh 占用） | 未响应=红 |
| 代理 127.0.0.1:7890 | TCP 连通测试（1.5s） | 不可用=红 |
| GitHub API | 快速 GET api.github.com（5s，独立 HttpClient） | 不可达=红 |
| 设置项 | `AppSettings` **逐字段拷贝** | — |
| 日志文件 | 路径 + 大小 | — |
| 更新状态 | Harness / 应用（未检查/已是最新/发现新版/失败） | 新版=黄 |

四项独立探测（node / 服务 / 代理 / GitHub）`Task.WhenAll` 并行，总耗时 ≈ max ≈ 5s。
窗口关闭即取消在途探测；回调经 Dispatcher + try/catch + Closed 标志（防写已关窗口）。

## 复制内容

"复制全部"→ 剪贴板纯文本键值块（`键：值` 每行一行），按钮反馈"已复制 ✓"2 秒；剪贴板被占用自动重试一次，仍失败明确提示。

## 敏感边界（硬性）

- **绝不采集**：DEEPSEEK_API_KEY、EncryptedApiKey 值、`.npmrc` 内容、任何 token/凭据
- 凭据只报授权布尔（是/否）
- 代理只报连通性，不输出 URL 原文（防 URL 含 user:pass）
- 设置项逐字段拷贝（禁整对象序列化——`AppSettings` 含 EncryptedApiKey 字段）

## 交互细节

- 打开即采集（占位"正在采集…"）；"刷新"重采（取消在途）
- 固定行高防逐行填充抖动；值长时省略号 + ToolTip 全文
- 状态点颜色走双套字典资源（绿 ButtonGreenBrush / 黄 AccentOrangeBrush / 红 AccentRedBrush / 蓝 AccentBlueBrush，R8）
- 窗口模态（ShowDialog，Owner=主窗口），与设置/关于同风格（CenterOwner、ShowInTaskbar=False）
