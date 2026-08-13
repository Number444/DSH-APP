# dsh-app — DeepSeek Harness 桌面壳

> v1.1.0(2026-08-14)

把 DeepSeek Harness Web GUI 封装成独立 Windows 桌面应用(WPF + WebView2,纯壳零侵入)。

## 它做什么

双击 `dsh-app.exe` → 自动拉起 `dsh web` 服务器(若未运行)→ 无地址栏独立窗口加载 Harness UI → 关闭窗口即停(自家拉起的 + 接管验证过的 dsh 一并停止)。

## 特性

- **自绘顶栏**(WindowChrome):纯文字工具按钮(刷新 / 浏览器 / 重启服务 / 菜单;日志入口在菜单内)
- **启动状态机**:5 步进度(探测 → 定位环境 → 拉起 → 就绪 → 加载),失败定位到具体步骤
- **断连检测**:自家进程崩溃(`ServerDied`)与接管服务心跳(30s×2 次)双通道
- **接管身份验证**:外部 dsh 经 PID + 命令行双重验证后才接管清理,非 dsh 程序绝不误杀
- **主题系统**:深色 / 浅色 / 跟随系统(读系统主题 + 实时监听),全 DynamicResource 动态切换
- **Harness 更新**:顶栏菜单"检查更新"(发现新版高亮提示)+ 启动后台自动检查(默认开,设置可关);用户确认后停服 → `npm install -g` → 自动重启,全程日志可见
- **顶栏余额显示**:菜单右侧常驻 DeepSeek 开放平台剩余资金(¥),60s 自动刷新 + 点击手动刷新;API Key 经用户确认授权后自动读取 dsh 凭据,或设置页手动填写(DPAPI 加密)
- **菜单下拉**:关于 / 设置 / 检查更新(胶囊分段 + 滑块开关,套用 Toolbox 设计)
- **窗口记忆**:位置/尺寸/最大化状态持久化(防外接屏拔除后窗口丢失)
- **自定义滚动条**:Toolbox 同款四档过渡(拖拽 > 悬停 Thumb > 悬停轨道 > 常态)

## 关键行为

| 场景 | 行为 |
|---|---|
| 服务器未运行(3080 无服务) | 隐藏窗口启动 `dsh web`(直连 node bin.js,不依赖 PATH),轮询就绪后加载页面 |
| 服务器已在运行 | 识别身份:dsh → 接管(关窗一并停止);非 dsh → 直连不清理 |
| 3080 被占用 | 并发探测 3081~3090(最坏 ~1s);都不可用则提示错误 |
| 服务运行中崩溃 | 错误卡片"服务连接已断开" + 重试(接管模式靠心跳感知) |
| 渲染进程崩溃 | 自动刷新一次,再崩溃 → 错误卡片 |
| 重复双击 | 单实例,激活已有窗口 |
| 环境缺失(未装 node/dsh) | 错误卡片直接给出安装步骤指引 |

日志位置:`%LOCALAPPDATA%\dsh-app\app.log`(服务器输出与壳日志)。

## 构建与发布

```powershell
# 开发构建
dotnet build -c Release

# 发布:self-contained 单 exe(目标机免装 .NET,拷走即用)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# 产物: bin\Release\net9.0-windows\win-x64\publish\dsh-app.exe (~127MB)
```

## 部署到另一台电脑

目标机前置条件(通常已具备):
- Node.js + `@deepseek-ai/dsh` 全局安装,`~/.dsh` 配置完整(未装则错误卡片会给出安装指引)
- WebView2 Runtime(Win11 随 Edge 预装;缺则装官方 Evergreen 引导程序)
- **不需要** .NET(已 self-contained 单文件)

步骤:
1. 把 `dsh-app.exe` 拷到目标机任意目录(如 `D:\dsh-app\`)
2. 桌面新建快捷方式,目标指向 `dsh-app.exe`
3. 双击即用

> 壳内无任何绝对路径硬编码,服务器环境由 `dsh web` 按本机 profile 解析,双机通用。

## 文档

- [ARCHITECTURE.md](ARCHITECTURE.md) — 架构、调用链路、设计决策
- [docs/UI-GUIDELINES.md](docs/UI-GUIDELINES.md) — 窗口架构与 UI 规范(R1~R8 铁律)
- [docs/UPDATE-MECHANISM.md](docs/UPDATE-MECHANISM.md) — Harness 更新机制设计（已实现）
- [docs/BALANCE-DISPLAY.md](docs/BALANCE-DISPLAY.md) — 顶栏余额显示设计（已实现）
- [docs/RELEASE.md](docs/RELEASE.md) — 发布流程（commit → 单 exe → push 由主人确定）

## 回退

原浏览器方式不受影响:服务器可继续用 `dsh-web.bat` 启动(快捷方式备份在 `DeepSeek Harness.lnk.bak`)。
