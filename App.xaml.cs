using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using dsh_app.Helpers;
using dsh_app.Server;

namespace dsh_app;

/// <summary>
/// 应用入口：单实例互斥、全局异常兜底、服务器引用托管。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 壳自身版本（读 Assembly InformationalVersion，SDK 自动生成自 csproj &lt;Version&gt;；
    /// 去 + 后缀兜底——本地构建可能带 +sha）。供关于窗口、诊断面板、应用自更新共用。
    /// </summary>
    public static string AppVersion { get; } = ReadAppVersion();

    private static string ReadAppVersion()
    {
        try
        {
            var v = typeof(App).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(v))
            {
                var plus = v.IndexOf('+');
                return plus >= 0 ? v[..plus] : v;
            }
        }
        catch
        {
            // 读取失败回退常量
        }
        return "1.3.0";
    }
    /// <summary>
    /// 互斥体名：必须与历史版本（v1.1.0 及更早，Global\）一致——旧版只认互斥体不认进程扫描，
    /// 用 Local\ 会让旧版在修复版之后仍能启动（新旧并存回归，实测复现）。
    /// 跨会话隔离由进程扫描的 SessionId 过滤承担（互斥体对象存在与否不影响任何会话启动）。
    /// </summary>
    private const string MutexName = @"Global\DSH-APP-SingleInstance-4F2A9C71";

    /// <summary>第二实例激活第一实例窗口的自定义消息号（RegisterWindowMessage 保证唯一）。</summary>
    internal static readonly int WmDshActivate = RegisterWindowMessage("DSH-APP-Activate-4F2A9C71");

    /// <summary>未处理异常兜底请求强制退出：OnClosing 必须放行（否则"应用将退出"实际被托盘拦截）。</summary>
    internal static bool ForceExit { get; set; }

    private Mutex? _mutex;
    private string _logFilePath = "";

    /// <summary>主窗口持有的服务器控制器，供异常兜底时清理。</summary>
    public static ServerController? ActiveServer { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dsh-app");
        Directory.CreateDirectory(logDir);
        _logFilePath = Path.Combine(logDir, "app.log");

        // 清理上次自更新残留的下载文件（*.part / *.new / *.sha256）。
        // 不碰 backup/ 与 rolled-back.flag/updater.log——备份与回滚职责全归更新器脚本，
        // 防"新实例清理删掉备份致回滚失效"竞态（架构评审项）。
        try
        {
            var updateDir = Path.Combine(logDir, "update");
            if (Directory.Exists(updateDir))
            {
                foreach (var pattern in new[] { "*.part", "*.new", "*.sha256" })
                {
                    foreach (var f in Directory.EnumerateFiles(updateDir, pattern))
                    {
                        try { File.Delete(f); } catch { /* 忽略单个失败 */ }
                    }
                }
            }
        }
        catch
        {
            // 清理失败不影响启动
        }

        // 单实例判定（v1.2.1 加固）：
        // ① 进程扫描是权威判断：任何存活的其他 dsh-app 进程（含互斥体已丢失的僵尸实例、
        //    不同版本的 exe）一律踢出——曾出现"互斥体释放但进程存活"导致新旧版本并存；
        // ② 互斥体（存在性语义）仅作辅助快路径与扫描失败的兜底。
        try
        {
            _mutex = new Mutex(false, MutexName, out _);
        }
        catch
        {
            // 互斥体创建失败（极端权限环境）不阻塞启动：进程扫描仍是权威防线
        }

        if (OtherInstanceRunning())
        {
            // 已有实例在运行：通知其激活窗口后退出本实例
            ActivateExistingWindow();
            Shutdown();
            return;
        }

        // 主题初始化（读设置 → 应用配色 → 订阅系统主题变化），须先于主窗口创建
        ThemeManager.Initialize();

        DispatcherUnhandledException += (_, args) =>
        {
            WriteLog("[unhandled] " + args.Exception);
            ActiveServer?.Shutdown();
            MessageBox.Show($"发生未处理的错误，应用将退出：\n\n{args.Exception.Message}",
                "DeepSeek Harness", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            ForceExit = true; // 放行 OnClosing 的托盘拦截，确保真正退出
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteLog("[fatal] " + args.ExceptionObject);
            ActiveServer?.Shutdown();
        };

        base.OnStartup(e);

        // 手动创建主窗口（无 StartupUri，便于单实例分支控制）
        var main = new MainWindow();
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ActiveServer?.Shutdown();
        ActiveServer?.Dispose();
        ActiveServer = null;
        _mutex?.Dispose(); // 存在性语义：句柄关闭即释放
        ForceExit = false;
        base.OnExit(e);
    }

    /// <summary>
    /// 是否存在其他存活的 dsh-app 进程（任意版本/路径，进程名相同即拦截；限同一会话，
    /// 避免 RDP/快速用户切换的其它会话实例被误拦）。枚举异常 fail-closed（视为有实例），
    /// 防止扫描失败时双实例并发。
    /// </summary>
    private static bool OtherInstanceRunning()
    {
        var me = Environment.ProcessId;
        int mySession = Process.GetCurrentProcess().SessionId;
        try
        {
            foreach (var p in Process.GetProcessesByName("dsh-app"))
            {
                if (p.Id == me) continue;
                try
                {
                    if (p.SessionId == mySession)
                        return true;
                }
                catch
                {
                    return true; // 无法读取会话信息：保守视为同会话实例
                }
            }
        }
        catch
        {
            return true; // fail-closed：扫描失败按"已有实例"处理，宁可误拒不可并发
        }
        return false;
    }

    private void WriteLog(string message)
    {
        try
        {
            FileLog.Append($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
        catch { /* 忽略 */ }
    }

    /// <summary>通知已运行的实例显示其主窗口（自定义消息经 PostMessage，走 WPF 状态机，避免 ShowWindow 直改底层 HWND 导致 Visibility 不同步）。</summary>
    private static void ActivateExistingWindow()
    {
        var me = Environment.ProcessId;
        IntPtr activated = IntPtr.Zero;
        try
        {
            foreach (var p in Process.GetProcessesByName("dsh-app"))
            {
                if (p.Id == me) continue;
                var hwnd = p.MainWindowHandle;
                if (hwnd != IntPtr.Zero && PostMessage(hwnd, WmDshActivate, IntPtr.Zero, IntPtr.Zero))
                {
                    activated = hwnd;
                    break;
                }
            }
        }
        catch
        {
            // 激活失败静默
        }

        if (activated == IntPtr.Zero)
        {
            // 找不到任何可激活的窗口（如第一实例窗口未创建/托盘隐藏且句柄不可见）：
            // 给出提示而非静默闪退，用户才能知道发生了什么
            try
            {
                MessageBox.Show("dsh-app 已在运行，但未找到可激活的窗口。\n请从托盘图标或任务管理器结束现有实例后重试。",
                    "DeepSeek Harness", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* 忽略 */ }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
