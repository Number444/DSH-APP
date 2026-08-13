# dsh-app — DeepSeek Harness 桌面壳

把 DeepSeek Harness Web GUI 封装成独立 Windows 桌面应用(WPF + WebView2)。

## 它做什么

双击 `dsh-app.exe` → 自动拉起 `dsh web` 服务器(若未运行)→ 无地址栏独立窗口加载 Harness UI → 关闭窗口即停(仅停本应用拉起的服务器)。

## 关键行为

| 场景 | 行为 |
|---|---|
| 服务器未运行(3080 无服务) | 隐藏窗口启动 `dsh web`,轮询就绪后加载页面 |
| 服务器已在运行(如旧启动器) | 直接连接,退出时**不**杀它 |
| 3080 被占用 | 自动探测 3081~3090;都不可用则提示错误 |
| 重复双击 | 单实例,激活已有窗口 |
| 页面进程崩溃 | 错误卡片 + 重试 |

日志位置:`%LOCALAPPDATA%\dsh-app\app.log`(服务器输出与壳日志)。

## 构建与发布

```powershell
# 开发构建
dotnet build -c Release

# 发布(self-contained,目标机免装 .NET,可拷到任意 Win10/11)
dotnet publish -c Release -r win-x64 --self-contained true
# 产物: bin\Release\net9.0-windows\win-x64\publish\
```

## 部署到另一台电脑

目标机前置条件(通常已具备):
- Node.js + `@deepseek-ai/dsh` 全局安装,`~/.dsh` 配置完整
- WebView2 Runtime(Win11 随 Edge 预装;缺则装官方 Evergreen 引导程序)
- **不需要** .NET(已 self-contained)

步骤:
1. 把 `publish\` 整个文件夹拷到目标机任意目录(如 `D:\dsh-app\`)
2. 桌面新建快捷方式,目标指向 `dsh-app.exe`
3. 双击即用

> 壳内无任何绝对路径硬编码,服务器环境由 `dsh web` 按本机 profile 解析,双机通用。

## 回退

原浏览器方式不受影响:服务器可继续用 `dsh-web.bat` 启动(快捷方式备份在 `DeepSeek Harness.lnk.bak`)。
