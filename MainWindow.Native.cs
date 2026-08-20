using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using dsh_app.Helpers;
using dsh_app.Server;
using dsh_app.Views;
using Microsoft.Web.WebView2.Core;

namespace dsh_app;

/// <summary>主窗口分部：DWM 窗口属性 / WM_GETMINMAXINFO 最大化钳制 / P-Invoke 声明 / 窗口位置记忆 / 覆盖层步骤行（自 MainWindow.xaml.cs 拆出，纯移动无逻辑变更）。</summary>
public partial class MainWindow
{
    // ---------------- DWM 窗口属性（深色 + 圆角） ----------------

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 深色跟随主题；自绘标题栏（WindowStyle=None）下恢复 Win11 原生圆角
        ApplyDwmTheme();
        ThemeManager.ThemeChanged += OnThemeChangedApplyDwm; // 命名订阅：OnClosed 解除（静态事件不挂住窗口实例）

        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // 无边框窗口最大化钳制（WM_GETMINMAXINFO）：修正最大化时顶部溢出、底部探入任务栏
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc);

        WindowPlacementStore.Restore(this);
    }

    /// <summary>主题切换时重套 DWM 深色属性（命名方法，便于 OnClosed 解除静态事件订阅）。</summary>
    private void OnThemeChangedApplyDwm(bool _) => ApplyDwmTheme();

    // ---------------- 最大化钳制（无边框窗口经典修复） ----------------

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    /// <summary>
    /// WindowStyle=None + WindowChrome 下，WPF 最大化默认按整个屏幕扩展窗口
    /// （顶部溢出屏幕、底部盖住任务栏）。此处把最大化位置/尺寸钳制到窗口
    /// 所在显示器的工作区（物理像素，系统层面生效，与 DPI 无关）。
    /// </summary>
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 第二实例激活请求（App.ActivateExistingWindow 经 PostMessage 送达）：走 WPF 状态机恢复窗口
        if (App.WmDshActivate != 0 && msg == App.WmDshActivate)
        {
            Dispatcher.BeginInvoke(ShowMainWindow);
            return IntPtr.Zero;
        }

        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref mi)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.ptMaxPosition.X = mi.rcWork.Left;
        mmi.ptMaxPosition.Y = mi.rcWork.Top;
        mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
        mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
        // 不置 handled：让 WPF 的 WmGetMinMaxInfo 继续写 ptMinTrackSize（MinWidth/MinHeight 约束），
        // 它只改 min/maxTrack，不会覆盖上面写入的 ptMaxSize/ptMaxPosition（审查确认）
        return IntPtr.Zero;
    }

    private void ApplyDwmTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = ThemeManager.IsDarkNow ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWM_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}

/// <summary>
/// 窗口位置/尺寸/最大化状态记忆：%LOCALAPPDATA%\dsh-app\window.json。
/// 还原前校验与当前屏幕工作区有交集，防止拔掉外接屏后窗口开到不可见区域。
/// </summary>
internal static class WindowPlacementStore
{
    private sealed record Placement(double Left, double Top, double Width, double Height, bool IsMaximized);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dsh-app", "window.json");

    public static void Save(Window window)
    {
        try
        {
            // 最大化时存 RestoreBounds（还原后的真实位置）
            var bounds = window.WindowState == WindowState.Maximized
                ? window.RestoreBounds
                : new Rect(window.Left, window.Top, window.Width, window.Height);
            var placement = new Placement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                window.WindowState == WindowState.Maximized);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(placement));
        }
        catch { /* 保存失败不影响关闭 */ }
    }

    public static void Restore(Window window)
    {
        Placement? placement = null;
        try
        {
            if (File.Exists(FilePath))
                placement = JsonSerializer.Deserialize<Placement>(File.ReadAllText(FilePath));
        }
        catch { /* 损坏的配置按首次启动处理 */ }
        if (placement is null) return;

        var rect = new Rect(placement.Left, placement.Top,
            Math.Max(window.MinWidth, placement.Width),
            Math.Max(window.MinHeight, placement.Height));

        // 目标矩形必须与虚拟屏幕（所有显示器并集）有可见交集，否则回退默认居中
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!rect.IntersectsWith(virtualScreen)) return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = rect.Left;
        window.Top = rect.Top;
        window.Width = rect.Width;
        window.Height = rect.Height;
        if (placement.IsMaximized)
            window.WindowState = WindowState.Maximized;
    }
}

/// <summary>
/// 覆盖层步骤行：状态图标 + 步骤名 + 详情（含耗时）。
/// </summary>
public sealed class StepRow : INotifyPropertyChanged
{
    private static readonly Brush BrushBlue = new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF));
    private static readonly Brush BrushGreen = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush BrushRed = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly Brush BrushGray = new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x81));

    private readonly Stopwatch _sw = new();
    private StepStatus _status;
    private string _detail = "";

    public ServerStep Step { get; }
    public string Name { get; }

    /// <summary>当前状态（进度条统计完成步数用）。</summary>
    public StepStatus Status => _status;

    public StepRow(ServerStep step, string name)
    {
        Step = step;
        Name = name;
    }

    public string Icon => _status switch
    {
        StepStatus.Running => "⋯",
        StepStatus.Done => "✓",
        StepStatus.Failed => "✗",
        _ => "○",
    };

    public Brush IconBrush => _status switch
    {
        StepStatus.Running => BrushBlue,
        StepStatus.Done => BrushGreen,
        StepStatus.Failed => BrushRed,
        _ => BrushGray,
    };

    public string Detail => _detail;

    public void Update(StepStatus status, string detail)
    {
        if (status == StepStatus.Running)
            _sw.Restart();
        else if (_sw.IsRunning)
            detail = $"{detail} · {_sw.Elapsed.TotalSeconds:0.0}s";

        _status = status;
        _detail = detail;
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(IconBrush));
        OnPropertyChanged(nameof(Detail));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
