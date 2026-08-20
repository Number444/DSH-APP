# dsh-app 顶栏余额显示（设计文档）

> 状态：**已实现（2026-08-14）** · 实现对照本文档 + MainWindow/SettingsWindow 接线
> 功能：顶栏菜单按钮右侧显示 DeepSeek 开放平台剩余资金，可在设置页开关。
> 与 [UPDATE-MECHANISM.md](UPDATE-MECHANISM.md)（Harness 更新机制）相互独立。
> 已确认决策：API Key **优先自动读取 `~/.dsh/.credentials.yaml`**，用户首次开启时经**确认弹窗显式授权**——这是对架构红线"壳不引用 ~/.dsh"的**用户特批例外**；拒绝授权则回退设置页手动填写。

---

## 1. 背景与目标

用户在 Harness 里对话会消耗 DeepSeek 开放平台余额，希望随时在顶栏看到剩余资金，不必开网页查。
目标：顶栏常驻显示余额数值（如 `¥ 23.50`），定时刷新，可点击手动刷新，设置页可开关。

## 2. 现状事实（已调研确认）

| 项 | 结论 | 对设计的影响 |
|---|---|---|
| dsh 服务器（3080）余额接口 | **不存在**（已查主包 lib、dsh-host-apiproxy、前端 bundle，无 balance 端点） | 壳只能直连 DeepSeek 开放平台 |
| API key 存放 | `~/.dsh/.credentials.yaml` 的 `DEEPSEEK_API_KEY` 键（格式已读 dsh 源码确认） | 默认不读（架构红线）；**经用户确认弹窗授权后读取**，属用户特批例外（§3） |
| dsh home 路径 | 默认 `%USERPROFILE%\.dsh`，可被环境变量 `DSH_HOME` 覆盖（`dsh-credentials-local` 源码 `resolveDshHome` 确认） | 读取路径 = `$DSH_HOME` ?? `%USERPROFILE%\.dsh`，两处都尝试 |
| 官方余额接口 | `GET https://api.deepseek.com/user/balance`，Header `Authorization: Bearer <key>` | 响应格式见下 |
| 网络 | api.deepseek.com 国内可直连；本机走 Clash 系统代理（127.0.0.1:7890） | HttpClient 配 `WebRequest.GetSystemWebProxy()` 自动探测，有代理走代理、无代理直连 |
| 接口成本 | 余额查询免费、无严格限流 | 60s 轮询合理 |

**官方响应格式**（[Get User Balance](https://api-docs.deepseek.com/api/get-user-balance/)）：

```json
{
  "is_available": true,
  "balance_infos": [
    { "currency": "CNY", "total_balance": "110.00",
      "granted_balance": "10.00", "topped_up_balance": "100.00" }
  ]
}
```

- `total_balance` = 剩余总资金（`granted_balance` 赠送 + `topped_up_balance` 充值），**顶栏显示这个**
- 金额为字符串，需 `decimal.Parse` 后格式化
- `is_available: false` = 账户不可用（欠费/封禁等）→ 显示 `—`，ToolTip"账户不可用"，不弹窗

## 3. API Key 来源决策

**Key 获取顺序**（前一个不可用才走下一个）：

| 优先级 | 来源 | 说明 |
|---|---|---|
| 1 | **`~/.dsh/.credentials.yaml` 的 `DEEPSEEK_API_KEY` 键**（需确认弹窗授权） | 用户特批的红线例外；自动、无感；key 仅内存使用不落盘 |
| 2 | 环境变量 `DEEPSEEK_API_KEY` | 无需授权（壳读自己的进程环境，零侵入）；dsh 官方也支持此回退 |
| 3 | 设置页手动填写（DPAPI 加密存 settings.json） | 未授权/前两者都不可用时的兜底 |

**凭据文件格式**（已读 dsh 源码 `dsh-credentials-local` 确认）：`~/.dsh/.credentials.yaml` 是"凭据引用名 → 非空字符串"的严格顶层映射，例如：

```yaml
DEEPSEEK_API_KEY: sk-xxxxxxxx
ANTHROPIC_AUTH_TOKEN: sk-ant-xxxx
```

- DeepSeek key 的引用名默认 **`DEEPSEEK_API_KEY`**（`dsh-llm-deepseek` 源码 `DEFAULT_API_KEY_ENV` 确认；用户改过 `apiKeyEnv` 设置时以设置值为准，读取时键名做 `DEEPSEEK_API_KEY` 与设置值双尝试）
- 值必须是非空字符串；解析失败按"该来源不可用"处理，**不报错打断**

**解析实现**：C# 无内置 YAML 解析器。该文件格式虽简单（顶层 mapping、值非空字符串），但 YAML 字符串可能带引号/转义——引入 **YamlDotNet**（唯一新增 NuGet 依赖，纯托管、体积可忽略），与 dsh 官方 `yaml` 库行为一致，格式变化容忍度高。

**存储安全**：自动读取模式下 key **不落盘**（每次启动现读现用）；手动兜底模式的 key 用 Windows DPAPI（`ProtectedData.Protect`，`CurrentUser` 作用域）加密后以 Base64 存 settings.json——同用户解密，其他用户/拷贝文件不可用；**settings.json 与日志中绝不明文**。

### 3.1 授权确认弹窗（红线例外的把关）

| 项 | 设计 |
|---|---|
| 触发时机 | 用户首次开启"显示余额"开关，且检测到 `~/.dsh/.credentials.yaml` 存在时；**每次启动不重复弹**（授权结果持久化） |
| 标题 | "允许 dsh-app 读取 dsh 凭据？" |
| 正文 | "为显示 DeepSeek 开放平台余额，需要读取 `~/.dsh/.credentials.yaml` 中的 `DEEPSEEK_API_KEY`。该 Key 仅在本机内存中使用：不会写入本应用配置、不会写入日志、不会上传；你可以在设置页随时撤销授权。" |
| 按钮 | "允许"（授权并继续）/ "暂不"（进入手动填写模式） |
| 弹窗样式 | 沿用项目自绘风格（`BgCardBrush` + `RoundedButton`，GitHub Dark），与设置/关于窗一致；Owner 设为设置窗 |
| 授权持久化 | settings.json `AllowReadDshCredentials = true`；撤销后清除 |
| 反悔路径 | 设置页提供"重新授权"入口（拒绝后不自动再弹，避免骚扰） |
| 安全基调 | 这是**对架构红线的一次性、显式、可撤销的用户授权**，不是默认行为；授权状态在设置页可见 |

## 4. 模块设计：`Server/BalanceMonitor.cs`

新实例类，放 `Server` 命名空间（网络服务，与 ServerController 同级；也可复用其 HttpClient 思路）。

```csharp
namespace dsh_app.Server;

/// <summary>DeepSeek 开放平台余额监控：定时轮询 + 手动刷新。
/// 生命周期由 MainWindow 管理（页面加载完成后 Start，关窗 Stop）。</summary>
public sealed class BalanceMonitor : IDisposable
{
    /// <summary>余额变化/刷新回调：参数为格式化后的金额文本（"23.50"），失败/未配置为 null。</summary>
    public event Action<string?>? BalanceChanged;

    /// <summary>明细文本（total/granted/topped_up + 更新时间），供悬停 ToolTip。</summary>
    public string? DetailText { get; private set; }

    /// <summary>最近一次失败原因（null = 无；UI 可据此提示"检查 API Key/网络"）。</summary>
    public string? LastError { get; private set; }

    public void Start();          // 立即查一次 + 启动 60s 定时轮询
    public void Stop();           // 停止轮询（关窗/开关关闭）
    public Task RefreshAsync();   // 手动刷新（点击余额文本触发），防抖：距上次请求 <2s 忽略
}
```

**实现要点**：

| 项 | 设计 |
|---|---|
| Key 获取 | 按 §3 顺序：`CredentialsReader`（凭据文件，需授权）→ 环境变量 `DEEPSEEK_API_KEY` → 设置页手动 key（DPAPI）；每次 `RefreshAsync` 前重取（凭据文件可能被 dsh Web Models 页更新） |
| HTTP | `HttpClient`（超时 10s），Handler `Proxy = WebRequest.GetSystemWebProxy()`、`UseProxy = true`；`Authorization: Bearer <key>` |
| 轮询 | `System.Threading.Timer` 60s 间隔；失败不加速（防打爆接口）；网络失败保留上次值继续轮询 |
| 解析 | `decimal.Parse(InvariantCulture)`，失败判 `LastError = "余额数据格式异常"`；`total_balance` 保留 2 位小数输出；`balance_infos` 取第一个条目（DeepSeek 目前单一 CNY 币种；多币种时取 `currency == "CNY"` 优先，无则第一个）；`is_available=false` 判账户不可用 |
| 失败判定 | HTTP 401/403 → `LastError = "API Key 无效"`；超时/网络异常 → `LastError = "网络错误"`；均回调 `null` |
| 生命周期竞态 | `Stop()`/`Dispose()` 后置 `_stopped` 标志，在途请求完成回调时忽略（不碰已销毁的 UI） |
| 线程 | 回调经 `Dispatcher.Invoke` 上 UI（沿用 R4） |

## 5. UI 设计（顶栏）

### 5.1 位置与布局

顶栏现有布局：`[标题][工具按钮×4（…菜单）][系统按钮]`（日志入口在菜单内）。
余额文本放在**菜单按钮右侧、系统按钮左侧**：

```
… [刷新][浏览器][重启服务][菜单]  ¥ 23.50   ─ □ ✕
```

| 项 | 规范 |
|---|---|
| 文本 | `¥ 23.50`，12px，弱文字色（DynamicResource，深色 `#6E7681`），Padding 左右 8，MaxWidth 80 |
| 对齐 | 与 IconButton 同高 26 同中心（R5：StackPanel 内 Stretch 贴顶陷阱）；单行文本不设 LineHeight |
| 点击反馈 | 复用 `IconButton` 样式（hover 背景 `HoverBgBrush`），与顶栏工具按钮一致的交互反馈 |
| 点击 | 手动刷新（加 ToolTip"点击刷新 · 上次更新 12:00:05"） |
| 悬停 | ToolTip 显示明细：`总额 ¥23.50 · 赠送 ¥10.00 · 充值 ¥13.50 · 更新 12:00:05` |
| 窗口过窄 | `TextTrimming=CharacterEllipsis` 截断，不换行、不挤压系统按钮 |
| 状态显示 | 加载中 `…`；失败/未配置 `—`（ToolTip 给原因）；有值显示金额 |
| 与 R6/R7 的关系 | 余额是**可点击显示区**（非工具按钮清单成员），hover 反馈借用 IconButton 样式但不进入 4 按钮清单；错误态（服务断连）下余额照常显示（独立于服务，R7 精神一致；注：启动即失败时余额未启动，断连后仍显示） |

### 5.2 刷新时机

- 启动：页面加载完成后 `Start()`（与自动检查更新同一时机）；**仅当 `ShowBalance=true` 时启动**，否则保持隐藏
- 定时：60s 轮询
- 手动：点击余额文本
- 关窗/开关关闭：`Stop()` + 文本隐藏

## 6. 设置页

主题区、自动检查更新开关之下，新增"显示余额"区：

| 控件 | 行为 |
|---|---|
| "显示余额" 滑块开关 | 开 → 未授权且检测到凭据文件 → 弹授权确认（§3.1）；已授权/无凭据文件 → 立即 `Start()`；关 → `Stop()` + 顶栏隐藏 |
| 授权状态行 | 已授权：显示"已授权读取 dsh 凭据（`~/.dsh/.credentials.yaml`）" + "撤销授权"按钮（撤销后清除 `AllowReadDshCredentials`、内存 key 立即清空、回退手动模式）；未授权且被拒过：显示"重新授权"按钮 |
| API Key 输入（PasswordBox，兜底模式） | 仅开关开启且无自动来源时可见可用；失焦/变更即保存（DPAPI 加密）；旁附"获取 Key"链接（`https://platform.deepseek.com/api_keys`，`Process.Start` 打开） |
| Key 提示文案 | "自动读取失败时可在此手动填写 DeepSeek 开放平台 API Key" |
| 校验反馈 | 保存后立即 `RefreshAsync()` 一次，失败在输入框旁显示原因（key 无效/网络错误） |

开关默认**关**（无 key 无意义）；开启且无任何可用 key → 顶栏显示 `—`，设置页提示当前生效的来源（凭据文件/环境变量/手动）。

## 7. 数据与设置持久化

settings.json（`%LOCALAPPDATA%\dsh-app\`）扩展：

```json
{
  "Theme": "System",
  "AutoCheckUpdate": true,
  "ShowBalance": false,
  "AllowReadDshCredentials": false,
  "EncryptedApiKey": "<DPAPI-Base64>"
}
```

- `AppSettings.cs` 增加 `ShowBalance`、`AllowReadDshCredentials`、`EncryptedApiKey` 三字段（与更新机制共用同一设置文件与读写）
- **自动读取模式下 key 不落盘**（settings.json 无 key 字段）；`EncryptedApiKey` 仅手动兜底模式使用，为 DPAPI 密文，**禁止明文写入**
- 空 key：存 `null` 而非空串

## 8. 错误处理与安全边界

| 场景 | 行为 |
|---|---|
| 未填 key / 开关开但无任何来源 | 顶栏 `—`，ToolTip"未配置 API Key，请在设置中填写/授权" |
| 凭据文件缺失 / 无 `DEEPSEEK_API_KEY` 键 / 解析失败 | 自动回退环境变量 → 再回退手动模式，设置页提示当前来源 |
| 用户拒绝授权 | 进入手动模式；不自动重复弹窗；设置页可"重新授权" |
| 授权后撤销 | 清除 `AllowReadDshCredentials`，并立即触发一次刷新（顶栏不残留旧值）；后续刷新不再使用凭据文件 key，回退环境变量/手动来源；设置页文案为"撤销 dsh 凭据读取授权" |
| key 无效（401/403） | 显示 `—` + 日志 + ToolTip 原因；设置页输入框旁红字提示；不弹窗打扰 |
| 网络失败（超时/断网/代理挂） | 保留上次值继续轮询；无上次值显示 `—`；恢复后自动更新 |
| 轮询中关窗 | `Stop()`；Timer 与 HttpClient 一并 Dispose |
| 明文泄露 | settings.json 只有 DPAPI 密文；日志不输出 key（记录时打码 `sk-***`） |
| 单实例 | Mutex 已保证无并发轮询 |
| 与更新机制并发 | 相互独立：更新流程停/启服务不影响余额查询（余额直连开放平台） |

**红线**：key 属于敏感凭据——明文不出现在 settings.json、app.log、覆盖层日志；代码日志只输出打码形式（`sk-***`）。**读取 `~/.dsh/.credentials.yaml` 仅发生在用户显式授权之后**（§3.1），授权可随时在设置页撤销；这是架构红线"壳不引用 ~/.dsh"的唯一例外，须在 ARCHITECTURE.md 中如实记录。

## 9. 改动清单（实现时对照）

| 文件 | 改动 |
|---|---|
| `Server/BalanceMonitor.cs` | **新增**：轮询/刷新/解析/Key 来源链 |
| `Helpers/CredentialsReader.cs` | **新增**：读 `~/.dsh/.credentials.yaml` 的 `DEEPSEEK_API_KEY`（YamlDotNet 解析，仅在授权后调用） |
| `dsh-app.csproj` | 新增 YamlDotNet 依赖（唯一新增 NuGet 包） |
| `Helpers/AppSettings.cs` | 增加 `ShowBalance`、`AllowReadDshCredentials`、`EncryptedApiKey` 字段（与更新机制共用） |
| `MainWindow.xaml(.cs)` | 顶栏菜单右侧加余额 TextBlock（含状态与 ToolTip）；点击刷新；`NavigationCompleted` 后 `Start()`、关窗 `Stop()` |
| `Views/SettingsWindow.xaml(.cs)` | 加"显示余额"开关 + 授权状态/撤销/重新授权 + API Key 输入（PasswordBox + 链接 + 校验反馈） |
| `README.md` / `ARCHITECTURE.md` | 实现后同步：README 特性表；ARCHITECTURE 模块表（含红线例外说明） |
| 本文件 | 实现完成后状态改"已实现" |

依赖：`System.Security.Cryptography.ProtectedData` 需显式 NuGet 引用（.NET 9 桌面运行时**不内置**该 API，与 WebView2 同属包引用）；加上 YamlDotNet 后 csproj 共 3 个包引用。

## 10. 验证清单（冒烟 + 对抗）

1. **正常显示**：授权读取凭据文件 → 顶栏显示 `¥ xx.xx`（无需手动填 key）；60s 自动更新；点击手动刷新；ToolTip 明细正确
2. **授权流程**：首次开启弹确认 → 允许后自动生效；拒绝 → 手动填模式不重复弹；设置页撤销 → 回退手动且内存清空；"重新授权"可恢复
3. **来源回退**：凭据文件删除/无该键 → 自动走环境变量 → 手动；设置页显示当前生效来源
4. **错误态**：key 无效 → `—` + 原因提示不崩；断网 → 保留旧值，恢复后自动更新
5. **开关**：关 → 隐藏 + 停止轮询（日志确认无请求）；再开 → 恢复；重启后状态保持
6. **安全**：settings.json 无明文 key（grep 确认）；日志无 key（打码 `sk-***`）；授权状态可见可撤销
7. **对抗**：更新机制进行中余额照常刷新；dsh 侧 Web Models 页改 key 后余额查询自动用新 key（每次刷新重取）；关窗无残留；窗口最窄余额截断不挤压系统按钮
8. **回归**：主题三模式（余额文字随 DynamicResource 变色）、顶栏 4 按钮 + 菜单（日志/检查更新/设置/关于）、R5 对齐不受影响

## 11. 后续可选（本期不做）

- ~~余额低于阈值变色提醒~~ → **v1.2.0 已实现**（见下方实现记录）
- 多币种/多账户切换
- 从 dsh 借用 key（等 dsh 官方提供余额接口后，壳退回"纯 HTTP 边界"零配置）

---

## 实现记录（2026-08-14）

- `Helpers/CredentialsReader.cs`：`DSH_HOME` ?? `%USERPROFILE%\.dsh` 双路径尝试；YamlDotNet 解析 `DEEPSEEK_API_KEY`；仅授权后读取；红线（不记录文件内容）遵守。
- `Server/BalanceMonitor.cs`：Key 来源链（凭据文件→环境变量→手动 DPAPI）；HttpClient 系统代理；60s 轮询 + 2s 防抖 + 防重入；`is_available=false`/401/格式异常清空显示，网络失败保留旧值；Stop 后回调忽略（Volatile + Dispatch 双重检查）。
- `Helpers/DpapiHelper.cs`：统一 DPAPI 加解密（CurrentUser + Base64），设置页与 Monitor 共用。
- `Views/ConfirmDialog.cs`：自绘确认弹窗（GitHub Dark 风格），用于凭据授权与更新确认。
- `MainWindow`：顶栏菜单按钮右侧余额 TextBlock（点击刷新、ToolTip 明细、`—`/`…` 状态）；页面加载完成后 Start，关窗 Stop；设置窗关闭后同步开关并立即刷新。
- `SettingsWindow`：显示余额开关（默认关）+ 授权状态/撤销/重新授权 + 手动 Key（PasswordBox，DPAPI 加密，失焦保存）+ 首次主动开启弹授权确认；**构造期赋值不触发弹窗**（`_initializing` 屏蔽，避免每次打开设置窗重弹）。
- 冒烟：余额端点可达（401 认证拒绝符合预期）；启动/单实例链路回归通过。

## 实现记录（v1.2.0 · 2026-08-14 常驻体验）

- **余额告警**：`BalanceMonitor` 新增 `BalanceAmountChanged(decimal?)` 事件与 `LastBalance` 属性（成功回调金额数值，失败/清除回调 null）；`MainWindow.OnBalanceAmountChanged` 做**阈值跨越检测**（上次 ≥ 阈值、本次 < 阈值才提醒一次，恢复后复位，避免 60s 轮询反复轰炸）→ 双通道提醒：窗口内橙色状态卡 + 托盘气泡（`TaskbarIcon.ShowBalloonTip`，点击气泡直达充值页）。
- **余额左键菜单**（v1.2.1 重构，**右键交互已删除**）：`TitleBalance_Click` 左键弹出 `BalanceMenuPopup`（AppMenuPanel 公共菜单）→ **刷新余额**（点击后余额按钮下方状态卡先显"正在刷新余额…"，结果回调更新 ✓/✗）与 **打开充值页**（点击后才 `OpenTopUpPage()` 打开 `https://platform.deepseek.com/usage`，用户确认的充值路径）；ToolTip 为"左键菜单：刷新 / 充值"。
- **余额状态色**（v1.2.1）：文字颜色按余额分级——≥¥5 `AccentBlueBrush`（公共蓝）、¥2~¥5 `BalanceAlertBrush`（告警黄，新增 token 双套字典同 key）、<¥2 `AccentRedBrush`（危险红）；阈值固定常量（`BalanceWarnThreshold=5` / `BalanceDangerThreshold=2`），与设置页可调的通知阈值相互独立；失败/无值回退默认弱色。规范见 docs/UI-GUIDELINES.md §4.1。
- **设置页**：余额区新增"余额低于阈值时提醒"开关（默认开）+ 阈值输入（默认 ¥5，失焦校验保存，非法回显当前值）。
- **设置持久化**：`AppSettings` 新增 `MinimizeToTrayOnClose`（默认 true）、`BalanceAlertEnabled`（默认 true）、`BalanceAlertThreshold`（默认 5m）。
- 托盘悬停同步实时余额（`TaskbarIcon.ToolTipText`：`DeepSeek Harness — 余额 ¥xx.xx`）。

## 实现记录（未发布 · 2026-08-20 Kimi 额度来源）

- **显示来源切换**：设置页余额区新增「显示来源」胶囊单选（DeepSeek 余额 / Kimi 额度），持久化 `AppSettings.BalanceSource`（"deepseek" 默认 | "kimi"）；监控器每次刷新按设置重选 provider，切换后关闭设置窗即生效，无需重启。
- **Provider 分层**：`Server/BalanceProviders.cs` 新增——`FetchOutcome`/`IBalanceProvider` 上提共享；`BalanceProviderBase` 归一 HTTP 公共段（Bearer、401/403=Key 无效、其余非 2xx=网络错误保留旧值）；`DeepSeekBalanceProvider` 原逻辑原样搬入；`BalanceMonitor` 只保留轮询/防抖/派发外壳。
- **KimiUsageProvider**：`GET https://api.kimi.com/coding/v1/usages`；配额制（次数）非 ¥ 余额——解析 `limits[]` 中 300 分钟窗口（5h）+ 根 `usage`（周级）；顶栏显示 `5h: 13% / 7d: 56%`（**已用**百分比，与 Kimi 控制台语义一致；缺窗口时只显示有的一侧）；ToolTip 明细含两窗口已用次数与重置时间（本地时区）；状态色数值 = 两窗口较高已用百分比（80%/90% 黄红分级，常量 `KimiWarnPercent`/`KimiDangerPercent`）。
- **Key 来源链**：与 DeepSeek 同构——凭据文件 `KIMI_CODING_API_KEY`（同一授权，弹窗文案已更新）→ 环境变量 → 手动 DPAPI（新字段 `AppSettings.EncryptedKimiApiKey`，设置页独立 PasswordBox）。
- **UI 分支**：Kimi 模式下——余额菜单文案「刷新额度 / 打开 Kimi 用量页」（`https://www.kimi.com/code/console`），状态卡与托盘悬停文案同步；**低额告警仅 DeepSeek 生效**（Kimi 是周期配额），设置页告警区在 Kimi 来源下折叠为说明文案；顶栏文本 MaxWidth 放宽至 160（双窗口文本比 ¥ 金额宽，极端值 `5h: 100% / 7d: 100%` ~140px，80/130 均会截断）。
- 诊断面板「余额显示」行标注当前来源。
- **修复**：点余额菜单「刷新」时 `ShowBalanceStatus` 无条件 `BalanceMenuPopup.IsOpen = false` 会瞬关菜单、杀掉收拢动画——改为收拢动画在跑时不动它（`PopupAnimator.IsClosing` 新增公开查询；非菜单来源路径仍瞬关防叠显）。
- **调参与钩子化**：状态卡轻量档 200ms/120ms 肉眼不可感知——`LightOpen` 320ms（FadeMs 260 / BlurMs 280 / 模糊 8）、`LightClose` 220ms；两档仅此一处调用，不影响菜单。**根因修复**：状态卡从 `StaysOpen=False`（系统级瞬关，绕过一切动画——实测"只有打开动画"的元凶）改为 `StaysOpen=True` 并接入全局鼠标钩子——点击外部经 `DismissBalanceStatus()` 播淡出（与自动消失同一入口，序号失效 + PlayClose 幂等防竞态）；钩子安装/卸载并入菜单同款 Install/IfIdle 流程。
