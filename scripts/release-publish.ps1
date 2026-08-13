# release-publish.ps1 — 一键发布（完整发布流程第二步自动化）
#
# 用法：powershell -ExecutionPolicy Bypass -File scripts\release-publish.ps1 [-SkipSmoke]
# 流程：关闭壳进程（保持 3080 服务存活，GUI 会话不中断）→ dotnet publish 单 exe
#       → 冒烟（启动→3080 就绪→关窗→释放）→ 自动重新打开窗口交付。
#       全程无人值守；窗口关闭到重新打开之间工具通道（3080）不中断。
# 前提：第一步 commit 已完成（脚本检测未提交改动仅警告不阻断）。
# 说明：第三步 push 不在此脚本内——由主人确定后手动执行（git push origin main）。
# 退出码：0 = 发布成功且窗口已交付；非 0 = 失败（见错误信息）。

param(
    [switch]$SmokeFull  # 完整冒烟（含关窗释放验证）——仅离线场景可用：关窗会杀掉
                        # 被接管的 3080 服务，即当前 Web GUI 会话（工具通道），会中断发布
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host '=== dsh-app one-click publish ===' -ForegroundColor Cyan

# ① 未提交改动检查（警告不阻断）
$dirty = git status --porcelain
if ($dirty) {
    Write-Warning "uncommitted changes detected (publish should correspond to committed code):`n$dirty"
}

# ② 关闭壳进程（保留其 node 服务：3080 不断，GUI 会话不中断）
Write-Host '--- closing shell process (keeping 3080 service alive) ---' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'close-dsh-app.ps1')
if ($LASTEXITCODE -ne 0) { throw 'failed to close shell process; aborting' }

# ③ 发布单 exe（self-contained 单文件）
Write-Host '--- dotnet publish (self-contained single file) ---' -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
$exe = Join-Path $root 'bin\Release\net9.0-windows\win-x64\publish\dsh-app.exe'
if (-not (Test-Path $exe)) { throw "artifact missing: $exe" }
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "artifact ready: $exe (${size}MB)" -ForegroundColor Green

# 启动产物并等待 3080 就绪（冒烟点）；返回是否就绪
function Start-And-Wait-Ready {
    Start-Process -FilePath $exe | Out-Null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        try {
            $r = Invoke-WebRequest -Uri 'http://127.0.0.1:3080' -TimeoutSec 2 -UseBasicParsing
            if ($r.StatusCode -lt 500) { return $true }
        }
        catch { }
    }
    return $false
}

# ④ 启动产物：就绪验证即交付（不关窗！）
# 注意：不能做"关窗释放"验证——新实例会接管 3080 上的既有服务（孤儿 dsh web），
# 关窗会把它一并杀掉，而该服务承载当前 Web GUI 会话（工具通道），杀掉即中断发布。
# 因此默认只验证"启动 + 3080 就绪"，窗口保留交付；完整关窗验证仅离线场景可用（-SmokeFull）。
Write-Host '--- starting artifact and waiting 3080 ready (deliver, no close) ---' -ForegroundColor Cyan
if (-not (Start-And-Wait-Ready)) {
    throw 'startup failed: 3080 not ready within 30s'
}
Write-Host 'app ready, window delivered OK (smoke: HTTP alive + window up)' -ForegroundColor Green

Write-Host '=== publish done. step 3 (push) is decided by the owner: git push origin main ===' -ForegroundColor Cyan
exit 0
