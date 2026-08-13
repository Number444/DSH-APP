using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using dsh_app.Server;
using Microsoft.Web.WebView2.Core;

namespace dsh_app;

/// <summary>
/// 主窗口：WebView2 承载 Harness UI，覆盖层负责启动状态与错误提示。
/// </summary>
public partial class MainWindow : Window
{
    private const int DWM_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly ServerController _server = new();
    private readonly StringBuilder _logTail = new();
    private readonly string _logFilePath;

    public MainWindow()
    {
        InitializeComponent();

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dsh-app");
        Directory.CreateDirectory(logDir);
        _logFilePath = Path.Combine(logDir, "app.log");

        _server.Log += msg => Dispatcher.Invoke(() => AppendLog(msg));

        App.ActiveServer = _server;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // ---------------- 启动流程 ----------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitWebViewAsync();
            ShowLoading();
            var ok = await _server.EnsureServerAsync();
            if (!ok)
            {
                ShowError("dsh 服务启动失败", _server.IsSelfStarted
                    ? "请查看日志确认 dsh 是否安装、端口是否被占用。"
                    : _logTail.ToString().TrimEnd(), allowRetry: true);
                return;
            }

            WebView.CoreWebView2.Navigate($"http://127.0.0.1:{_server.Port}");
        }
        catch (Exception ex)
        {
            ShowError("初始化失败", ex.Message);
        }
    }

    private async Task InitWebViewAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dsh-app",
            "WebView2");

        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await WebView.EnsureCoreWebView2Async(env);

        var wv = WebView.CoreWebView2;
        wv.Settings.AreDefaultContextMenusEnabled = true;
        wv.Settings.IsStatusBarEnabled = false;

        wv.NavigationCompleted += OnNavigationCompleted;
        wv.ProcessFailed += OnProcessFailed;

        AppendLog("WebView2 就绪");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            AppendLog($"页面加载完成: {WebView.CoreWebView2.Source}");
            HideOverlay();
        }
        else
        {
            AppendLog($"页面加载失败: {e.WebErrorStatus}");
            ShowError("页面加载失败", $"WebView2 无法连接服务器（{e.WebErrorStatus}）。\n服务器可能已停止。", allowRetry: true);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            Dispatcher.Invoke(() =>
                ShowError("页面进程已退出", "浏览器内核异常退出，服务器可能仍在运行。", allowRetry: true));
        }
    }

    // ---------------- 覆盖层控制 ----------------

    private void ShowLoading()
    {
        Overlay.Visibility = Visibility.Visible;
        OverlayGlyph.Text = "◈";
        OverlayTitle.Text = "正在启动 DeepSeek Harness…";
        OverlayDetail.Text = string.IsNullOrWhiteSpace(_logTail.ToString())
            ? "正在拉起 dsh web 服务并等待就绪，首次启动可能需要 10~20 秒…"
            : _logTail.ToString().TrimEnd();
        OverlayProgress.Visibility = Visibility.Visible;
        OverlayActions.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string title, string detail, bool allowRetry = false)
    {
        Overlay.Visibility = Visibility.Visible;
        OverlayGlyph.Text = "⚠";
        OverlayGlyph.Foreground = System.Windows.Media.Brushes.Orange;
        OverlayTitle.Text = title;
        OverlayDetail.Text = detail;
        OverlayProgress.Visibility = Visibility.Collapsed;
        OverlayActions.Visibility = Visibility.Visible;
        RetryButton.Visibility = allowRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

    // ---------------- 按钮事件 ----------------

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        try
        {
            ShowLoading();
            _server.Shutdown(); // 若此前拉起过，先清理干净
            var ok = await _server.EnsureServerAsync();
            if (!ok)
            {
                ShowError("dsh 服务启动失败", _logTail.ToString().TrimEnd(), allowRetry: true);
                return;
            }
            WebView.CoreWebView2.Navigate($"http://127.0.0.1:{_server.Port}");
        }
        catch (Exception ex)
        {
            ShowError("重试失败", ex.Message, allowRetry: true);
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    private void LogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", _logFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"打开日志失败: {ex.Message}");
        }
    }

    // ---------------- 日志 ----------------

    private void AppendLog(string message)
    {
        _logTail.AppendLine(message);
        if (_logTail.Length > 8_000)
            _logTail.Remove(0, _logTail.Length - 8_000);

        try
        {
            var utf8Bom = new UTF8Encoding(true);
            if (!File.Exists(_logFilePath))
                File.WriteAllText(_logFilePath, "", utf8Bom);
            File.AppendAllText(_logFilePath, message + Environment.NewLine, utf8Bom);
        }
        catch { /* 日志写入失败不影响主流程 */ }

        // 覆盖层可见时同步展示最新日志
        if (Overlay.Visibility == Visibility.Visible)
            OverlayDetail.Text = _logTail.ToString().TrimEnd();
    }

    // ---------------- 生命周期 ----------------

    private void OnClosed(object? sender, EventArgs e)
    {
        _server.Shutdown();
        _server.Dispose();
        App.ActiveServer = null;
    }

    // ---------------- DWM 深色标题栏 ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int attr = 1;
            _ = DwmSetWindowAttribute(hwnd, DWM_USE_IMMERSIVE_DARK_MODE, ref attr, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
