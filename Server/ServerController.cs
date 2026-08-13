using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace dsh_app.Server;

/// <summary>启动步骤（覆盖层展示用）。</summary>
public enum ServerStep
{
    Detect,    // 探测已有服务
    Resolve,   // 定位运行环境（node + dsh 入口）
    Launch,    // 启动 dsh 进程
    WaitReady, // 等待服务就绪
    Load,      // 加载界面（由窗口维护）
}

/// <summary>步骤状态。</summary>
public enum StepStatus
{
    Pending, Running, Done, Failed,
}

/// <summary>
/// dsh web 服务器生命周期管理：
/// 探测端口(3080~3090) → 若无服务则隐藏窗口拉起 `dsh web` → 轮询就绪 → 退出时仅清理自己拉起的进程。
/// 纯壳逻辑，不引用任何本机绝对路径，双机通用。
/// </summary>
public sealed class ServerController : IDisposable
{
    private const int DefaultPort = 3080;
    private const int PortScanMax = 3090;
    private const int ReadyTimeoutMs = 30_000;
    private const int PollIntervalMs = 500;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(1) };
    private Process? _serverProcess;
    private bool _selfStarted;
    private bool _disposed;
    /// <summary>服务已就绪（EnsureServerAsync 成功）标志，用于区分启动中退出与运行中崩溃。</summary>
    private volatile bool _ready;

    /// <summary>接管的外部 dsh 进程 PID（0 = 无接管）。</summary>
    private int _adoptedPid;
    /// <summary>接管进程是否已通过身份验证（确认为 dsh，而非其他占用端口的程序）。</summary>
    private bool _adoptedIsDsh;

    /// <summary>当前使用的端口(默认 3080)。</summary>
    public int Port { get; private set; } = DefaultPort;

    /// <summary>服务器是否由本应用拉起(决定退出时是否清理)。</summary>
    public bool IsSelfStarted => _selfStarted;

    /// <summary>运行日志回调，供 UI 状态区与日志文件使用。</summary>
    public event Action<string>? Log;

    /// <summary>启动步骤状态变化（步骤、状态、详情），供覆盖层展示。</summary>
    public event Action<ServerStep, StepStatus, string>? StepChanged;

    /// <summary>自家拉起的服务在就绪后中途退出（运行中崩溃）时触发；启动阶段退出不触发。</summary>
    public event Action? ServerDied;

    /// <summary>启动失败时的安装指引（环境缺失时设置，UI 直接展示）。</summary>
    public string? StartupErrorHint { get; private set; }

    private void WriteLog(string message) => Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");

    private void ReportStep(ServerStep step, StepStatus status, string detail)
        => StepChanged?.Invoke(step, status, detail);

    /// <summary>
    /// 确保 dsh 服务可用：已在运行则直连；否则隐藏窗口拉起 `dsh web` 并轮询就绪。
    /// 全程异步，不阻塞调用线程（UI 线程）。
    /// </summary>
    public async Task<bool> EnsureServerAsync()
    {
        StartupErrorHint = null;
        _ready = false;

        // ① 探测已有服务（async，避免 UI 冻结）
        ReportStep(ServerStep.Detect, StepStatus.Running, $"扫描 {DefaultPort}~{PortScanMax}…");
        var existing = await DetectRunningServerAsync();
        if (existing > 0)
        {
            _selfStarted = false;
            _ready = true;
            ReportStep(ServerStep.Detect, StepStatus.Done, $"http://127.0.0.1:{existing} 已在运行");
            return true;
        }
        ReportStep(ServerStep.Detect, StepStatus.Done, "无已有服务，走本地启动");

        // ② 定位运行环境（node.exe 与 dsh 入口，不依赖进程 PATH）
        _selfStarted = true;
        ReportStep(ServerStep.Resolve, StepStatus.Running, "定位 node 与 dsh 入口…");
        var nodeExe = ResolveNodeExe();
        if (nodeExe is null)
        {
            StartupErrorHint = "未检测到 Node.js。\n\n安装步骤：\n1. 打开命令行执行：winget install OpenJS.NodeJS（或到 nodejs.org 下载安装包）\n2. 重新启动本程序";
            ReportStep(ServerStep.Resolve, StepStatus.Failed, "未找到 node.exe");
            WriteLog("未找到 node.exe，请确认已安装 Node.js");
            return false;
        }
        var dshBin = ResolveDshBinJs();
        if (dshBin is null)
        {
            StartupErrorHint = "未检测到 DeepSeek Harness（dsh）。\n\n安装步骤：\n1. 打开命令行执行：npm install -g @deepseek-ai/dsh\n2. 重新启动本程序";
            ReportStep(ServerStep.Resolve, StepStatus.Failed, "未找到 dsh 包入口");
            WriteLog("未找到 dsh 包入口（node_modules/@deepseek-ai/dsh/lib/bin.js），请确认已执行：npm i -g @deepseek-ai/dsh");
            return false;
        }
        ReportStep(ServerStep.Resolve, StepStatus.Done, $"{nodeExe}");
        WriteLog($"启动：{nodeExe} {dshBin} web");

        // ③ 拉起 dsh 进程
        ReportStep(ServerStep.Launch, StepStatus.Running, "拉起 dsh web…");
        if (!TryStartServer(nodeExe, dshBin))
        {
            ReportStep(ServerStep.Launch, StepStatus.Failed, "进程启动失败");
            return false;
        }
        ReportStep(ServerStep.Launch, StepStatus.Done, $"PID {_serverProcess!.Id}");

        // ④ 轮询就绪
        ReportStep(ServerStep.WaitReady, StepStatus.Running, $"轮询 http://127.0.0.1:{Port}");
        var deadline = DateTime.UtcNow.AddMilliseconds(ReadyTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHttpAliveAsync(Port))
            {
                _ready = true;
                ReportStep(ServerStep.WaitReady, StepStatus.Done, $"http://127.0.0.1:{Port} 就绪");
                WriteLog($"dsh 服务就绪：http://127.0.0.1:{Port}");
                return true;
            }

            if (_serverProcess?.HasExited == true)
            {
                ReportStep(ServerStep.WaitReady, StepStatus.Failed, $"进程提前退出，退出码 {_serverProcess.ExitCode}");
                WriteLog($"dsh 进程提前退出，退出码 {_serverProcess.ExitCode}");
                return false;
            }

            await Task.Delay(PollIntervalMs);
        }

        ReportStep(ServerStep.WaitReady, StepStatus.Failed, "30s 超时");
        WriteLog("等待 dsh 服务就绪超时(30s)，请查看日志");
        return false;
    }

    /// <summary>
    /// 在 3080~3090 范围内探测已运行的 dsh HTTP 服务（并发，最坏 ~1s 而非串行 ~11s）。
    /// 命中时记录监听进程 PID 并做身份验证（区分 dsh 与占用端口的其他程序）。
    /// 返回存活的最小端口；无则返回 -1。
    /// </summary>
    private async Task<int> DetectRunningServerAsync()
    {
        var ports = Enumerable.Range(DefaultPort, PortScanMax - DefaultPort + 1).ToArray();
        var alive = await Task.WhenAll(ports.Select(p => IsHttpAliveAsync(p)));
        var hit = -1;
        for (var i = 0; i < ports.Length; i++)
        {
            if (alive[i]) { hit = ports[i]; break; }
        }
        if (hit < 0) return -1;

        Port = hit;
        _adoptedPid = await GetListeningPidAsync(hit);
        _adoptedIsDsh = await IsDshProcessAsync(_adoptedPid);
        WriteLog(_adoptedIsDsh
            ? $"检测到已在运行的 dsh 服务：http://127.0.0.1:{hit} (PID {_adoptedPid}，确认为 dsh，关窗时一并停止)"
            : $"检测到端口 {hit} 已被其他程序占用 (PID {_adoptedPid})，非 dsh，关窗时不清理");
        return hit;
    }

    /// <summary>轻量存活检查（心跳用）：当前端口上的 HTTP 服务是否仍在响应。</summary>
    public Task<bool> CheckAliveAsync() => IsHttpAliveAsync(Port);

    /// <summary>通过 netstat 解析监听指定端口的进程 PID（0 = 未找到）。</summary>
    private static async Task<int> GetListeningPidAsync(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains($":{port}") || !line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && int.TryParse(parts[^1], out var pid))
                    return pid;
            }
        }
        catch
        {
            // 解析失败视为未找到
        }
        return 0;
    }

    /// <summary>通过进程命令行验证 PID 是否为 dsh 服务（命令行含 dsh / bin.js 特征）。</summary>
    private static async Task<bool> IsDshProcessAsync(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            var psi = new ProcessStartInfo("powershell",
                $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi)!;
            var cmd = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return cmd.Contains("dsh", StringComparison.OrdinalIgnoreCase)
                || cmd.Contains("bin.js", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool TryStartServer(string nodeExe, string dshBin)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dsh-app");
            Directory.CreateDirectory(logDir);

            var psi = new ProcessStartInfo
            {
                FileName = nodeExe,
                Arguments = $"\"{dshBin}\" web",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            _serverProcess = Process.Start(psi);
            if (_serverProcess is null)
            {
                WriteLog("无法启动 dsh 进程");
                return false;
            }

            // 就绪后中途退出 → 通知 UI（启动阶段退出由就绪轮询里的 HasExited 分支处理，不重复报）
            _serverProcess.EnableRaisingEvents = true;
            _serverProcess.Exited += (_, _) =>
            {
                if (_ready)
                {
                    _ready = false;
                    WriteLog($"dsh 进程运行中退出，退出码 {SafeExitCode()}");
                    ServerDied?.Invoke();
                }
            };

            _serverProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) WriteLog(e.Data);
            };
            _serverProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) WriteLog("[err] " + e.Data);
            };
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();
            WriteLog($"dsh 进程已启动 (PID {_serverProcess.Id})");
            return true;
        }
        catch (Exception ex)
        {
            WriteLog($"启动 dsh 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 定位 node.exe：依次扫描进程 PATH、注册表 PATH(Machine+User)、常见安装位置。
    /// 注册表 PATH 扫描可绕过父进程环境过期的问题（如 explorer 未刷新环境变量）。
    /// </summary>
    private static string? ResolveNodeExe()
    {
        foreach (var dir in GetPathDirs(Environment.GetEnvironmentVariable("PATH")))
        {
            var candidate = Path.Combine(dir, "node.exe");
            if (File.Exists(candidate)) return candidate;
        }

        // 注册表真值（Machine + User），不继承自父进程
        var machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        foreach (var dir in GetPathDirs(machinePath + ";" + userPath))
        {
            var candidate = Path.Combine(dir, "node.exe");
            if (File.Exists(candidate)) return candidate;
        }

        // 常见安装位置兜底
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[]
        {
            Path.Combine(programFiles, "nodejs", "node.exe"),
            Path.Combine(localAppData, "Programs", "nodejs", "node.exe"),
        }.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 定位 dsh 真实入口 bin.js（npm 全局包 @deepseek-ai/dsh）。
    /// 通过 dsh.cmd 的宿主目录（npm 全局 bin 目录）反推，绕开 .cmd 包装器。
    /// </summary>
    private static string? ResolveDshBinJs()
    {
        string? npmDir = null;
        foreach (var dir in GetPathDirs(Environment.GetEnvironmentVariable("PATH")))
        {
            if (File.Exists(Path.Combine(dir, "dsh.cmd"))) { npmDir = dir; break; }
        }
        if (npmDir is null)
        {
            // 常见 npm 全局目录兜底
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            npmDir = new[]
            {
                Path.Combine(appData, "npm"),
                Path.Combine(programFiles, "nodejs"),
            }.FirstOrDefault(dir => File.Exists(Path.Combine(dir, "dsh.cmd")));
        }
        if (npmDir is null) return null;

        var bin = Path.Combine(npmDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        return File.Exists(bin) ? bin : null;
    }

    /// <summary>把 PATH 字符串按 ';' 拆成清理后的目录列表（跳过空项与非法段）。</summary>
    private static IEnumerable<string> GetPathDirs(string? path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim('"');
            if (trimmed.Length == 0) continue;
            if (IsExistingDir(trimmed)) yield return trimmed;
        }
    }

    private static bool IsExistingDir(string dir)
    {
        try { return Directory.Exists(dir); }
        catch { return false; }
    }

    private async Task<bool> IsHttpAliveAsync(int port)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}");
            using var resp = await _http.SendAsync(req);
            // 2xx/3xx/4xx 都说明端口上有 HTTP 服务在监听
            return (int)resp.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 关闭应用时调用：停止本应用拉起的 dsh 进程；
    /// 若端口服务是外部启动的，仅当通过身份验证（确认为 dsh）才一并停止，其他程序绝不误杀。
    /// </summary>
    public void Shutdown()
    {
        _ready = false; // 主动清理不触发 ServerDied

        // 自家拉起的：先杀进程树，再 taskkill /T /F 兜底
        if (_selfStarted && _serverProcess is not null)
        {
            try
            {
                if (!_serverProcess.HasExited)
                {
                    WriteLog("关闭应用，停止 dsh 服务…");
                    try
                    {
                        _serverProcess.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // 进程可能刚好退出，忽略后走兜底
                    }

                    if (!_serverProcess.WaitForExit(3000))
                    {
                        KillPidTree(_serverProcess.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"停止 dsh 进程失败：{ex.Message}");
            }
        }

        // 接管的外部 dsh（已验证）：一并停止，避免"关不掉"的残留
        if (_adoptedPid > 0 && _adoptedIsDsh)
        {
            WriteLog($"关闭应用，停止接管的外部 dsh 服务 (PID {_adoptedPid})…");
            KillPidTree(_adoptedPid);
        }

        _selfStarted = false;
        _adoptedPid = 0;
        _adoptedIsDsh = false;
    }

    /// <summary>安全读取进程退出码（进程句柄可能已失效）。</summary>
    private int SafeExitCode()
    {
        try { return _serverProcess?.ExitCode ?? -1; }
        catch { return -1; }
    }

    /// <summary>taskkill /T /F 强制结束整棵进程树。</summary>
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        _serverProcess?.Dispose();
        _serverProcess = null;
    }
}
