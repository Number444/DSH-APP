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

/// <summary>主窗口分部：菜单（顶栏/托盘/余额共用）、点击外部关闭钩子、菜单项构建（自 MainWindow.xaml.cs 拆出，纯移动无逻辑变更）。</summary>
public partial class MainWindow
{
    // ---------------- 菜单（顶栏下拉 / 托盘右键 / 余额右键共用） ----------------

    private void TitleBtnMenu_Click(object sender, RoutedEventArgs e) => MenuPopup.IsOpen = true;

    /// <summary>菜单关闭统一入口：animated=true 播关闭动画（打开动画的倒放，完成后置 IsOpen=false），
    /// false 瞬关（状态卡交接/隐藏退出路径）。倒放起点由 AppMenuPanel 内部记录（_lastFlyFrom）。</summary>
    private void AnimateMenuClose(Popup popup, AppMenuPanel panel, bool animated)
    {
        if (!popup.IsOpen) return;
        if (animated) panel.PlayCloseAnimation(() => popup.IsOpen = false);
        else popup.IsOpen = false;
    }

    /// <summary>Popup → 面板映射（外部点击钩子遍历用）。</summary>
    private AppMenuPanel PanelOf(Popup popup) =>
        popup == MenuPopup ? TopMenu : popup == BalanceMenuPopup ? BalanceMenu : TrayMenu;

    /// <summary>公共菜单项路由：顶栏（TopMenu）、托盘（TrayMenu）、余额（BalanceMenu）共用。</summary>
    private async void OnTopMenuClicked(string tag)
    {
        AnimateMenuClose(MenuPopup, TopMenu, true);
        AnimateMenuClose(BalanceMenuPopup, BalanceMenu, true);
        AnimateMenuClose(TrayMenuPopup, TrayMenu, true);
        switch (tag)
        {
            case "about":
                new Views.AboutWindow { Owner = this }.ShowDialog();
                break;
            case "settings":
                OpenSettingsDialog();
                break;
            case "log":
                OpenLog();
                break;
            case "diagnostics":
                new Views.DiagnosticsWindow(_server, _updater, _appUpdater) { Owner = this }.ShowDialog();
                break;
            case "checkupdate":
                _ = CheckUpdateAsync();
                break;
            case "checkappupdate":
                _ = CheckAppUpdateAsync();
                break;
            case "show":
                ShowMainWindow();
                break;
            case "refresh":
                // 余额菜单"刷新余额"：状态卡先显示"刷新中"，结果由 BalanceChanged 回调更新
                _userRefreshPending = true;
                ShowBalanceStatus("正在刷新余额…");
                var result = await _balance.RefreshAsync();
                if (result == BalanceMonitor.RefreshResult.Debounced ||
                    result == BalanceMonitor.RefreshResult.Stopped)
                {
                    // 防抖/已停止：无在途请求，回调不会来——复位 pending 并提示
                    _userRefreshPending = false;
                    ShowBalanceStatus(result == BalanceMonitor.RefreshResult.Stopped
                        ? "余额监控未运行"
                        : "操作太频繁，请稍后再试",
                        (Brush)FindResource("AccentOrangeBrush"));
                }
                else if (result == BalanceMonitor.RefreshResult.InFlight)
                {
                    // 轮询/上次请求在途：结果仍会经回调到达，不触碰 pending
                    ShowBalanceStatus("正在获取中…",
                        (Brush)FindResource("AccentOrangeBrush"));
                }
                // Started：等回调更新状态卡
                break;
            case "topup":
                OpenTopUpPage();
                break;
            case "exit":
                // 退出路径：瞬关菜单再关窗（不播收拢动画——窗口即将销毁，杜绝动画时序干扰退出）
                CloseAllMenus();
                _trayExitRequested = true;
                Close();
                break;
        }
    }

    /// <summary>打开设置窗口（顶栏菜单 / 托盘菜单共用）；关闭后同步余额开关状态。</summary>
    private void OpenSettingsDialog()
    {
        var dlg = new Views.SettingsWindow { Owner = this };
        dlg.ShowDialog();
        // 设置可能改了余额开关，关闭后同步启动/停止
        SyncBalanceFromSettings();
    }

    /// <summary>恢复并激活主窗口（托盘双击 / 托盘"打开主窗口"）。</summary>
    private void ShowMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        // 抢焦点：窗口从后台/隐藏恢复时置顶闪回（先置顶再取消，仅抢激活）
        Topmost = true;
        Topmost = false;

        // 应用更新已下载完成（托盘化期间下载继续）：恢复窗口时提示安装
        if (_appUpdateReady)
        {
            _appUpdateReady = false;
            UpdateMenuItems();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!IsVisible) return;
                var dlg = new Views.ConfirmDialog("应用更新已就绪",
                    $"v{_appUpdater.LatestVersion} 已下载完成，重启应用后生效。\n重启期间服务会中断约 10~30 秒。",
                    "立即重启", "稍后") { Owner = this };
                if (dlg.ShowDialog() == true)
                    InstallDownloadedAppUpdate();
            });
        }
    }

    /// <summary>打开 DeepSeek 开放平台充值页（默认浏览器）。</summary>
    private void OpenTopUpPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://platform.deepseek.com/usage") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"打开充值页失败: {ex.Message}");
        }
    }

    // ---------------- 菜单点击外部关闭（低级鼠标钩子） ----------------

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>菜单 Popup 打开时安装全局鼠标钩子（幂等）。</summary>
    private void InstallDismissHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseHookProc = OnLowLevelMouse; // 强引用防 GC
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            _mouseHookProc = null; // 安装失败：退化为 Popup 自身 StaysOpen=False 行为
    }

    /// <summary>所有菜单都关闭后卸载钩子（幂等）。</summary>
    private void UninstallDismissHookIfIdle()
    {
        if (MenuPopup.IsOpen || BalanceMenuPopup.IsOpen || TrayMenuPopup.IsOpen) return;
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseHookProc = null;
    }

    /// <summary>
    /// 任意鼠标按下：点击落在打开的菜单矩形外 → 关闭；落在菜单锚点（触发按钮/托盘图标）上 → 跳过，
    /// 让按钮自身的 Click/toggle 语义生效（否则"按下时钩子关闭 + 弹起时 Click 重开"竞态导致菜单永远关不掉）。
    /// 全程物理像素比较（PointToScreen 与 GetCursorPos 同系），混合 DPI 下不偏移。
    /// </summary>
    private IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                {
                    var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                    var pt = new Point(info.X, info.Y); // 物理像素
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            MenuPopups[0] = MenuPopup;
                            MenuPopups[1] = BalanceMenuPopup;
                            MenuPopups[2] = TrayMenuPopup;
                            foreach (var popup in MenuPopups)
                            {
                                if (!popup.IsOpen) continue;
                                if (IsAnchorHit(popup, pt)) continue; // 锚点命中：交给按钮 toggle
                                var rect = GetPopupScreenRect(popup);
                                if (!rect.IsEmpty)
                                {
                                    // 动画安全区（透明留白）不算"菜单内"：内缩后判定，点击留白即外部关闭。
                                    // DPI 取 Popup 自身所在显示器（Child 的 DPI 上下文），混 DPI 下内缩量才准确
                                    double dpi = popup.Child is FrameworkElement fe
                                        ? VisualTreeHelper.GetDpi(fe).PixelsPerDip
                                        : VisualTreeHelper.GetDpi(this).PixelsPerDip;
                                    double padPx = AppMenuPanel.AnimSafePad * dpi;
                                    rect.Inflate(-padPx, -padPx);
                                    if (!rect.Contains(pt))
                                        AnimateMenuClose(popup, PanelOf(popup), true);
                                }
                            }
                            UninstallDismissHookIfIdle();
                        }
                        catch
                        {
                            // 关闭期 Dispatcher 异常等：忽略（菜单关闭是尽力而为）
                        }
                    });
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }
        catch
        {
            // 钩子回调在系统钩子线程：任何托管异常逃逸都会终止进程，必须兜住
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }
    }

    /// <summary>点击是否落在菜单锚点（触发按钮/托盘图标）上——命中则不关闭该菜单。</summary>
    private bool IsAnchorHit(Popup popup, Point physPt)
    {
        try
        {
            if (popup == MenuPopup)
                return IsPointInElementScreenRect(TitleBtnMenu, physPt);
            if (popup == BalanceMenuPopup)
                return IsPointInElementScreenRect(TitleBalance, physPt);
            if (popup == TrayMenuPopup)
                return _trayIconScreenPos is Point p &&
                       Math.Abs(physPt.X - p.X) < 32 &&
                       Math.Abs(physPt.Y - p.Y) < 32;
        }
        catch
        {
            // 命中判定失败按"未命中"处理（菜单可被外部点击关闭，无碍）
        }
        return false;
    }

    /// <summary>物理像素点是否落在元素屏幕矩形内。</summary>
    private static bool IsPointInElementScreenRect(FrameworkElement element, Point physPt)
    {
        if (element.Visibility != Visibility.Visible) return false;
        var dpi = VisualTreeHelper.GetDpi(element).PixelsPerDip;
        var topLeft = element.PointToScreen(new Point(0, 0)); // 物理像素
        var rect = new Rect(topLeft.X, topLeft.Y, element.ActualWidth * dpi, element.ActualHeight * dpi);
        return rect.Contains(physPt);
    }

    /// <summary>Popup 内容在屏幕上的矩形（物理像素；Popup 是独立 HWND，Child.PointToScreen 即为屏幕坐标）。</summary>
    private static Rect GetPopupScreenRect(Popup popup)
    {
        if (!popup.IsOpen || popup.Child is not FrameworkElement child)
            return Rect.Empty;
        try
        {
            var topLeft = child.PointToScreen(new Point(0, 0)); // 物理像素
            var dpi = VisualTreeHelper.GetDpi(child).PixelsPerDip;
            return new Rect(topLeft.X, topLeft.Y, child.ActualWidth * dpi, child.ActualHeight * dpi);
        }
        catch
        {
            return Rect.Empty; // 测量未完成等瞬时状态：跳过本次判定
        }
    }

    /// <summary>托盘菜单项：打开主窗口 / 检查 Harness 更新 / 检查应用更新 / 诊断信息 / 设置 / 退出（状态与顶栏同步）。</summary>
    private void UpdateTrayMenuItems()
    {
        var items = new List<AppMenuItem>
        {
            new("show", "打开主窗口"),
        };
        AppendUpdateItems(items);
        items.Add(new AppMenuItem("diagnostics", "诊断信息"));
        items.Add(new AppMenuItem("settings", "设置"));
        items.Add(new AppMenuItem("exit", "退出"));
        TrayMenu.ItemsSource = items;
    }

    /// <summary>
    /// 构建两个更新菜单项（Harness + 应用），顶栏/托盘共用。
    /// 统一入口守卫：任一检查/更新进行中，两个更新入口均禁用（防"下载中再触发更新"、弹窗叠加、停服交错）。
    /// </summary>
    private void AppendUpdateItems(List<AppMenuItem> items)
    {
        var anyBusy = _checking || _updating || _appChecking || _appUpdating;

        // Harness（npm 包）更新项
        if (_updating)
            items.Add(new AppMenuItem("checkupdate", "正在更新 Harness…", Enabled: false));
        else if (_checking)
            items.Add(new AppMenuItem("checkupdate", "检查 Harness 更新…", Enabled: false));
        else if (_updater.LastCheckSucceeded && _updater.HasUpdate)
            items.Add(new AppMenuItem("checkupdate",
                $"更新可用：v{_updater.LocalVersion} → v{_updater.LatestVersion}",
                (Brush)FindResource("AccentBlueBrush")));
        else
            items.Add(new AppMenuItem("checkupdate", "检查 Harness 更新", Enabled: !anyBusy));

        // 应用（壳自身）更新项
        if (_appUpdating)
            items.Add(new AppMenuItem("checkappupdate", "正在更新应用…", Enabled: false));
        else if (_appUpdateReady)
            items.Add(new AppMenuItem("checkappupdate",
                $"更新就绪：v{_appUpdater.LocalVersion} → v{_appUpdater.LatestVersion}",
                (Brush)FindResource("AccentBlueBrush"), Enabled: !anyBusy));
        else if (_appChecking)
            items.Add(new AppMenuItem("checkappupdate", "检查应用更新…", Enabled: false));
        else if (_appUpdater.LastCheckSucceeded && _appUpdater.HasUpdate)
            items.Add(new AppMenuItem("checkappupdate",
                $"更新可用：v{_appUpdater.LocalVersion} → v{_appUpdater.LatestVersion}",
                (Brush)FindResource("AccentBlueBrush"), Enabled: !anyBusy));
        else
            items.Add(new AppMenuItem("checkappupdate", "检查应用更新", Enabled: !anyBusy));
    }

    /// <summary>重建顶栏/托盘菜单项：有新版时高亮提示（主题色走资源，R8），检查/更新中显示进行态。</summary>
    private void UpdateMenuItems()
    {
        var items = new List<AppMenuItem>
        {
            new("about", "关于"),
            new("settings", "设置"),
            new("log", "日志"),
            new("diagnostics", "诊断信息"),
        };
        AppendUpdateItems(items);
        // 顶栏"退出"（与托盘一致）：退出整个 harness（壳 + dsh 服务）
        items.Add(new AppMenuItem("exit", "退出"));
        TopMenu.ItemsSource = items;
        UpdateTrayMenuItems(); // 托盘更新项与顶栏联动
    }
}
