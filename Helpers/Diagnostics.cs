using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using dsh_app.Server;

namespace dsh_app.Helpers;

/// <summary>诊断行状态（决定状态点颜色：绿/黄/红/蓝信息）。</summary>
public enum DiagStatus
{
    Ok,
    Warn,
    Error,
    Info,
}

/// <summary>诊断行：名称 + 值 + 状态。</summary>
public sealed record DiagRow(string Key, string Value, DiagStatus Status);

/// <summary>
/// 诊断信息采集（排障用，纯环境与运行状态）。
/// 敏感边界（硬性）：绝不采集 DEEPSEEK_API_KEY / EncryptedApiKey / .npmrc 内容 / 任何 token 凭据；
/// 凭据只报授权布尔；代理只报连通性，不输出 URL 原文（防 URL 含 user:pass）。
/// 网络/子进程探测并行执行（node --version / 服务状态 / 代理 / GitHub），总耗时 ≈ max ≈ 5s。
/// </summary>
public static class Diagnostics
{
    private const int NodeTimeoutMs = 3_000;
    private const int ProxyTimeoutMs = 1_500;
    private const int GitHubTimeoutMs = 5_000;

    /// <summary>并行采集全部诊断行（调用方负责在 UI 线程展示；ct 取消后抛 OperationCanceledException）。</summary>
    public static async Task<List<DiagRow>> CollectAsync(
        ServerController server, HarnessUpdater harnessUpdater, AppUpdater appUpdater, CancellationToken ct)
    {
        var rows = new List<DiagRow>
        {
            new("dsh-app 版本", App.AppVersion, DiagStatus.Info),
            new("进程 ID", Environment.ProcessId.ToString(), DiagStatus.Info),
        };

        // 内存项即时
        rows.Add(new DiagRow("dsh 版本", harnessUpdater.LocalVersion ?? "未检测到",
            harnessUpdater.LocalVersion is null ? DiagStatus.Warn : DiagStatus.Ok));

        // 四项独立探测并行执行
        var nodeTask = GetNodeVersionAsync(ct);
        var serviceTask = GetServiceStatusAsync(server, ct);
        var proxyTask = GetProxyStatusAsync(ct);
        var githubTask = GetGitHubStatusAsync(ct);
        await Task.WhenAll(nodeTask, serviceTask, proxyTask, githubTask);
        ct.ThrowIfCancellationRequested();
        rows.Add(nodeTask.Result);
        rows.Add(serviceTask.Result);
        rows.Add(proxyTask.Result);
        rows.Add(githubTask.Result);

        // 设置项（逐字段拷贝，绝不含任何敏感值）
        var s = AppSettings.Current;
        var theme = s.Theme switch
        {
            AppTheme.Dark => "深色",
            AppTheme.Light => "浅色",
            _ => ThemeManager.IsDarkNow ? "深色（跟随系统）" : "浅色（跟随系统）",
        };
        rows.Add(new DiagRow("主题", theme, DiagStatus.Info));
        rows.Add(new DiagRow("最小化到托盘", s.MinimizeToTrayOnClose ? "开" : "关", DiagStatus.Info));
        rows.Add(new DiagRow("自动检查 Harness 更新", s.AutoCheckUpdate ? "开" : "关", DiagStatus.Info));
        rows.Add(new DiagRow("自动检查应用更新", s.AutoCheckAppUpdate ? "开" : "关", DiagStatus.Info));
        rows.Add(new DiagRow("余额显示", s.ShowBalance
            ? (s.BalanceSource == "kimi" ? "开（Kimi 额度）" : $"开（DeepSeek，阈值 ¥{s.BalanceAlertThreshold:0.##}）")
            : "关", DiagStatus.Info));
        rows.Add(new DiagRow("凭据读取授权", s.AllowReadDshCredentials ? "是" : "否", DiagStatus.Info));

        // 日志文件
        try
        {
            var fi = new FileInfo(FileLog.LogFilePath);
            var sizeText = fi.Exists ? (fi.Length / 1048576.0).ToString("0.#") : "0";
            rows.Add(new DiagRow("日志文件", $"{FileLog.LogFilePath}（{sizeText} MB）", DiagStatus.Info));
        }
        catch
        {
            rows.Add(new DiagRow("日志文件", FileLog.LogFilePath, DiagStatus.Info));
        }

        // 更新状态（Harness / 应用）
        rows.Add(UpdateRow("Harness 更新", harnessUpdater.LocalVersion,
            harnessUpdater.LastCheckSucceeded, harnessUpdater.HasUpdate, harnessUpdater.LatestVersion, harnessUpdater.LastError));
        rows.Add(UpdateRow("应用更新", appUpdater.LocalVersion,
            appUpdater.LastCheckSucceeded, appUpdater.HasUpdate, appUpdater.LatestVersion, appUpdater.LastError));

        return rows;
    }

    /// <summary>更新状态行：未检查=本地版本（Info）；已检查有新版=发现新版（Warn）；已检查无新版=已是最新（Ok）；检查失败=失败原因（Error）。</summary>
    private static DiagRow UpdateRow(string key, string? local, bool checkedOk, bool hasUpdate,
        string? latest, string? lastError)
    {
        if (local is null)
            return new DiagRow(key, "未知", DiagStatus.Warn);
        if (!checkedOk && lastError is not null)
            return new DiagRow(key, $"检查失败（{lastError}）", DiagStatus.Error);
        if (hasUpdate)
            return new DiagRow(key, $"发现新版：v{local} → v{latest}", DiagStatus.Warn);
        if (checkedOk)
            return new DiagRow(key, $"已是最新（v{latest ?? local}）", DiagStatus.Ok);
        return new DiagRow(key, $"本地 v{local}（未检查）", DiagStatus.Info);
    }

    /// <summary>node 版本：ResolveNodeExe → node --version（隐藏窗口，3s 超时）。</summary>
    private static async Task<DiagRow> GetNodeVersionAsync(CancellationToken ct)
    {
        var node = ServerController.ResolveNodeExe();
        if (node is null)
            return new DiagRow("node 版本", "未找到 node.exe", DiagStatus.Error);
        try
        {
            var psi = new ProcessStartInfo(node, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return new DiagRow("node 版本", "无法启动 node", DiagStatus.Warn);
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var exitTask = proc.WaitForExitAsync();
            var done = await Task.WhenAny(exitTask, Task.Delay(NodeTimeoutMs, ct));
            if (done != exitTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return new DiagRow("node 版本", "读取超时", DiagStatus.Warn);
            }
            var ver = (await outputTask).Trim();
            return string.IsNullOrEmpty(ver)
                ? new DiagRow("node 版本", "读取失败", DiagStatus.Warn)
                : new DiagRow("node 版本", ver, DiagStatus.Ok);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new DiagRow("node 版本", "读取失败", DiagStatus.Warn);
        }
    }

    /// <summary>dsh 服务状态：端口 + 存活 + 模式（自启动/接管/非 dsh 占用）。</summary>
    private static async Task<DiagRow> GetServiceStatusAsync(ServerController server, CancellationToken ct)
    {
        var alive = await server.CheckAliveAsync();
        ct.ThrowIfCancellationRequested();
        var mode = server.IsSelfStarted ? "自启动" : server.IsManaged ? "接管外部 dsh" : "非 dsh 占用";
        return new DiagRow("dsh 服务",
            $"http://127.0.0.1:{server.Port}（{(alive ? "运行中" : "未响应")}，{mode}）",
            alive ? DiagStatus.Ok : DiagStatus.Error);
    }

    /// <summary>代理 127.0.0.1:7890 TCP 连通性（只报连通，不读配置、不输出 URL）。</summary>
    private static async Task<DiagRow> GetProxyStatusAsync(CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", 7890);
            var done = await Task.WhenAny(connect, Task.Delay(ProxyTimeoutMs, ct));
            if (done != connect)
                return new DiagRow("代理 127.0.0.1:7890", "不可用（连接超时）", DiagStatus.Error);
            await connect;
            return new DiagRow("代理 127.0.0.1:7890", "可用", DiagStatus.Ok);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new DiagRow("代理 127.0.0.1:7890", "不可用", DiagStatus.Error);
        }
    }

    /// <summary>GitHub API 可达性（自更新排障关键；独立 HttpClient，5s 超时）。</summary>
    private static async Task<DiagRow> GetGitHubStatusAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(GitHubTimeoutMs);
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com");
            req.Headers.UserAgent.ParseAdd("dsh-app-diagnostics");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return new DiagRow("GitHub API", $"可达（HTTP {(int)resp.StatusCode}）", DiagStatus.Ok);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DiagRow("GitHub API", "不可达（超时 5s）", DiagStatus.Error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new DiagRow("GitHub API", "不可达", DiagStatus.Error);
        }
    }
}
