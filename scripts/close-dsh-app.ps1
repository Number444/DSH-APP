# close-dsh-app.ps1 — 关闭 dsh-app 壳进程并腾空 3080（发布流程前置步骤）
#
# 用法：powershell -ExecutionPolicy Bypass -File scripts\close-dsh-app.ps1
#
# v1.7.0（harness v0.1.2-alpha.1 launch token 时代）机制变更：
#   旧逻辑"只杀壳、留孤儿 node 服务保 GUI 会话"的前提是——新实例能接管旧服务。
#   launch token 只存在于拉起进程的 stdout，接管已归档：孤儿服务不杀，
#   新壳启动即撞"端口被占用"错误卡。因此本脚本现在【杀壳 + 杀 3080 上的 dsh 服务】，
#   把端口腾空交给新壳拉起带 token 的新服务。
#   代价：当前 Web GUI 会话在发布期间断开，新壳就绪后刷新/重开 GUI 页面即恢复。
#
# 安全边界：只杀命令行含 dsh/bin.js 的 node 进程；3080 上是非 dsh 程序时报错退出，
# 绝不误杀。
#
# 退出码：0 = 壳已关闭且 3080 已腾空（或本就无占用）；1 = 失败（人工处理）。

$ErrorActionPreference = 'Continue'

# ---- 强杀壳进程（所有实例） ----
$procs = @(Get-Process -Name 'dsh-app' -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) {
    Write-Host '[close-dsh-app] no running shell instance' -ForegroundColor Green
}
else {
    foreach ($p in $procs) {
        Write-Host "[close-dsh-app] killing shell process PID $($p.Id)..."
        taskkill /PID $p.Id /F | Out-Null
    }
    Start-Sleep -Seconds 2
    if (Get-Process -Name 'dsh-app' -ErrorAction SilentlyContinue) {
        Write-Error '[close-dsh-app] shell process still running; handle manually'
        exit 1
    }
    Write-Host '[close-dsh-app] shell process closed OK' -ForegroundColor Green
}

# ---- 腾空 3080：只杀 dsh 服务（身份验证：命令行含 dsh\bin.js） ----
Start-Sleep -Seconds 1
$line = netstat -ano | Select-String 'LISTENING' | Select-String ':3080\s' | Select-Object -First 1
if (-not $line) {
    Write-Host '[close-dsh-app] port 3080 already free OK' -ForegroundColor Green
    exit 0
}

$svcPid = [int](($line -split '\s+')[-1])
$cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$svcPid" -ErrorAction SilentlyContinue).CommandLine
if ($cmd -and $cmd -match 'dsh' -and $cmd -match 'bin\.js') {
    Write-Host "[close-dsh-app] stopping dsh service PID $svcPid (token era: new shell cannot adopt, port must be freed)..."
    taskkill /PID $svcPid /F | Out-Null
    Start-Sleep -Seconds 1
    if (netstat -ano | Select-String 'LISTENING' | Select-String ':3080\s') {
        Write-Error '[close-dsh-app] port 3080 still occupied after kill; handle manually'
        exit 1
    }
    Write-Host '[close-dsh-app] dsh service stopped, port 3080 free OK' -ForegroundColor Green
    Write-Host '[close-dsh-app] NOTE: Web GUI session is down until the new shell starts a fresh service.' -ForegroundColor Yellow
    exit 0
}

Write-Error "[close-dsh-app] port 3080 is held by a NON-dsh process (PID $svcPid, cmd: $cmd); refusing to kill"
exit 1
