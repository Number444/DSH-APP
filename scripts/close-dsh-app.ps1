# close-dsh-app.ps1 — 关闭 dsh-app 壳进程（发布流程前置步骤，保持 3080 服务存活）
#
# 用法：powershell -ExecutionPolicy Bypass -File scripts\close-dsh-app.ps1
#
# 关键机制：当前 Web GUI 会话（AI 工具通道）运行在 3080 服务上，而该服务由
# dsh-app 实例拉起。因此本脚本【只强杀 dsh-app.exe 本体，不带 /T】——
# 它拉起的 dsh web(node) 进程成为孤儿继续占用 3080，保证 GUI 会话不中断；
# 发布完成后新实例启动会自动接管该服务（身份验证通过，关窗时一并清理）。
# 若孤儿服务意外退出（管道 EPIPE 等），脚本会尝试独立拉起 dsh web 恢复 3080。
#
# 退出码：0 = 壳进程已关闭（3080 服务存活或已兜底恢复）；1 = 失败（人工处理）。

$ErrorActionPreference = 'Continue'

# ---- 定位 node.exe 与 dsh 入口（与 ServerController 解析思路一致） ----
function Resolve-NodeExe {
    $cmd = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($c in @("$env:ProgramFiles\nodejs\node.exe", "$env:LOCALAPPDATA\Programs\nodejs\node.exe")) {
        if (Test-Path $c) { return $c }
    }
    return $null
}
function Resolve-DshBinJs {
    foreach ($dir in @("$env:APPDATA\npm", "$env:ProgramFiles\nodejs")) {
        $bin = Join-Path $dir 'node_modules\@deepseek-ai\dsh\lib\bin.js'
        if (Test-Path $bin) { return $bin }
    }
    return $null
}
function Start-DshWebFallback {
    $node = Resolve-NodeExe
    $bin = Resolve-DshBinJs
    if (-not $node -or -not $bin) {
        Write-Warning '[close-dsh-app] node.exe or dsh bin.js not found; cannot restart 3080 fallback'
        return $false
    }
    Write-Host "[close-dsh-app] restarting dsh web: $node $bin web"
    Start-Process -FilePath $node -ArgumentList "`"$bin`" web" -WindowStyle Hidden | Out-Null
    return $true
}

# ---- 强杀壳进程（不带 /T：保留其拉起的 node 服务，3080 不断） ----
$proc = Get-Process -Name 'dsh-app' -ErrorAction SilentlyContinue
if (-not $proc) {
    Write-Host '[close-dsh-app] no running instance' -ForegroundColor Green
}
else {
    Write-Host "[close-dsh-app] killing shell process PID $($proc.Id) (no /T: keep its node service alive on 3080)..."
    taskkill /PID $proc.Id /F | Out-Null
    Start-Sleep -Seconds 2
    if (Get-Process -Name 'dsh-app' -ErrorAction SilentlyContinue) {
        Write-Error '[close-dsh-app] shell process still running; handle manually'
        exit 1
    }
    Write-Host '[close-dsh-app] shell process closed OK' -ForegroundColor Green
}

# ---- 检查 3080：存活则 GUI 通道保持；死亡则兜底拉起 ----
Start-Sleep -Seconds 2
$listening = netstat -ano | Select-String ':3080' | Select-String 'LISTENING'
if ($listening) {
    Write-Host '[close-dsh-app] port 3080 still alive (GUI session uninterrupted) OK' -ForegroundColor Green
}
else {
    Write-Warning '[close-dsh-app] port 3080 is down (service died with the shell); starting fallback dsh web...'
    if (Start-DshWebFallback) {
        Start-Sleep -Seconds 3
        if (netstat -ano | Select-String ':3080' | Select-String 'LISTENING') {
            Write-Host '[close-dsh-app] fallback dsh web up on 3080 OK' -ForegroundColor Green
        }
        else {
            Write-Warning '[close-dsh-app] fallback start pending or failed; continuing (publish will still run)'
        }
    }
}
exit 0
