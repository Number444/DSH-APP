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

/// <summary>主窗口分部：系统托盘（初始化 / 菜单定位 / 打开动画方向）（自 MainWindow.xaml.cs 拆出，纯移动无逻辑变更）。</summary>
public partial class MainWindow
{
    // ---------------- 系统托盘 ----------------

    /// <summary>初始化托盘图标：提取 exe 自带图标、绑定双击恢复与右键菜单（Popup 方案）。</summary>
    private void InitTray()
    {
        try
        {
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "");
            if (icon is null) return;
            TrayIcon.Icon = icon;
            TrayIcon.Visibility = Visibility.Visible;
            _trayAvailable = true;
            TrayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
            // 右键弹菜单（与 APP 内同款 Popup 菜单；不设 ContextMenu，避免系统样式/动画差异）。
            // 用 Up 而非 Down：按下时打开会让同一交互序列的弹起被 Popup 当作"外部点击"而立即关闭（闪烁）。
            TrayIcon.TrayRightMouseUp += (_, _) => OpenTrayMenu();
            // 气泡点击按种类路由（余额 → 充值页；完成通知 → 恢复窗口；见 MainWindow.Notify.cs）
            TrayIcon.TrayBalloonTipClicked += (_, _) => OnBalloonClicked();
        }
        catch (Exception ex)
        {
            AppendLog($"托盘初始化失败: {ex.Message}");
        }
    }

    /// <summary>在托盘图标旁弹出右键菜单（复用公共菜单控件；紧贴鼠标展开，超屏自动反向——系统右键菜单同款行为）。</summary>
    private void OpenTrayMenu()
    {
        // 已打开 → 再点关闭（toggle，播收拢动画）
        if (TrayMenuPopup.IsOpen)
        {
            AnimateMenuClose(TrayMenuPopup, TrayMenu, true);
            return;
        }

        UpdateTrayMenuItems();
        GetCursorPos(out var pt); // 物理像素
        _trayIconScreenPos = new Point(pt.X, pt.Y); // 记录锚点（钩子命中判定用）

        // 光标所在显示器的工作区与 DPI（多显示器混合 DPI 下不能用主窗口/主屏换算）
        var monitor = MonitorFromPoint(new NativePoint { X = pt.X, Y = pt.Y }, MONITOR_DEFAULTTONEAREST);
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var work = SystemParameters.WorkArea; // DIP，fallback
        if (monitor != IntPtr.Zero)
        {
            var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref mi))
            {
                var rc = mi.rcWork; // 物理像素
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
                    scale = dpiX / 96.0;
                work = new Rect(rc.Left / scale, rc.Top / scale, (rc.Right - rc.Left) / scale, (rc.Bottom - rc.Top) / scale);
            }
        }

        double x = pt.X / scale;
        double y = pt.Y / scale;

        // 菜单实际尺寸（Measure 实量，长文本项不会导致翻转判定失真；减去动画安全区留白得视觉尺寸）
        TrayMenu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double menuW = Math.Max(220, TrayMenu.DesiredSize.Width - 2 * AppMenuPanel.AnimSafePad);
        double menuH = TrayMenu.DesiredSize.Height - 2 * AppMenuPanel.AnimSafePad;

        // 默认在鼠标右下方展开（紧贴）；右/下放不下则反向（图标在任务栏底部时菜单自然落在图标正上方）
        // Popup 定位点是 HWND 左上角（含安全区留白），须反向补偿让视觉位置贴合光标；
        // 超屏判定与抛出方向均用"视觉位置"（offsetX + 安全区）计算，不能直接比较补偿后的 offset
        double offsetX = x + 4 - AppMenuPanel.AnimSafePad;
        if (offsetX + AppMenuPanel.AnimSafePad + menuW > work.Right)
            offsetX = x - menuW - 4 - AppMenuPanel.AnimSafePad;
        double offsetY = y + 4 - AppMenuPanel.AnimSafePad;
        if (offsetY + AppMenuPanel.AnimSafePad + menuH > work.Bottom)
            offsetY = y - menuH - 4 - AppMenuPanel.AnimSafePad;

        TrayMenuPopup.HorizontalOffset = offsetX;
        TrayMenuPopup.VerticalOffset = offsetY;
        // 抛出起点（直上直下，仅垂直分量）：菜单视觉位置在光标下方 → 从上方抛出，反向展开则从下方抛出，距离固定 14px
        _trayFlyFrom = new Point(0, offsetY + AppMenuPanel.AnimSafePad > y ? -14 : 14);
        TrayMenuPopup.IsOpen = true;
    }
}
