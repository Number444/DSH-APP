using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using dsh_app.Helpers;

namespace dsh_app.Server;

/// <summary>
/// Harness（npm 全局包 @deepseek-ai/dsh）版本检查与更新。
/// 检查：`npm view @deepseek-ai/dsh version`（只读网络，不打断运行中的服务，60s 硬超时）；
/// 更新：`npm install -g @deepseek-ai/dsh@latest`（5 分钟硬超时；前置条件由调用方保证已停服）。
/// 事件模式与 ServerController 一致；日志回调只负责透出，UI 与落盘由调用方处理。
/// </summary>
public sealed class HarnessUpdater : IDisposable
{
    private const int CheckTimeoutMs = 60_000;
    private const int UpdateTimeoutMs = 300_000;

    // ---- 代理自适应：每次执行 npm 前实时探测 Clash 端口（TCP 直连探测，最可靠） ----
    // 代理在线 → 显式走代理（行为同用户 npmrc）；代理离线 → --userconfig 空配置绕过死代理纯直连。
    private const string ProxyHost = "127.0.0.1";
    private const int ProxyPort = 7890;
    private const string ProxyUrl = "http://127.0.0.1:7890";
    private const int ProxyProbeTimeoutMs = 800;

    /// <summary>npm view 版本号输出校验正则（不匹配即视为检查失败，防止误报更新）。</summary>
    private static readonly Regex RegistryVersionRegex = new(
        @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled);

    /// <summary>串行化检查/更新，避免并发启动多个 npm 进程。</summary>
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>最近一次启动的 npm 进程（超时兜底与 Dispose 用）。</summary>
    private Process? _npmProcess;
    private bool _disposed;

    /// <summary>日志回调（与 ServerController.Log 同一通道：UI + app.log）。</summary>
    public event Action<string>? Log;

    /// <summary>
    /// 本地已装版本（构造时解析；UpdateAsync 成功后刷新为实装版本，
    /// 供 UI 展示"已更新到 vY"；包缺失/损坏时为 null）。
    /// </summary>
    public string? LocalVersion { get; private set; }

    /// <summary>最近一次检查到的 registry 最新版（未检查为 null；检查失败保留上次值）。</summary>
    public string? LatestVersion { get; private set; }

    /// <summary>最近一次检查是否发现有新版（任一侧版本无法比较时不误报）。</summary>
    public bool HasUpdate { get; private set; }

    /// <summary>最近一次检查是否成功（网络失败/版本解析失败/组件缺失为 false）。</summary>
    public bool LastCheckSucceeded { get; private set; }

    /// <summary>最近一次检查/更新的失败原因文案（null = 无；UI 提示用）。</summary>
    public string? LastError { get; private set; }

    /// <summary>构造函数：解析本地版本（只读文件，无网络）。</summary>
    public HarnessUpdater()
    {
        LocalVersion = ReadLocalVersion();
        WriteLog(LocalVersion is null
            ? "本地 Harness 版本读取失败（node_modules/@deepseek-ai/dsh 缺失或损坏？）"
            : $"本地 Harness 版本：{LocalVersion}");
    }

    /// <summary>
    /// 检查最新版：npm view @deepseek-ai/dsh version → LatestVersion + HasUpdate。
    /// 只读网络，不打断运行中的服务；可重复调用（每次重新检查，幂等）。
    /// 60s 硬超时兜底（npm 自身网络重试可能更久）。
    /// </summary>
    /// <returns>true = 检查成功（是否有新版看 HasUpdate）；false = 检查失败（组件缺失/超时/网络/版本解析失败）。</returns>
    public async Task<bool> CheckAsync()
    {
        await _runLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                LastCheckSucceeded = false;
                HasUpdate = false;
                return false;
            }

            WriteLog("检查 Harness 更新：npm view @deepseek-ai/dsh version…");
            // fetch-retries=0：网络失败快速报错（默认重试会拖 ~70s 超过 60s 硬超时）；
            // 隔离缓存目录：断网时 npm 会回退本地缓存"假成功"（拿陈数据报已是最新），
            // 指向 %TEMP% 下专用空目录强制每次纯网络检查——失败必报错，绝不拿陈缓存交差
            var cache = EnsureIsolatedCacheDir();
            var checkArgs = "view @deepseek-ai/dsh version --fetch-retries=0"
                + (cache is null ? "" : $" --cache \"{cache}\"");
            var result = await RunNpmAsync(checkArgs, CheckTimeoutMs);
            if (!result.Launched)
            {
                // 组件缺失/启动失败，原因已由 RunNpmAsync 记录
                LastError = "未找到 node.exe 或 npm 组件缺失";
                LastCheckSucceeded = false;
                HasUpdate = false;
                return false;
            }
            if (result.TimedOut)
            {
                WriteLog("检查更新超时（60s），判定检查失败（不影响服务运行）");
                LastError = "检查超时（60s），网络可能不可用";
                LastCheckSucceeded = false;
                HasUpdate = false;
                return false;
            }
            if (result.ExitCode != 0)
            {
                WriteLog($"npm view 退出码 {result.ExitCode}，检查失败");
                LastError = $"npm 检查命令退出码 {result.ExitCode}";
                LastCheckSucceeded = false;
                HasUpdate = false;
                return false;
            }

            if (!TryParseRegistryVersion(result.LastOutputLine, out var latest))
            {
                WriteLog($"registry 版本解析失败（原始输出：{result.LastOutputLine ?? "(无输出)"}），本次检查判失败");
                LastError = "registry 返回了无法识别的版本号";
                LastCheckSucceeded = false;
                HasUpdate = false;
                return false;
            }

            LatestVersion = latest;
            LastCheckSucceeded = true;
            LastError = null;
            if (LocalVersion is null)
            {
                // 本地版本未知（包被删/损坏）：检查照常，但无法比较则不提示更新（不误报）
                HasUpdate = false;
                WriteLog($"本地版本未知，暂不提示更新；registry 最新版：{latest}");
                return true;
            }

            HasUpdate = SemVer.Compare(latest, LocalVersion) > 0;
            WriteLog(HasUpdate
                ? $"发现新版：本地 {LocalVersion} → 最新 {latest}"
                : $"已是最新版本：{latest}（本地 {LocalVersion}）");
            return true;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>
    /// 执行更新：npm install -g @deepseek-ai/dsh@latest。
    /// 前置条件：调用方已停服（Windows 下运行中的 node 进程持有包文件句柄，否则覆盖安装会 EPERM）。
    /// 输出经 Log 事件逐行透出；5 分钟硬超时，超时 kill npm 进程树并判失败；
    /// 安装后重读 package.json 验证版本已变化，防止 npm 静默失败（exit 0 但未装成）。
    /// </summary>
    /// <returns>true = 安装成功且版本验证通过；false = 失败（组件缺失/超时/非零退出/验证不一致）。</returns>
    public async Task<bool> UpdateAsync()
    {
        await _runLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                WriteLog("更新被拒绝：对象已释放");
                return false;
            }

            WriteLog($"开始更新 Harness：npm install -g @deepseek-ai/dsh@latest（本地 {LocalVersion ?? "(未知)"}）");
            var result = await RunNpmAsync("install -g @deepseek-ai/dsh@latest", UpdateTimeoutMs);
            if (!result.Launched)
            {
                WriteLog("更新失败：无法启动 npm（node.exe 或 npm-cli.js 缺失），请先确认环境后重试");
                return false;
            }
            if (result.TimedOut)
            {
                WriteLog("更新超时（5 分钟），已强制终止 npm 进程，判定失败");
                return false;
            }
            if (result.ExitCode != 0)
            {
                WriteLog($"npm install 退出码 {result.ExitCode}，更新失败；" +
                         "若报权限错误（EPERM），npm 全局目录可能为系统级，请以管理员身份手动执行：npm install -g @deepseek-ai/dsh@latest");
                return false;
            }

            // 安装后验证：重读本地版本，须已变为新版本（或至少 ≠ 旧版本），否则判失败
            var installed = ReadLocalVersion();
            WriteLog($"npm install 退出码 0，安装后本地版本：{installed ?? "(读取失败)"}");
            if (installed is null || (LocalVersion is not null && installed == LocalVersion))
            {
                WriteLog("安装后版本验证未通过（版本未变化或读取失败），判定更新失败；" +
                         "若服务异常可手动执行：npm install -g @deepseek-ai/dsh@latest");
                return false;
            }

            if (LatestVersion is not null && installed != LatestVersion)
                WriteLog($"提示：实际安装版本 {installed} 与最近一次检查到的最新版 {LatestVersion} 不一致（registry 可能已变化）");
            WriteLog($"Harness 更新成功：{LocalVersion ?? "(无旧版本)"} → {installed}");
            // 更新成功即视为已到最新：刷新本地版本、复位更新提示，避免菜单残留"更新可用"
            LocalVersion = installed;
            HasUpdate = false;
            LatestVersion = installed;
            return true;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>
    /// 统一 npm 命令执行器：直连 node.exe + npm-cli.js（绕开 .cmd 包装器），
    /// 隐藏窗口、输出重定向逐行进 Log（仿 ServerController.TryStartServer 模式）；
    /// 硬超时兜底：超时 kill 进程树并以 TimedOut=true 返回。
    /// </summary>
    /// <param name="arguments">npm-cli.js 之后的参数（如 view @deepseek-ai/dsh version）。</param>
    /// <param name="timeoutMs">硬超时毫秒数。</param>
    /// <returns>执行结果（Launched=false 表示组件缺失/启动失败，原因已记日志）。</returns>
    private async Task<NpmRunResult> RunNpmAsync(string arguments, int timeoutMs)
    {
        var nodeExe = ServerController.ResolveNodeExe();
        if (nodeExe is null)
        {
            WriteLog("未找到 node.exe，无法执行 npm 命令（请确认已安装 Node.js）");
            return new NpmRunResult(false, false, -1, null);
        }
        var nodeDir = Path.GetDirectoryName(nodeExe);
        var npmCli = Path.Combine(nodeDir ?? "", "node_modules", "npm", "bin", "npm-cli.js");
        if (string.IsNullOrEmpty(nodeDir) || !File.Exists(npmCli))
        {
            WriteLog($"npm 组件缺失：{npmCli}");
            return new NpmRunResult(false, false, -1, null);
        }

        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            Arguments = $"\"{npmCli}\" {arguments}{await BuildNetworkArgsAsync()}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            WriteLog($"启动 npm 进程失败：{ex.Message}");
            return new NpmRunResult(false, false, -1, null);
        }
        if (proc is null)
        {
            WriteLog("启动 npm 进程失败（Process.Start 返回 null）");
            return new NpmRunResult(false, false, -1, null);
        }
        _npmProcess = proc;

        // 用 ReadLineAsync 循环收集输出（Log 透出 + 最后一行非空供版本解析），
        // 避免事件回调与最终读取之间的竞态（审查发现项）
        var drain = DrainOutputAsync(proc, WriteLog);

        var exitTask = proc.WaitForExitAsync();
        var completed = await Task.WhenAny(exitTask, Task.Delay(timeoutMs));
        if (completed != exitTask)
        {
            WriteLog($"npm 命令超时（>{timeoutMs / 1000}s），强制终止…");
            KillNpmTree();
            try
            {
                await exitTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // 终止后进程可能尚未完全退出，忽略
            }
            var lastLineTimedOut = await AwaitDrainBounded(drain); // 被终止后管道关闭，读剩余缓冲
            return new NpmRunResult(true, true, -1, lastLineTimedOut);
        }

        var lastLine = await AwaitDrainBounded(drain); // 进程已退出，排空剩余输出
        return new NpmRunResult(true, false, SafeExitCode(proc), lastLine);
    }

    /// <summary>等待输出排空（有界 5s）：进程树被强杀后管道句柄可能被孤儿孙进程持有，
    /// ReadLineAsync 永不返回的风险下兜底放弃（否则 _runLock 被永久占用，更新器死锁）。</summary>
    private static async Task<string?> AwaitDrainBounded(Task<string?> drain)
    {
        var done = await Task.WhenAny(drain, Task.Delay(5000));
        return done == drain ? drain.Result : null;
    }

    /// <summary>
    /// 异步逐行读取 stdout/stderr：每行经 log 委托透出（stderr 带 [err] 前缀），
    /// 返回 stdout 最后一行非空内容（CheckAsync 取版本号用）。
    /// </summary>
    private static async Task<string?> DrainOutputAsync(Process proc, Action<string> log)
    {
        string? last = null;
        var stdout = Task.Run(async () =>
        {
            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync();
                if (line is null) break;
                if (line.Length > 0)
                {
                    log(line);
                    last = line;
                }
            }
        });
        var stderr = Task.Run(async () =>
        {
            while (true)
            {
                var line = await proc.StandardError.ReadLineAsync();
                if (line is null) break;
                if (line.Length > 0) log("[err] " + line);
            }
        });
        await Task.WhenAll(stdout, stderr);
        return last;
    }

    /// <summary>读 <npmDir>/node_modules/@deepseek-ai/dsh/package.json 的 version 字段（失败返回 null）。</summary>
    private static string? ReadLocalVersion()
    {
        try
        {
            var npmDir = ServerController.ResolveNpmDir();
            if (npmDir is null) return null;
            var pkg = Path.Combine(npmDir, "node_modules", "@deepseek-ai", "dsh", "package.json");
            if (!File.Exists(pkg)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>校验 npm view 输出：Trim 后用正则校验，匹配才视为合法版本号（原始字符串异常时由调用方记录）。</summary>
    private static bool TryParseRegistryVersion(string? raw, out string version)
    {
        version = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        if (!RegistryVersionRegex.IsMatch(trimmed)) return false;
        version = trimmed;
        return true;
    }

    /// <summary>
    /// 强制终止正在运行的 npm 进程树（用户确认"中断更新并关闭"时调用；
    /// Dispose 不杀进程——更新中关窗须让安装跑完，见设计文档 §8）。
    /// </summary>
    public void AbortRunningNpm() => KillNpmTree();

    /// <summary>终止当前 npm 进程树（先 Kill 整树，再 taskkill /T /F 兜底）。</summary>
    private void KillNpmTree()
    {
        var proc = _npmProcess;
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程可能刚好退出，忽略后走兜底
                }
                if (!proc.WaitForExit(3000))
                {
                    KillPidTree(proc.Id);
                }
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>taskkill /PID /T /F 强制结束整棵进程树（与 ServerController 同思路）。</summary>
    private static void KillPidTree(int pid)
    {
        try
        {
            var killer = Process.Start(new ProcessStartInfo("taskkill",
                $"/PID {pid} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            killer?.WaitForExit(3000);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>安全读取进程退出码（进程句柄可能已失效）。</summary>
    private static int SafeExitCode(Process proc)
    {
        try { return proc.ExitCode; }
        catch { return -1; }
    }

    /// <summary>
    /// 代理自适应：探测 Clash 端口，在线走代理、离线直连（--userconfig 空配置绕过用户 npmrc 里的死代理）。
    /// 探测结果写日志透出。
    /// </summary>
    private async Task<string> BuildNetworkArgsAsync()
    {
        if (await ProbeProxyAsync())
        {
            WriteLog($"代理探测：{ProxyHost}:{ProxyPort} 可达，走代理");
            return $" --proxy={ProxyUrl} --https-proxy={ProxyUrl}";
        }
        var rc = EnsureDirectNpmrc();
        WriteLog(rc is null
            ? $"代理探测：{ProxyHost}:{ProxyPort} 不可达，直连（空 npmrc 写入失败，仍受用户配置影响）"
            : $"代理探测：{ProxyHost}:{ProxyPort} 不可达，直连 registry");
        return rc is null ? "" : $" --userconfig \"{rc}\"";
    }

    /// <summary>TCP 探测代理端口（800ms 超时；不问系统设置、不读 npm 配置，直接问端口最可靠）。</summary>
    private static async Task<bool> ProbeProxyAsync()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(ProxyProbeTimeoutMs);
            await tcp.ConnectAsync(ProxyHost, ProxyPort, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>直连模式用的空 npmrc（绕过用户配置中的代理设置；%TEMP% 下只写一次，失败返回 null）。</summary>
    private static string? EnsureDirectNpmrc()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "dsh-app-direct.npmrc");
            if (!File.Exists(path))
                File.WriteAllText(path, "# dsh-app 直连模式空配置：绕过用户 npmrc 中的代理设置\n");
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 检查更新专用隔离缓存目录（%TEMP% 下；**每次清空重建**——npm 断网会回退缓存"假成功"，
    /// 只换目录不清空等于没隔离；清空后断网必失败，绝不拿陈缓存交差）。创建失败返回 null 退回默认缓存。
    /// </summary>
    private static string? EnsureIsolatedCacheDir()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "dsh-app-npmcache");
            if (Directory.Exists(path)) Directory.Delete(path, true);
            Directory.CreateDirectory(path);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>带时间戳写日志（与 ServerController.WriteLog 同格式）。</summary>
    private void WriteLog(string message) => Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>
    /// 释放资源：仅释放进程句柄，**不杀仍在运行的 npm 进程**——更新期间关窗必须让
    /// npm install 跑完（半安装比跑完更危险，见设计文档 §8）；进程随应用退出成为孤儿
    /// 自然结束，下次启动即用新版本。超时兜底在 RunNpmAsync 内部已负责终止。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _npmProcess?.Dispose();
        _npmProcess = null;
    }

    /// <summary>npm 命令执行结果。</summary>
    /// <param name="Launched">进程是否成功启动（false = node/npm-cli.js 缺失或启动异常）。</param>
    /// <param name="TimedOut">是否硬超时被杀。</param>
    /// <param name="ExitCode">进程退出码（未启动/超时为 -1）。</param>
    /// <param name="LastOutputLine">stdout 最后一行非空输出（版本号候选）。</param>
    private sealed record NpmRunResult(bool Launched, bool TimedOut, int ExitCode, string? LastOutputLine);
}
