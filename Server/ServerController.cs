using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace dsh_app.Server;

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

    /// <summary>当前使用的端口(默认 3080)。</summary>
    public int Port { get; private set; } = DefaultPort;

    /// <summary>服务器是否由本应用拉起(决定退出时是否清理)。</summary>
    public bool IsSelfStarted => _selfStarted;

    /// <summary>运行日志回调，供 UI 状态区与日志文件使用。</summary>
    public event Action<string>? Log;

    private void WriteLog(string message) => Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>
    /// 在 3080~3090 范围内探测已运行的 dsh HTTP 服务。
    /// 返回命中的端口；无则返回 -1（此时 Port 保持默认值，供启动使用）。
    /// </summary>
    public int DetectRunningServer()
    {
        for (int port = DefaultPort; port <= PortScanMax; port++)
        {
            if (IsHttpAlive(port))
            {
                Port = port;
                WriteLog($"检测到已在运行的 dsh 服务：http://127.0.0.1:{port}");
                return port;
            }
        }
        return -1;
    }

    /// <summary>
    /// 确保 dsh 服务可用：已在运行则直连；否则隐藏窗口拉起 `dsh web` 并轮询就绪。
    /// </summary>
    public async Task<bool> EnsureServerAsync()
    {
        if (DetectRunningServer() > 0)
        {
            _selfStarted = false;
            return true;
        }

        _selfStarted = true;
        WriteLog($"未检测到 dsh 服务，正在启动：dsh web (端口 {Port})");
        if (!TryStartServer())
            return false;

        var deadline = DateTime.UtcNow.AddMilliseconds(ReadyTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (IsHttpAlive(Port))
            {
                WriteLog($"dsh 服务就绪：http://127.0.0.1:{Port}");
                return true;
            }

            if (_serverProcess?.HasExited == true)
            {
                WriteLog($"dsh 进程提前退出，退出码 {_serverProcess.ExitCode}");
                return false;
            }

            await Task.Delay(PollIntervalMs);
        }

        WriteLog("等待 dsh 服务就绪超时(30s)，请查看日志");
        return false;
    }

    private bool TryStartServer()
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dsh-app");
            Directory.CreateDirectory(logDir);

            var psi = new ProcessStartInfo
            {
                FileName = "dsh",
                Arguments = "web",
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

    private bool IsHttpAlive(int port)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}");
            using var resp = _http.Send(req);
            // 2xx/3xx/4xx 都说明端口上有 HTTP 服务在监听
            return (int)resp.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 关闭应用时调用：仅当服务器是本应用拉起的才停止。
    /// 先杀进程树，再 taskkill /T /F 兜底，确认无残留。
    /// </summary>
    public void Shutdown()
    {
        if (!_selfStarted || _serverProcess is null)
        {
            _selfStarted = false;
            return;
        }

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
                    // 兜底：强制结束整棵进程树
                    try
                    {
                        var killer = Process.Start(new ProcessStartInfo("taskkill",
                            $"/PID {_serverProcess.Id} /T /F")
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
            }
        }
        catch (Exception ex)
        {
            WriteLog($"停止 dsh 进程失败：{ex.Message}");
        }

        _selfStarted = false;
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
