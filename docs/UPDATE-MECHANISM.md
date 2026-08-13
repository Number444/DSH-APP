# dsh-app Harness 更新机制（设计文档）

> 状态：**已实现（2026-08-14）** · 实现对照本文档 + MainWindow/SettingsWindow 接线
> 本文档描述"通过壳更新 Harness（npm 全局包 `@deepseek-ai/dsh`）"的完整方案，供维护时对照。
> 已确认的决策：入口只放顶栏菜单下拉；启动自动检查默认开、设置里可关。
> 相关文档：[BALANCE-DISPLAY.md](BALANCE-DISPLAY.md)（顶栏余额显示，独立功能）

---

## 1. 背景与目标

dsh-app 是纯壳，Harness 本体是 npm 全局包 `@deepseek-ai/dsh`，由壳直连 `node.exe + bin.js` 拉起。
Harness 升级目前只能用户去命令行手动 `npm install -g @deepseek-ai/dsh@latest`，目标是在壳内完成：

1. **检查**：本地已装版本 vs npm registry 最新版（手动 + 启动后台自动）
2. **更新**：用户确认后停服 → `npm install -g` → 重启服务，全程可见进度与日志
3. **回滚兜底**：更新失败不破坏现状（先恢复服务再报错）

## 2. 现状事实（本机已验证）

| 项 | 值 | 对设计的影响 |
|---|---|---|
| 本地 dsh 版本 | `0.1.0-rc.6`（读 `package.json` 的 `version`） | 版本比较必须处理 pre-release（rc） |
| npm 全局目录 | `C:\Users\Administrator\AppData\Roaming\npm`（用户级） | **更新不需要管理员权限** |
| node.exe | `C:\Program Files\nodejs\node.exe` | 复用现有 `ResolveNodeExe()` |
| npm 真实入口 | `<node目录>\node_modules\npm\bin\npm-cli.js` | npm 命令同样走"直连 node + 真实入口"，绕开 `.cmd` 包装器 |
| npm 代理 | 已配置（127.0.0.1:7890），registry 官方源 | 网络由 npm 自理，壳不感知代理 |
| 更新前必须停服 | Windows 下运行中的 node 进程持有 bin.js 文件句柄 | 更新前置条件：先 `Shutdown()`，否则 npm 覆盖安装 EPERM |

## 3. 总体流程

```
┌─ 手动：菜单"检查更新" ─┐   ┌─ 自动：启动加载完成后后台 CheckAsync ─┐
└──────────┬─────────────┘   └──────────────────┬──────────────────┘
           └──────────────┬────────────────────┘
                          ▼
                 CheckAsync()（只读网络，不打断服务）
              node.exe npm-cli.js view @deepseek-ai/dsh version
                          │
              ┌───────────┴───────────┐
              ▼                       ▼
        版本比较（见 §5）        网络失败/无新版
              │                       │
        有新版（vX → vY）       菜单"检查更新"→ 提示"已是最新 vX"；
              │                  自动检查 → 静默（不打扰）
              ▼
        用户确认（弹窗：当前/最新/更新说明）
              │
              ▼
    WebView.Visibility = Collapsed（R1：覆盖层盖不住 HWND，先收页面）
              │
              ▼
    覆盖层"正在更新 Harness…"（ShowLoading 复用）
              │
              ▼
    _server.Shutdown()   ← 必须先停服（§6.1）
              │
              ▼
    UpdateAsync()：node.exe npm-cli.js install -g @deepseek-ai/dsh@latest
    （隐藏窗口、输出重定向进 Log 通道、5 分钟超时兜底）
              │
      ┌───────┴────────┐
      ▼                ▼
    成功              失败
      │                │
      ▼                ▼
 EnsureServerAsync() + Navigate → 提示"已更新到 vY"
                EnsureServerAsync() 先恢复服务 → 错误提示 + 日志入口
```

## 4. 模块设计：`Server/HarnessUpdater.cs`

新增实例类，放 `Server` 命名空间，事件模式与 ServerController 一致（MainWindow 订阅）。

```csharp
namespace dsh_app.Server;

/// <summary>Harness（npm 全局包 @deepseek-ai/dsh）版本检查与更新。</summary>
public sealed class HarnessUpdater : IDisposable  // Dispose：终止仍在运行的 npm 检查/更新进程
{
    /// <summary>日志回调（与 ServerController.Log 同一通道：UI + app.log）。</summary>
    public event Action<string>? Log;

    /// <summary>本地已装版本（构造时解析一次；解析失败为 null）。</summary>
    public string? LocalVersion { get; }

    /// <summary>最近一次检查到的 registry 最新版（未检查为 null）。</summary>
    public string? LatestVersion { get; private set; }

    /// <summary>最近一次检查是否发现有新版。</summary>
    public bool HasUpdate { get; private set; }

    /// <summary>最近一次检查是否成功（网络/解析失败为 false）。</summary>
    public bool LastCheckSucceeded { get; private set; }

    /// <summary>构造函数：解析本地版本（只读文件，无网络）。</summary>
    public HarnessUpdater();

    /// <summary>检查最新版：npm view。只读网络，不打断运行中的服务。耗时 1~10s（npm 自身网络重试可能更久），60s 硬超时兜底。</summary>
    public Task<bool> CheckAsync();   // true = 检查成功（是否有新版看 HasUpdate）

    /// <summary>执行更新：npm install -g @deepseek-ai/dsh@latest。
    /// 前置条件：调用方已 Shutdown 服务（§6.1）。输出经 Log 事件透出。</summary>
    public Task<bool> UpdateAsync();  // true = 安装成功
}
```

**依赖的路径解析**（复用 ServerController 现有逻辑，提为 internal 供两处调用）：

| 方法 | 现位置 | 改动 |
|---|---|---|
| `ResolveNodeExe()` | `ServerController` private static | 改 `internal static` |
| `ResolveDshBinJs()` 内的 npmDir 解析 | `ServerController` private static | 提为 `internal static string? ResolveNpmDir()`，bin.js 解析继续用它 |

**npm 命令统一执行器**（UpdateAsync / CheckAsync 共用，仿 `TryStartServer` 模式）：

```csharp
// node 目录 = Path.GetDirectoryName(ResolveNodeExe())
// npm-cli.js = <node目录>\node_modules\npm\bin\npm-cli.js（不存在则报"npm 组件缺失"）
// 参数示例：view @deepseek-ai/dsh version / install -g @deepseek-ai/dsh@latest
ProcessStartInfo {
    FileName = nodeExe, Arguments = $"\"{npmCli}\" {args}",
    UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardOutput = true, RedirectStandardError = true,
}
// 输出逐行进 Log；CheckAsync 需收集 stdout 最后一行作为版本号
```

## 5. 版本比较规则（semver 简化版）

`npm view @deepseek-ai/dsh version` 返回的是 `latest` dist-tag 对应的版本（若官方把 rc 当 latest 发布，则 rc 也可能出现在最新版里，比较规则必须覆盖）。

| 规则 | 判定 |
|---|---|
| 主/次/补丁数字段从左到右比较，先大者新 | `1.0.0` > `0.9.9` |
| 无 pre-release 段 > 有 pre-release 段（同数字段时） | `1.0.0` > `1.0.0-rc.6` |
| pre-release 段按 `.` 分段比较（数字段按数值、字母段按字典序） | `1.0.0-rc.7` > `1.0.0-rc.6` |
| 完全相同 | 无更新 |
| 任一侧解析失败 | 视为无更新（不误报），日志记录原始字符串 |

实现为 `Helpers` 下的静态 `SemVer.Compare(a, b)`（约 40 行，不引第三方包；npm 生态标准规则，未来有需要可换 `NuGet.Versioning` 之类）。build metadata（`+` 后缀）不参与比较，忽略。

**版本号解析健壮性**（CheckAsync 内）：`npm view` 输出取**最后一行非空行**，`Trim()` 后用正则 `^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$` 校验，不匹配视为检查失败（不误报更新）。npm 在输出重定向下不带 ANSI 颜色，无需剥离。

## 6. 更新执行细节

### 6.1 先停服（硬性前置）

Windows 文件占用：运行中的 `node.exe <bin.js> web` 进程持有 bin.js 及包内文件句柄，npm 覆盖安装会 EPERM 或半更新。
流程固定在调用方（MainWindow）：

```
确认更新 → _server.Shutdown()（复用现有逻辑，幂等）→ updater.UpdateAsync()
```

接管模式（外部 dsh）同理：Shutdown 会一并停掉已验证的 dsh；若被接管服务不是 dsh（非 dsh 占端口），**更新流程拒绝执行**（`Shutdown()` 不会清理它，且包目录可能正被它占用），提示"端口被非 dsh 服务占用，无法更新"。

**权限容错**：本机 npm prefix 为用户级（`%APPDATA%\npm`）无需提权；但目标机若 npm prefix 是系统级（如 `C:\Program Files\nodejs` 下的全局目录），`npm install -g` 会 EPERM——失败提示应包含指引"若为系统级全局目录，请用管理员身份手动执行 `npm install -g @deepseek-ai/dsh@latest`"。

### 6.2 安装与超时

- **不可中途取消**：确认弹窗即最终确认，安装开始后不提供取消（中断安装比让它跑完更危险）；唯一强制中断是超时兜底
- 命令：`npm install -g @deepseek-ai/dsh@latest`（显式 @latest，确保拿到 dist-tag 所指）
- 时长通常 10~60s（视网络），**5 分钟硬超时**兜底：超时 kill npm 进程树，判失败
- 输出重定向逐行进 Log：用户可在覆盖层日志/`app.log` 看到安装进度，npm 失败原因（网络/权限/EPERM）可查
- 更新期间顶栏"重启服务"等按钮禁用（防与更新流程竞争）；关窗不杀 npm 进程（中断安装比让它跑完更危险，见 §8）

### 6.3 安装后验证与重启

- **安装后验证**：`UpdateAsync` 退出码为 0 后，重读 `package.json` 的 `version`，确认已变为目标版本（或至少 ≠ 旧版本），否则判失败——防止 npm 静默失败（极端情况下 exit 0 但未装成）
- 成功：`EnsureServerAsync()` → `Navigate` 到端口地址（复用现有重试链路），完成后提示"已更新到 vY"；窗口记忆、主题不受影响
- 失败：npm 安装采用"先 stage 到临时目录、再替换包目录"的方式，失败/中断时包目录通常保持旧版本完好；但极端情况（磁盘满、断电、安装中途被杀）仍可能损坏。失败路径统一为：先 `EnsureServerAsync()` 恢复服务（能起来就用）→ 错误提示 + 日志入口，并指引"若服务异常，手动执行 `npm install -g @deepseek-ai/dsh@latest` 修复"

## 7. UI 与交互（已确认：入口只放菜单）

### 7.1 顶栏菜单下拉（`MainWindow`）

现有菜单项：关于 / 设置。新增一项 **"检查更新"**（工具按钮清单 §R6 的 5 个固定按钮不变，更新是菜单内条目，不新增顶栏按钮）。

| 状态 | 菜单项表现 | 点击行为 |
|---|---|---|
| 未检查过 | 检查更新 | 执行 CheckAsync（覆盖层不打断，完成后弹结果） |
| 检查中 | 检查更新…（禁用） | — |
| 检查成功、无新版 | 检查更新（可再点） | 弹窗"已是最新 vX" |
| 检查成功、有新版 | **更新可用：vX → vY**（高亮蓝） | 确认弹窗（当前/最新/更新说明/风险提示，并告知"更新期间服务中断、页面重新加载，约 1 分钟内"）→ 执行更新流程 |
| 检查失败 | 检查更新（可再点） | 弹窗"检查失败，请查看日志" |
| 更新中 | 正在更新 Harness…（禁用） | — |

### 7.2 启动自动检查（默认开，设置里可关）

- 触发时机：服务就绪、页面加载完成后（`NavigationCompleted` 之后），后台 `CheckAsync()`，**不阻塞启动**
- 防重入：同一会话内已成功检查过则不重复自动检查（刷新页面、顶栏重启服务后不触发）；自动检查进行中菜单"检查更新"禁用，手动点击忽略或等待
- 发现新版：不弹窗，菜单项直接变"更新可用：vX → vY"（高亮），用户点菜单才进确认
- 检查失败：静默（仅写日志），不打扰
- 开关持久化：`settings.json`（§8），默认 `true`

## 8. 数据与设置持久化

`settings.json`（`%LOCALAPPDATA%\dsh-app\`）现有 `Theme` 字段，增加 `AutoCheckUpdate`：

```json
{ "Theme": "System", "AutoCheckUpdate": true }
```

**改动**：`ThemeManager` 内私有 `Settings` 类提升为共享类 `Helpers/AppSettings.cs`（`Theme` + `AutoCheckUpdate`，读写逻辑从 ThemeManager 迁入），ThemeManager 与 HarnessUpdater 共用同一文件与读写方法；默认值保持与现状一致（`Theme = System`、`AutoCheckUpdate = true`）。设置窗口在主题区下方加一行"启动时自动检查更新"开关（沿用现有滑块开关风格），变更即保存。

## 9. 错误处理与安全边界

| 场景 | 行为 |
|---|---|
| 网络失败 / registry 超时 | CheckAsync 返回失败（60s 兜底）；自动检查静默、手动检查弹"检查失败，请确认网络/代理后重试"；不影响服务运行 |
| 本地版本解析失败（包被删/损坏） | `LocalVersion = null`，检查照常；确认弹窗显示"本地版本未知" |
| 更新时服务是外部启动且非 dsh | 拒绝更新（见 §6.1），提示原因 |
| npm 安装失败（EPERM/网络/权限） | 判失败 → 先重启旧服务恢复可用 → 错误提示 + 日志 |
| 安装后版本验证不一致 | 判失败，同上恢复路径；日志记录实际版本与预期版本 |
| 更新期间用户关窗 | **拦截 + 确认**：OnClosing 弹"中断并关闭 / 继续更新"（半安装比跑完更危险）；确认中断 → `AbortRunningNpm` 终止安装进程 → 关闭并记录日志；选择继续 → 等待更新完成后再关（Dispose 不杀进程，普通退出让 npm 自然收尾） |
| 更新期间单实例 | Mutex 已保证无并发 |
| 自动检查开关 | 关闭后启动零网络请求，菜单手动检查仍可用 |

**红线**：自动检查**只提示不自动安装**——任何安装动作必须经过用户确认弹窗。

## 10. 改动清单（实现时对照）

| 文件 | 改动 |
|---|---|
| `Server/HarnessUpdater.cs` | **新增**：检查/更新/版本解析/SemVer 比较 |
| `Helpers/AppSettings.cs` | **新增**：从 ThemeManager 抽出共享设置类 |
| `Server/ServerController.cs` | `ResolveNodeExe` 改 internal；提 `ResolveNpmDir()` |
| `Helpers/ThemeManager.cs` | `Settings` 私有类移除，改用 `AppSettings` |
| `MainWindow.xaml(.cs)` | 菜单加"检查更新"项 + 状态机（检查/更新/结果）；自动检查编排；更新流程（确认→Shutdown→Update→Ensure→Navigate） |
| `Views/SettingsWindow.xaml(.cs)` | 加"启动时自动检查更新"开关 |
| `README.md` / `ARCHITECTURE.md` | 实现后同步：README 特性与关键行为表；ARCHITECTURE 模块表与调用链路 |
| 本文件 | 实现完成后状态改"已实现"，更新实际版本号与细节偏差 |

## 11. 验证清单（沿用冒烟 + 对抗风格）

1. **检查**：有新版（菜单变"更新可用"）/ 无新版（"已是最新"）/ 断网（手动弹失败、自动静默）
2. **更新成功**：覆盖层进度 → 服务重启 → 页面可用 → 版本号确认变新 → 日志完整
3. **更新失败模拟**（如断网中途）：旧服务恢复、错误提示、可重试
4. **设置开关**：关后重启零网络请求（抓包/日志确认）；开则后台检查不阻塞启动（启动到可对话时间不显著增加）
5. **对抗**：更新中关窗（npm 不被杀、下次启动新版本）；外部非 dsh 占端口时拒绝更新；接管模式更新（外部 dsh 先被停、装完重启）
6. **回归**：主题三模式、窗口记忆、单实例、心跳断连不受影响

## 12. 后续可选（本期不做）

- 更新后壳窗口不关闭、页面重新加载（本期直接走 Ensure + Navigate，可接受；未来可考虑保留页面会话）
- 更新历史/回滚到上一版本（npm 无内置，需留存旧包快照，成本高）
- 更新失败自动重试一次

---

## 实现记录（2026-08-14）

- `Server/HarnessUpdater.cs`：核心逻辑按本文档实现。与设计的偏差：SemVer 比较为类内 private static（未抽 `Helpers/SemVer.cs`，语义一致）；`UpdateAsync` 成功后 `HasUpdate` 复位、`LatestVersion` 与 `LocalVersion`（改可变）更新为实装版本。
- `MainWindow`：菜单新增"检查更新"项（有新版时高亮"更新可用：vX → vY"，高亮色走 `AccentBlueBrush` 资源）；手动点击**总是重新检查**（不复用启动时的陈旧结果）；更新流程 = 收起 WebView2（R1）→ ShowLoading → Shutdown → UpdateAsync → EnsureServerAsync → Navigate；更新中禁用"重启服务"；`NavigationCompleted` 后后台自动检查一次（默认开，设置页可关，防重入）。
- 更新中关窗：OnClosing 拦截 + ConfirmDialog 确认（"中断并关闭/继续更新"）→ 确认后 `AbortRunningNpm` 终止安装进程并记录日志；`Dispose` 不杀进程（让更新跑完的设计仅在用户确认中断时被打破）。
- 非 dsh 占用端口（`IsManaged == false`）时拒绝更新：弹窗说明原因（无法安全停服）。
- 更新中：禁用"重启服务"与"刷新"（防与更新流程并发 EnsureServerAsync）；菜单项文案"正在更新 Harness…"；更新成功弹"已更新到 vY"提示。
- `HarnessUpdater.LastError`：检查/更新失败原因透出（UI 提示用，替代笼统的"请确认网络"）。
- `SettingsWindow`：新增"启动时自动检查更新"开关（`AppSettings.AutoCheckUpdate`）。
- 共享设置迁移至 `Helpers/AppSettings.cs`（Theme + AutoCheckUpdate + 余额字段；Lazy 加载 + 临时文件原子替换），`ThemeManager` 改用它。
- 冒烟：npm view 链路返回 `0.1.0-rc.6`（与本地一致，不误报）；单实例/启动链路回归通过。
