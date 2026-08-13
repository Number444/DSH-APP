# release-publish.ps1 — 一键发布（完整发布流程第二步自动化，带 GUI 进度窗口）
#
# 用法：powershell -ExecutionPolicy Bypass -File scripts\release-publish.ps1
# 流程：关闭壳进程（保持 3080 服务存活，GUI 会话不中断）→ dotnet publish 单 exe
#       → 启动交付（就绪确认）。全程无人值守；桌面弹出进度窗口，
#       实时显示"执行到哪一步 / 每步结果 / 进度条"，结束弹出成功/失败结果框。
# 前提：第一步 commit 已完成（脚本检测未提交改动仅警告不阻断）。
# 说明：第三步 push 不在此脚本内——由主人确定后手动执行（git push origin main）。
# 退出码：0 = 发布成功且窗口已交付；非 0 = 失败（见结果框与日志）。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---------------- GUI 进度窗口（WinForms） ----------------
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'dsh-app 一键发布'
$form.Size = New-Object System.Drawing.Size(600, 380)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.MinimizeBox = $false

$logBox = New-Object System.Windows.Forms.TextBox
$logBox.Multiline = $true
$logBox.ReadOnly = $true
$logBox.ScrollBars = 'Vertical'
$logBox.Dock = 'Fill'
$logBox.Font = New-Object System.Drawing.Font('Consolas', 9)
$logBox.BackColor = [System.Drawing.Color]::FromArgb(13, 17, 23)
$logBox.ForeColor = [System.Drawing.Color]::FromArgb(230, 237, 243)

$status = New-Object System.Windows.Forms.Label
$status.Dock = 'Bottom'
$status.Height = 26
$status.Text = '准备发布…'
$status.TextAlign = 'MiddleLeft'
$status.BackColor = [System.Drawing.Color]::FromArgb(22, 27, 34)
$status.ForeColor = [System.Drawing.Color]::FromArgb(157, 167, 179)

$progress = New-Object System.Windows.Forms.ProgressBar
$progress.Dock = 'Bottom'
$progress.Height = 18
$progress.Minimum = 0
$progress.Maximum = 100
$progress.Value = 0

$form.Controls.Add($logBox)
$form.Controls.Add($status)
$form.Controls.Add($progress)
$form.Show() | Out-Null

$script:failed = $false

function Add-Log([string]$msg, [string]$color = '') {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $msg
    $logBox.AppendText($line + [Environment]::NewLine)
    $logBox.SelectionStart = $logBox.TextLength
    $logBox.ScrollToCaret()
    $status.Text = $msg
    [System.Windows.Forms.Application]::DoEvents()
}
function Set-Progress([int]$pct) {
    $progress.Value = [Math]::Min(100, $pct)
    [System.Windows.Forms.Application]::DoEvents()
}

try {
    Add-Log '=== dsh-app one-click publish ==='
    Set-Progress 2

    # ① 未提交改动检查（警告不阻断）
    $dirty = git status --porcelain
    if ($dirty) {
        Add-Log "[warn] uncommitted changes (publish should correspond to committed code):`n$dirty"
    }

    # ② 关闭壳进程（保留其 node 服务：3080 不断，GUI 会话不中断）
    Add-Log '--- step 1/3: closing shell process (keeping 3080 alive) ---'
    Set-Progress 8
    & (Join-Path $PSScriptRoot 'close-dsh-app.ps1') 2>&1 | ForEach-Object { Add-Log $_ }
    if ($LASTEXITCODE -ne 0) { throw 'failed to close shell process; aborting' }
    Add-Log 'shell closed OK'
    Set-Progress 20

    # ③ 发布单 exe（self-contained 单文件，输出实时进进度窗）
    Add-Log '--- step 2/3: dotnet publish (self-contained single file) ---'
    Set-Progress 25
    $pubOut = dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true 2>&1
    $pubOut | ForEach-Object { Add-Log $_ }
    if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
    $exe = Join-Path $root 'bin\Release\net9.0-windows\win-x64\publish\dsh-app.exe'
    if (-not (Test-Path $exe)) { throw "artifact missing: $exe" }
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Add-Log "artifact ready: $exe (${size}MB)"
    Set-Progress 60

    # ④ 启动产物：就绪即交付（不关窗！——接管模式下关窗会杀掉承载 GUI 会话的 3080 服务）
    Add-Log '--- step 3/3: starting artifact and waiting 3080 ready ---'
    Set-Progress 70
    Start-Process -FilePath $exe | Out-Null
    $deadline = (Get-Date).AddSeconds(30)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        [System.Windows.Forms.Application]::DoEvents()
        try {
            $r = Invoke-WebRequest -Uri 'http://127.0.0.1:3080' -TimeoutSec 2 -UseBasicParsing
            if ($r.StatusCode -lt 500) { $ready = $true; break }
        }
        catch { }
    }
    if (-not $ready) { throw 'startup failed: 3080 not ready within 30s' }
    Set-Progress 95
    Add-Log 'app ready, window delivered OK'
    Set-Progress 100

    Add-Log '=== publish done. step 3 (push) is decided by the owner: git push origin main ==='
    Start-Sleep -Milliseconds 400
    $form.Close()
    [System.Windows.Forms.MessageBox]::Show(
        "发布成功：$exe（${size}MB）`n`n当前窗口为最新版本。`n第三步 push 由主人确定。",
        'dsh-app 发布完成', 'OK', 'Information') | Out-Null
    exit 0
}
catch {
    $script:failed = $true
    Add-Log "[error] $($_.Exception.Message)"
    Add-Log '=== publish FAILED ==='
    Start-Sleep -Milliseconds 400
    $form.Close()
    [System.Windows.Forms.MessageBox]::Show(
        "发布失败：$($_.Exception.Message)`n`n详细日志见进度窗口/发布控制台。",
        'dsh-app 发布失败', 'OK', 'Error') | Out-Null
    exit 1
}
