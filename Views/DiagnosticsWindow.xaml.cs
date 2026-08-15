using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using dsh_app.Helpers;
using dsh_app.Server;

namespace dsh_app.Views;

/// <summary>诊断行展示模型（状态点颜色在当前主题下解析一次）。</summary>
public sealed record RowView(string Key, string Value, Brush DotBrush);

/// <summary>
/// 诊断信息窗口：一键采集环境与运行状态（并行探测），纯文本复制。
/// 敏感边界：采集内容绝不含任何凭据/Key（见 Diagnostics.CollectAsync）。
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private const int DWM_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private readonly ServerController _server;
    private readonly HarnessUpdater _updater;
    private readonly AppUpdater _appUpdater;
    private readonly Action<bool> _themeHandler;

    private CancellationTokenSource? _cts;
    private bool _closed;
    private List<DiagRow> _rows = new();

    public DiagnosticsWindow(ServerController server, HarnessUpdater updater, AppUpdater appUpdater)
    {
        InitializeComponent();
        _server = server;
        _updater = updater;
        _appUpdater = appUpdater;

        // 打开即采集；关闭取消在途探测，回调不再触碰已销毁控件
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _cts?.Cancel();
        };

        // 主题切换时联动标题栏深色（关闭时解除，防订阅泄漏）
        _themeHandler = _ => ApplyDwm();
        ThemeManager.ThemeChanged += _themeHandler;
        Closed += (_, _) => ThemeManager.ThemeChanged -= _themeHandler;
    }

    /// <summary>采集（并行四项 + 内存项）；字段先占位后填充，固定行高不抖动。</summary>
    private async Task RefreshAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        RowList.ItemsSource = new[] { new RowView("正在采集…", "", (Brush)FindResource("AccentBlueBrush")) };
        RefreshButton.IsEnabled = false;
        CopyStatusText.Text = "";
        try
        {
            var rows = await Diagnostics.CollectAsync(_server, _updater, _appUpdater, ct);
            if (_closed) return;
            _rows = rows;
            RowList.ItemsSource = rows.Select(r => new RowView(r.Key, r.Value, ResolveBrush(r.Status))).ToList();
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭/刷新打断：忽略
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _rows = new List<DiagRow> { new("采集失败", ex.Message, DiagStatus.Error) };
            RowList.ItemsSource = new[]
            {
                new RowView("采集失败", ex.Message, (Brush)FindResource("AccentRedBrush")),
            };
        }
        finally
        {
            if (!_closed) RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>状态 → 当前主题状态色（双套资源，R8）。</summary>
    private Brush ResolveBrush(DiagStatus status) => status switch
    {
        DiagStatus.Ok => (Brush)FindResource("ButtonGreenBrush"),
        DiagStatus.Warn => (Brush)FindResource("AccentOrangeBrush"),
        DiagStatus.Error => (Brush)FindResource("AccentRedBrush"),
        _ => (Brush)FindResource("AccentBlueBrush"),
    };

    /// <summary>纯文本键值块（复制用；与展示一致，无状态点）。</summary>
    private string BuildCopyText()
        => string.Join(Environment.NewLine, _rows.Select(r => $"{r.Key}：{r.Value}"));

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = BuildCopyText();
        CopyButton.IsEnabled = false;
        CopyStatusText.Text = "";
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    CopyButton.Content = "已复制 ✓";
                    await Task.Delay(2000);
                    if (!_closed) CopyButton.Content = "复制全部";
                    return;
                }
                catch
                {
                    // 剪贴板被占用等：短暂重试一次
                    if (attempt == 0) await Task.Delay(200);
                }
            }
            if (!_closed) CopyStatusText.Text = "复制失败，请重试";
        }
        finally
        {
            if (!_closed) CopyButton.IsEnabled = true;
        }
    }

    private void LogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", FileLog.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CopyStatusText.Text = $"打开日志失败：{ex.Message}";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDwm();
    }

    private void ApplyDwm()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int dark = ThemeManager.IsDarkNow ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWM_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
