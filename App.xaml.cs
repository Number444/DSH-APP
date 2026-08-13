using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using dsh_app.Server;

namespace dsh_app;

/// <summary>
/// 应用入口：单实例互斥、全局异常兜底、服务器引用托管。
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Global\DSH-APP-SingleInstance-4F2A9C71";

    private Mutex? _mutex;
    private bool _ownsMutex;
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

        _mutex = new Mutex(true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            // 已有实例在运行：激活其窗口后退出本实例
            ActivateExistingWindow();
            Shutdown();
            return;
        }

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
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); }
            catch { /* 忽略 */ }
        }
        _mutex?.Dispose();
        base.OnExit(e);
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
