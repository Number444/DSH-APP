# dsh-app 应用自更新机制（设计文档）

> 状态：**已实现（2026-08-14）** · 版本 v1.3.0
> 本文档描述"壳自身（dsh-app.exe）通过 GitHub Releases 自动更新"的完整方案。
> 与 Harness 更新（npm 包）正交，见 [UPDATE-MECHANISM.md](UPDATE-MECHANISM.md)。

---

## 1. 背景与目标

dsh-app 是单 exe 发布（self-contained 单文件，约 148MB），旧版升级只能手动替换文件。
目标：壳内完成"检查 GitHub Releases → 下载 → SHA256 校验 → 覆盖单 exe → 自动重启"，全程可见。

1. **检查**：本地壳版本 vs GitHub Releases `latest`（手动 + 启动后台自动）
2. **更新**：用户确认 → 下载（不打断服务）→ 校验 → 更新器覆盖 → 自动重启
3. **回滚兜底**：新 exe 启动失败自动回滚旧版 + 用户可见提示

**红线**：只提示不自动安装——任何安装动作必须经用户确认弹窗（与 Harness 更新一致）。

## 2. 现状事实

| 项 | 值 | 影响 |
|---|---|---|
| 运行中 exe 被锁定 | Windows 文件锁 | 覆盖必须由独立进程在旧实例退出后执行（更新器脚本） |
| 仓库 | github.com/Number444/DSH-APP（公开） | 未认证 API 限流 60 次/时（启动检查一次，可接受） |
| 网络 | github 直连实测可达；Clash 7890 曾失效 | 检查策略：直连优先，7890 TCP 可达时失败重试代理 |
| 本机代理 | npm 显式 7890 | 壳侧不依赖 npm 配置（.npmrc 含凭据风险，不读取） |
| 版本来源 | csproj `<Version>` → InformationalVersion（SDK 生成，`IncludeSourceRevisionInInformationalVersion=false` 免 +sha） | 壳与 Release tag 版本号同源 |

## 3. 总体流程

```
手动菜单"检查应用更新" ─┐   ┌─ 启动后台自动检查（默认开，设置可关）
                        ▼     ▼
              CheckAsync()：GET api.github.com/.../releases/latest
              （直连 → 失败且 7890 可达 → 代理重试一次；60s 超时）
                        │
          tag 正则 ^v\d+\.\d+\.\d+$ + asset dsh-app.exe/.sha256 必须齐全
          （发布不完整 = fail-closed 检查失败，不误报）
                        │
            ┌───────────┴───────────┐
            ▼                       ▼
      有新版（vX → vY）        无新版/404 → "已是最新 vX" / 静默
            │
        确认弹窗（当前/最新/说明：下载不打断服务，
        重启时服务短暂中断 10~30 秒）
            │
            ▼
     R1 收起 WebView → 覆盖层"正在下载应用更新…"
     （进度 MB + 百分比 + 实时日志 + 取消按钮）
            │
            ▼
     流式下载 .part（128KB 块 + IncrementalHash 同遍 SHA256，15min 超时）
     → 下载 .sha256（严格 64 位 hex）→ 比对 → 通过才 .part → .new
     （磁盘预检 ≥600MB；失败/取消即删 .part）
            │
     窗口可见？──否──→ 托盘气泡"已下载完成，恢复窗口后安装" + _appUpdateReady
            │（是）
            ▼
     生成 apply-update.ps1（内嵌模板 + 参数注入，UTF-8 BOM）→ 启动更新器（不等待）
     → _quitForAppUpdate = true → Close()
            │
            ▼
     更新器（独立 PowerShell 进程）：
     等所有 dsh-app 退出（60s 超时则放弃覆盖）
     → 备份旧 exe → 覆盖为新 → 启动 → 45s 双条件验证（进程 + 端口 HTTP）
     → 成功：清理 .new/备份/脚本自身（保留 updater.log）
     → 失败：终止新进程 → 备份覆盖回 → 启动旧版 → 写 rolled-back.flag
            │
            ▼
     新实例启动：检测 rolled-back.flag → 弹"已自动回滚到 vX"说明 → 删除标记
```

## 4. 模块设计

### 4.1 `Server/AppUpdater.cs`（实例类，MainWindow 持单实例）

| 成员 | 说明 |
|---|---|
| `CheckAsync()` | releases/latest 检查（60s 超时；404=无 Release 视为成功无更新；tag 正则 + 双 asset 校验 fail-closed）；`HasUpdate = SemVer.Compare(version, LocalVersion) > 0` |
| `DownloadAndVerifyAsync(progress, ct)` | 磁盘预检 → 流式下载 .part（同遍 SHA256）→ .sha256 严格解析比对 → .part→.new；失败/取消删 .part |
| `WriteUpdateScript(port)` | 提取内嵌模板 `dsh_app.Scripts.apply-update.ps1`（与仓库 `scripts/apply-update.ps1` 同源），参数注入写盘（**UTF-8 带 BOM**：PS5.1 无 BOM 按 ANSI 解析中文会语法错误，实测踩坑） |
| 属性 | `LocalVersion`/`LatestVersion`/`HasUpdate`/`LastCheckSucceeded`/`LastError`/`DownloadedNewExePath` |
| 网络 | 共享 HttpClient（连接池，Timeout 不设，per-call CTS）；代理重试走单独 handler（WebProxy 127.0.0.1:7890） |
| 并发 | SemaphoreSlim(1,1) 串行检查/下载；Dispose 不 Dispose SemaphoreSlim（仿 HarnessUpdater） |

### 4.2 `scripts/apply-update.ps1`（更新器模板，入库 + 嵌入）

参数：`-NewExe -AppExe -BackupDir -LogFile [-Port]`；测试参数：`-WaitProcessName -SkipStart -NoVerify`（默认值保持生产行为）。

| 步骤 | 行为 | 失败语义 |
|---|---|---|
| ① 等退出 | 轮询 60s | 超时 exit 2：不覆盖，.new 保留 |
| ② 备份 | Copy 到 backup\dsh-app.\<FileVersion\>.exe | 失败 exit 3 |
| ③ 覆盖 | Copy -Force | 失败：回滚 + exit 4 |
| ④ 启动 | Start-Process | 失败记日志（验证会兜底） |
| ⑤ 验证 | 45s 双条件（进程存在 + 端口 HTTP 就绪，冷启动最坏 30s 留缓冲） | 失败：终止新进程 → 回滚 → rolled-back.flag → 启动旧版 → exit 5 |
| ⑥ 清理 | 成功路径删 .new/备份/脚本自身 | 保留 updater.log |

**职责边界**：备份/清理全归脚本；应用 OnStartup 只清 `*.part/*.new/*.sha256` 下载残留——防"新实例清理删掉备份致回滚失效"竞态。

### 4.3 状态机（MainWindow）

- `_appChecking` / `_appUpdating` / `_appUpdateReady` / `_quitForAppUpdate` / `_appAutoCheckRan`（与 Harness 完全拆分）
- 统一入口守卫：任一检查/更新进行中（两套），两个更新入口均禁用
- `_quitForAppUpdate`：OnClosing 中位于 App.ForceExit 之后、托盘化分支之前短路放行（**默认托盘化会 e.Cancel+Hide 拦截 Close → 更新器空等 60s 静默失败**，三路评审共同确认的致命点）
- 下载中关窗（Hide）：不取消，下载继续 → 完成弹气泡 + `_appUpdateReady`；恢复窗口/菜单点击 → 确认安装
- 真退出（托盘退出/关窗直退）：CTS.Cancel + 清理（残留由下次启动兜底）

## 5. 安全边界

| 场景 | 行为 |
|---|---|
| tag 非法 | 检查失败（不误报更新） |
| Release 版本 ≤ 本地 | "已是最新"（**不校验资产**——旧 Release 无资产不得误报"发布不完整"，实测修正项） |
| 有新版但 asset 缺失 | 检查失败（不误报更新）；"发布不完整"提示 |
| .sha256 非法 / 哈希不匹配 | 拒绝安装，删 .part |
| 磁盘不足（<600MB） | 快速失败不下载 |
| 下载超时（15min）/ 取消 | 删 .part；服务不受影响 |
| 新 exe 启动即崩 | 45s 验证失败 → 自动回滚 + 标记文件 + 下次启动说明弹窗 |
| 旧实例不退出 | 60s 超时放弃覆盖（不半覆盖） |
| 覆盖 EPERM | 回滚 + 日志 |
| 更新器启动失败（壳侧） | 不退出应用，恢复 UI，服务不受影响 |
| 托盘化期间下载完成 | 气泡提示 + 待安装状态，不强制弹窗 |
| 更新期间单实例 | 互斥体名/进程名不变，无冲突 |

## 6. 发布侧（GitHub Release）

独立脚本 `scripts/create-release.ps1`（发布第四步，push 之后运行；`release-publish.ps1` 保持发布+交付单一职责）：

1. 校验 `HEAD == origin/main`（禁止未推送 HEAD 打 tag）
2. 生成 `dsh-app.exe.sha256`：**裸 64 位 hex**（`Get-FileHash` 输出 .Hash.ToLower()，无文件名——契约三处钉死：发布脚本 / `AppUpdater.Sha256Regex` / 本文档）
3. gh CLI 检测（`gh --version` + `gh auth status`）；不可用 → 手动命令指引 exit 3
4. tag 已存在 → 幂等跳过
5. 确认框（创建公开 Release 是公开动作）→ `gh release create v<版本> dsh-app.exe dsh-app.exe.sha256 --repo Number444/DSH-APP --title ... --notes <docs/CHANGELOG.md 最新节>`

## 7. 版本号

- 壳：csproj `<Version>1.3.0</Version>` + `IncludeSourceRevisionInInformationalVersion=false` → `App.AppVersion`（读 InformationalVersion，仍做去 `+` 兜底）
- Release tag：`v<Version>`（壳侧正则 `^v\d+\.\d+\.\d+$` 校验）
- 比较：`Helpers/SemVer.cs`（与 Harness 共用；pre-release 规则：rc < stable）

## 8. 验证清单（已执行/待执行）

- [x] 更新器对抗测试（测试目录副本，不影响生产实例）：
  - 成功路径：exit 0、覆盖、.new 删除、备份清理、脚本自删
  - 回滚路径：45s 验证失败 → exit 5、恢复旧版、rolled-back.flag
  - 等待超时：生产实例在跑 → 60s 放弃不覆盖（exit 2）
  - 覆盖失败：EPERM → 回滚（exit 4）
- [ ] 端到端：发布 v1.3.0 → create-release → 检查链路（已是最新）→ 下一版发布时真实自更新验收
- [ ] 对抗：下载中断/断网、哈希篡改、磁盘不足、取消下载、托盘化期间下载、更新中关窗
- [ ] 回归：Harness 更新/余额/托盘/单实例/主题/窗口记忆

## 9. 后续可选（本期不做）

- 断点续传；更新历史/回滚到任意版本；发布渠道切换；签名校验（Authenticode）
