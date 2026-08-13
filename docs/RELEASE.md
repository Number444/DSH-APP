# dsh-app 发布流程（记忆文件）

> 发布三步：**① commit → ② 发布单 exe → ③ push（由主人确定）**
> 依据：README「构建与发布」、ARCHITECTURE「self-contained 单文件发布」决策、~/.dsh/skills/dsh-app.md 技能档案。

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

## 第二步：发布单 exe

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# 产物：bin\Release\net9.0-windows\win-x64\publish\dsh-app.exe（~127MB，单文件拷走即用）
```

**前置检查**：
- 无运行中的 dsh-app 实例（exe 被进程锁定会发布失败——MSB3027 文件占用错误）
- 无未提交改动（发布产物应对应已 commit 的代码）

**发布后冒烟**（产物 exe，非 bin 目录版本）：
1. 启动产物 → 覆盖层进度 → 页面加载完成（3080 就绪）
2. 关窗 → 3080 释放、无残留进程（node/dsh-app）
3. 主要功能抽查：菜单（日志/检查更新/设置/关于）、余额显示（若开启）、主题切换

## 第三步：push（**由主人确定**）

```powershell
git push origin main
```

- **只有主人明确说"push"才执行**；未确认绝不 push
- push 命令**不带 HTTPS_PROXY 前缀**（全局红线）
- push 前可先 `git log --oneline -3` 确认要推的提交

## 部署到另一台电脑

1. 拷 `dsh-app.exe` 到目标机任意目录（如 `D:\dsh-app\`），免装 .NET
2. 目标机前置：Node.js + `@deepseek-ai/dsh` 全局安装 + `~/.dsh` 配置完整 + WebView2 Runtime（Win11 随 Edge 预装）
3. 桌面快捷方式指向 exe，双击即用；壳内无绝对路径硬编码，双机通用
