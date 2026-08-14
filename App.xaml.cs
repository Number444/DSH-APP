using System.Diagnostics;
using System.IO;
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
    private const string MutexName = @"Global\DSH-APP-SingleInstance-4F2A9C71";

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

        // 单实例双保险（v1.2.1 加固）：
        // ① 互斥体"存在性"语义：不请求所有权（Mutex(false,…)），对象存在即代表有实例，
        //    规避"持有权释放但进程存活"的竞态（曾导致新/旧版本并存）；
        // ② 进程扫描兜底：任何存活的 dsh-app 进程（含互斥体已丢失的僵尸实例）都会被拦下。
        _mutex = new Mutex(false, MutexName, out var createdNew);
        if (!createdNew && OtherInstanceRunning())
        {
            // 已有实例在运行：激活其窗口后退出本实例
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
        _mutex?.Dispose(); // 存在性语义：无需 ReleaseMutex，句柄关闭即释放
        base.OnExit(e);
    }

    /// <summary>是否存在其他存活的 dsh-app 进程（任意版本/路径，进程名相同即拦截）。</summary>
    private static bool OtherInstanceRunning()
    {
        var me = Environment.ProcessId;
        try
        {
            foreach (var p in Process.GetProcessesByName("dsh-app"))
            {
                if (p.Id != me)
                    return true;
            }
        }
        catch
        {
            // 枚举失败不阻塞启动（互斥体仍是第一道防线）
        }
        return false;
    }

    private void WriteLog(string message)
    {
        try
        {
            var utf8Bom = new System.Text.UTF8Encoding(true);
            if (!File.Exists(_logFilePath))
                File.WriteAllText(_logFilePath, "", utf8Bom);
            File.AppendAllText(_logFilePath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}", utf8Bom);
        }
        catch { /* 忽略 */ }
    }

    private static void ActivateExistingWindow()
    {
        try
        {
            var hwnd = FindWindow(null, "DeepSeek Harness");
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                _ = SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            // 激活失败静默，直接退出即可
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
