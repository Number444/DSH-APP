# create-release.ps1 — 创建 GitHub Release 并上传产物（发布流程第四步，push 之后运行）
#
# 用途：dsh-app 壳自身自动更新依赖 GitHub Releases（检查 releases/latest + 下载 asset）。
#       本脚本在发布（release-publish.ps1）与 push 之后运行：
#       1. 校验 HEAD == origin/main（禁止在未推送 HEAD 上打 tag）
#       2. 生成 dsh-app.exe.sha256（Get-FileHash 裸 64 位 hex，与壳侧解析契约一致）
#       3. 检测 gh CLI（gh --version + gh auth status），不可用则输出手动命令并退出
#       4. tag 已存在则跳过（幂等）
#       5. 确认框（创建公开 Release 属公开动作）→ gh release create
#
# 用法：powershell -ExecutionPolicy Bypass -File scripts\create-release.ps1
# 退出码：0 = 已创建/已存在/用户取消；2 = HEAD 与 origin/main 不一致（先 push）；
#         3 = gh CLI 缺失或未登录；1 = 其他失败（产物缺失/创建失败）。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 版本号（csproj <Version>）
$csproj = Get-Content (Join-Path $root 'dsh-app.csproj') -Raw
$m = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
if (-not $m.Success) { Write-Host 'ERROR: cannot read <Version> from dsh-app.csproj'; exit 1 }
$version = $m.Groups[1].Value
$tag = "v$version"
Write-Host "version: $version (tag: $tag)"

# ① HEAD == origin/main（push 前打 tag 会让 Release 指向未推送提交）
$head = git rev-parse HEAD
$origin = git rev-parse origin/main
if ($head -ne $origin) {
    Write-Host "[warn] HEAD ($head) != origin/main ($origin)"
    Write-Host "       push 后再运行本脚本（git push origin main）"
    exit 2
}

# ② 产物与 sha256
$exe = Join-Path $root 'bin\Release\net9.0-windows\win-x64\publish\dsh-app.exe'
if (-not (Test-Path $exe)) { Write-Host "ERROR: artifact missing: $exe"; exit 1 }
$shaFile = "$exe.sha256"
$sha = (Get-FileHash -Algorithm SHA256 -Path $exe).Hash.ToLower()
# 契约（三处钉死：发布脚本 / 壳侧 AppUpdater.Sha256Regex / SELF-UPDATE.md）：只写裸 64 位 hex
Set-Content -Path $shaFile -Value $sha -NoNewline -Encoding Ascii
Write-Host "sha256: $sha -> $(Split-Path $shaFile -Leaf)"

# ③ gh CLI 检测
$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    Write-Host 'ERROR: gh CLI not found. Install: winget install GitHub.cli'
    Write-Host "Manual fallback: gh release create $tag $exe $shaFile --repo Number444/DSH-APP --title 'dsh-app $tag' --notes '<changelog>'"
    exit 3
}
$null = gh auth status 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host 'ERROR: gh not authenticated. Run: gh auth login'
    Write-Host "Manual fallback: gh release create $tag $exe $shaFile --repo Number444/DSH-APP --title 'dsh-app $tag' --notes '<changelog>'"
    exit 3
}

# ④ tag 已存在 → 幂等跳过
$null = gh release view $tag --repo Number444/DSH-APP 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "release $tag already exists, skip"
    exit 0
}

# ⑤ 确认框（创建公开 Release 是公开动作）
Add-Type -AssemblyName System.Windows.Forms
$yes = [System.Windows.Forms.MessageBox]::Show(
    "创建公开 GitHub Release $tag 并上传 dsh-app.exe（含 .sha256）？",
    'dsh-app Release', 'YesNo', 'Question')
if ($yes -ne [System.Windows.Forms.DialogResult]::Yes) {
    Write-Host 'cancelled by user'
    exit 0
}

# ⑥ notes：docs/CHANGELOG.md 最新节（第一个 "## " 节起），无则用版本号
$notes = "dsh-app $version"
$changelog = Join-Path $root 'docs\CHANGELOG.md'
if (Test-Path $changelog) {
    $lines = Get-Content $changelog
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^##\s') { $start = $i; break }
    }
    if ($start -ge 0) {
        $end = $lines.Count
        for ($i = $start + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^##\s') { $end = $i; break }
        }
        $notes = ($lines[$start..($end - 1)] -join "`n").Trim()
    }
}

gh release create $tag $exe $shaFile --repo Number444/DSH-APP --title "dsh-app $tag" --notes $notes
if ($LASTEXITCODE -ne 0) {
    Write-Host 'ERROR: gh release create failed'
    exit 1
}
Write-Host "release created: https://github.com/Number444/DSH-APP/releases/tag/$tag"
exit 0
