# dsh-app 发布流程（记忆文件）

> 发布三步：**① commit → ② 一键发布脚本（release-publish.ps1）→ ③ push（由主人确定）**
> 依据：README「构建与发布」、ARCHITECTURE「self-contained 单文件发布」决策、~/.dsh/skills/dsh-app.md 技能档案。
> **铁律：第二步必须跑脚本，不得以"环境特殊/无实例/手动更可控"等现场判断为由改用裸命令**——除非主人明确指示（2026-08-14 教训：手动执行偏离规范，主人要求一律走脚本）。
> GitHub Release 由**主人手动发布**（v1.3.0 起；gh CLI 登录不可用，自动创建流程已删除）——手动发布指引见文末附录。

---

## 第一步：commit

```powershell
cd C:\Agent Space\dsh-app
git add -A
git commit -m "feat/refactor: <本次改动摘要>"
```

- **红线**：git commit/push 必须经主人明确授权；手动 git，不自动提交
- 提交前检查：`git status --short` 确认变更范围（bin/obj 已被 .gitignore 忽略，不会误入）
- 消息风格参照历史：`feat: ...` / `refactor: ...`，正文列关键点

## 第二步：一键发布（**必须跑脚本**）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release-publish.ps1
```

**脚本做什么**（全程无人值守，桌面弹出 GUI 进度窗口：步骤日志 / 状态 / 进度条 / 结果框）：
1. 检查未提交改动（仅警告不阻断）
2. 关闭壳进程并腾空 3080（close-dsh-app.ps1：杀壳 + 杀 3080 上的 dsh 服务——v1.7.0 launch token 时代新壳无法接管旧服务，孤儿服务必须杀掉腾端口；**Web GUI 会话随之中断，新壳就绪后刷新/重开页面恢复**；只杀命令行含 dsh/bin.js 的进程，非 dsh 占用则报错绝不误杀）
3. `dotnet publish` 单 exe（self-contained，产物 `bin\Release\net9.0-windows\win-x64\publish\dsh-app.exe`）
4. 启动产物 → 轮询 3080 就绪（≤30s；token 时代裸请求 401 即视为就绪——服务活着且鉴权生效）→ **交付：产物窗口保持打开**（最新版即当前窗口，自带 token 的新服务）

**规范约束**：
- 第二步**一律执行此脚本**（含 GUI 进度窗口与产物交付），不手动逐条敲 publish 命令
- 脚本执行环境：`powershell`（Windows PowerShell 5.1）；耗时 1~3 分钟；后台 job 跑时 GUI 窗口照常弹出
- 脚本内不含 push（第三步由主人确定后手动执行）
- 若脚本失败：按结果框/进度日志排查（常见：exe 被实例锁定、publish 编译错误、3080 未在 30s 内就绪）

**可选额外冒烟**（脚本已内置就绪验证；深度验证在无实例时另测）：
1. 产物窗口托盘化关窗 → 进程存活、3080 继续
2. 双向互斥抽查：新版本先启动 → 旧版被 Global 互斥体拦；旧版先启动 → 新版本被进程扫描拦
3. 菜单（日志/检查更新/设置/关于）、余额（若开启）、主题切换抽查

## 第三步：push（**由主人确定**）

```powershell
git push origin main
```

- **只有主人明确说"push"才执行**；未确认绝不 push
- push 命令**不带 HTTPS_PROXY 前缀**（全局红线）；若 git 配置的代理失效（Clash 挂起），用 `git -c http.proxy= -c https.proxy= push origin main` 直连（2026-08-14 实测：github 直连可达）
- push 前可先 `git log --oneline -3` 确认要推的提交

## 第四步：~~创建 GitHub Release~~（已删除，主人手动发布）

> v1.3.0 起 gh CLI 无法登录（浏览器拉起失败），自动创建流程（scripts/create-release.ps1）已删除。
> 自更新仍依赖 Release（GitHub API `releases/latest`），每次发版后**由主人手动发布**，否则壳侧检查更新不可用。

### 手动发布指引（主人执行 / 艾薇提供材料）

1. **创建 Release**（tag 必须 `v<版本>`，正则 `^v\d+\.\d+\.\d+$`）：
   - 网页 Draft：tag `v<版本>` + 上传 `dsh-app.exe`（**只需这一个资产**——校验锚是 GitHub 自动计算的资产 `digest`，壳侧直接读 API 字段）+ notes 取 `docs/CHANGELOG.md` 最新节
   - 或命令：`gh release create v<版本> <publish>\dsh-app.exe --repo Number444/DSH-APP --title "dsh-app v<版本>" --notes "<CHANGELOG 最新节>"`

**说明**：缺 exe 资产时壳侧 fail-closed 不误报。Release 创建失败不阻塞已交付的窗口，但下次检查更新会不可用——发布后请补建。

> 历史注记：v1.3.0~v1.7.0 的旧契约要求随附 `.sha256` 校验文件——发布侧从未上传过，壳内应用更新因此从未真正成功（fail-closed，2026-09-06 实锤）；v1.7.1 起退役，改用 API 资产 digest。**过渡期例外**：若需让 ≤v1.7.0 的旧壳壳内升级到新版，该版 Release 仍需补传旧格式的 `dsh-app.exe.sha256`（裸 64 hex）。

## 部署到另一台电脑

1. 拷 `dsh-app.exe` 到目标机任意目录（如 `D:\dsh-app\`），免装 .NET
2. 目标机前置：Node.js + `@deepseek-ai/dsh` 全局安装 + `~/.dsh` 配置完整 + WebView2 Runtime（Win11 随 Edge 预装）
3. 桌面快捷方式指向 exe，双击即用；壳内无绝对路径硬编码，双机通用
