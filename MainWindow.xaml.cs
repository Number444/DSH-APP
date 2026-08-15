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

/// <summary>
/// 主窗口：WebView2 承载 Harness UI，覆盖层负责启动状态与错误提示。
/// </summary>
public partial class MainWindow : Window
{
    private const int DWM_USE_IMMERSIVE_DARK_MODE = 20;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly ServerController _server = new();
    private readonly HarnessUpdater _updater = new();
    private readonly AppUpdater _appUpdater = new();
    private readonly BalanceMonitor _balance = new();
    private readonly ObservableCollection<StepRow> _steps = new();
    private readonly DispatcherTimer _heartbeat;
    private int _heartbeatMisses;
    private bool _rendererReloadTried;
    private bool _autoCheckRan;
    private bool _checking;
    private bool _updating;
    private bool _balanceRunning;
    private bool _abortUpdateConfirmed;
    /// <summary>应用（壳自身）更新状态机：检查中 / 下载安装中 / 已下载待安装（与 Harness 的 _checking/_updating 完全拆分，统一入口守卫）。</summary>
    private bool _appChecking;
    private bool _appUpdating;
    private bool _appUpdateReady;
    /// <summary>应用自更新重启放行：OnClosing 在托盘化分支前短路（否则默认托盘化会拦截 Close，更新器空等超时）。</summary>
    private bool _quitForAppUpdate;
    /// <summary>应用自更新自动检查防重入（独立于 Harness 的 _autoCheckRan）。</summary>
    private bool _appAutoCheckRan;
    /// <summary>应用更新下载取消令牌（托盘化不取消；真退出/取消按钮才 Cancel）。</summary>
    private CancellationTokenSource? _appDownloadCts;
    /// <summary>手动检查更新的进度弹窗（非模态；检查完成自动关闭，防"按钮变灰干等"）。</summary>
    private Views.UpdateProgressWindow? _checkProgress;
    /// <summary>托盘"退出"请求：跳过最小化到托盘拦截，真正退出。</summary>
    private bool _trayExitRequested;
    /// <summary>首次隐藏到托盘时是否已提示。</summary>
    private bool _trayHintShown;
    /// <summary>余额是否高于告警阈值（初始视为高：首次刷新低于阈值即告警；恢复后复位）。</summary>
    private bool _wasAboveThreshold = true;
    /// <summary>余额状态色阈值（固定）：低于此值变黄（告警）。</summary>
    private const decimal BalanceWarnThreshold = 5m;
    /// <summary>余额状态色阈值（固定）：低于此值变红（危险）。</summary>
    private const decimal BalanceDangerThreshold = 2m;
    /// <summary>用户点击余额触发的刷新待结果（仅此场景弹状态窗；后台轮询不弹）。</summary>
    private bool _userRefreshPending;
    /// <summary>状态窗序号：防快速连续点击时旧延迟关闭误关新弹窗。</summary>
    private int _statusSeq;

    // ---- 菜单"点击外部关闭"（低级鼠标钩子） ----
    // WebView2 是原生 HWND：点击页面区域不进入 WPF 输入路由，Popup 的 StaysOpen=False
    // 检测不到"外部点击"（托盘场景 Popup 捕获还可能被托盘交互抢占）→ 用 WH_MOUSE_LL 全局兜底。
    private IntPtr _mouseHook;
    private LowLevelMouseProc? _mouseHookProc;
    /// <summary>托盘菜单打开时记录的托盘图标位置（物理像素；作锚点命中判定）。</summary>
    private Point? _trayIconScreenPos;
    /// <summary>托盘图标是否挂载成功（失败时关窗不得走"最小化到托盘"）。</summary>
    private bool _trayAvailable;
    /// <summary>钩子热路径遍历的菜单集合（static 避免每次点击分配数组）。</summary>
    private static readonly Popup[] MenuPopups = new Popup[3];
    /// <summary>托盘菜单打开动画的抛出起点（相对菜单位置；由 OpenTrayMenu 按光标方向计算）。</summary>
    private Point _trayFlyFrom;

    public MainWindow()
    {
        InitializeComponent();
        VersionFooter.Text = $"dsh-app v{App.AppVersion} · DeepSeek Harness 桌面壳";

        _server.Log += msg => Dispatcher.Invoke(() => AppendLog(msg));
        _server.StepChanged += (step, status, detail) => Dispatcher.Invoke(() => UpdateStep(step, status, detail));
        _server.ServerDied += () => Dispatcher.Invoke(OnServerDied);

        // Harness 更新器：日志走同一通道
        _updater.Log += msg => Dispatcher.Invoke(() => AppendLog(msg));

        // 应用（壳自身）更新器：日志走同一通道
        _appUpdater.Log += msg => Dispatcher.Invoke(() => AppendLog(msg));

        // 余额监控：回调经 Dispatcher 上 UI（R4 线程铁律）
        _balance.BalanceChanged += text => Dispatcher.Invoke(() => OnBalanceChanged(text));
        _balance.BalanceAmountChanged += amount => Dispatcher.Invoke(() => OnBalanceAmountChanged(amount));

        // 顶栏菜单：公共菜单控件 + 数据驱动菜单项（更新状态高亮由 UpdateMenuItems 维护）
        TopMenu.ItemClicked += OnTopMenuClicked;
        UpdateMenuItems();

        // 余额左键菜单（刷新 / 充值；右键交互已删除）
        BalanceMenu.ItemClicked += OnTopMenuClicked;
        BalanceMenu.ItemsSource = new[]
        {
            new AppMenuItem("refresh", "刷新余额"),
            new AppMenuItem("topup", "打开充值页"),
        };

        // 托盘菜单：与 APP 内菜单同一公共控件（Popup 定位，弃用 ContextMenu）；内容由 UpdateMenuItems 联动
        TrayMenu.ItemClicked += OnTopMenuClicked;

        // 菜单"点击外部关闭"：三个 Popup 全部接入（打开时装钩子，关闭时自动卸载）
        // 打开动画经 Opened 统一驱动：顶栏/余额从按钮上方抛出（(0,-20)），托盘按光标方向
        MenuPopup.Opened += (_, _) => { InstallDismissHook(); TopMenu.PlayOpenAnimation(new Point(0, -20)); };
        MenuPopup.Closed += (_, _) => UninstallDismissHookIfIdle();
        BalanceMenuPopup.Opened += (_, _) => { InstallDismissHook(); BalanceMenu.PlayOpenAnimation(new Point(0, -20)); };
        BalanceMenuPopup.Closed += (_, _) => UninstallDismissHookIfIdle();
        TrayMenuPopup.Opened += (_, _) => { InstallDismissHook(); TrayMenu.PlayOpenAnimation(_trayFlyFrom); };
        TrayMenuPopup.Closed += (_, _) => UninstallDismissHookIfIdle();
        // 余额状态卡：轻量档打开动画（淡入 + 轻放大，无位移无弹性）
        BalanceStatusPopup.Opened += (_, _) =>
        {
            if (BalanceStatusPopup.Child is FrameworkElement child)
                PopupAnimator.PlayOpen(child, options: PopupAnimator.LightOpen);
        };

        // 系统托盘：图标/事件（失败静默，不影响主流程）
        InitTray();

        // 接管模式（外部 dsh）心跳：进程不归属本壳，只能靠 HTTP 探测感知死亡
        _heartbeat = new DispatcherTimer { Interval = HeartbeatInterval };
        _heartbeat.Tick += OnHeartbeatTick;

        // 预置 5 步状态行（前 4 步由 ServerController 驱动，第 5 步由窗口维护）
        _steps.Add(new StepRow(ServerStep.Detect, "探测已有服务"));
        _steps.Add(new StepRow(ServerStep.Resolve, "定位运行环境"));
        _steps.Add(new StepRow(ServerStep.Launch, "启动 dsh web 进程"));
        _steps.Add(new StepRow(ServerStep.WaitReady, "等待服务就绪"));
        _steps.Add(new StepRow(ServerStep.Load, "加载界面"));
        StepList.ItemsSource = _steps;

        App.ActiveServer = _server;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += (_, _) => UpdateMaxButtonGlyph();
    }

    // ---------------- 启动流程 ----------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 最先显示覆盖层：WebView2 首次冷启动可能耗时数秒，不能让它黑屏干等
            ShowLoading();

            // WebView2 初始化与服务拉起互不依赖，并行执行缩短冷启动
            var webTask = InitWebViewAsync();
            var srvTask = _server.EnsureServerAsync();
            await Task.WhenAll(webTask, srvTask);

            if (!srvTask.Result)
            {
                ShowError("dsh 服务启动失败", StartupFailureDetail(), allowRetry: true);
                return;
            }

            WebView.CoreWebView2.Navigate($"http://127.0.0.1:{_server.Port}");
        }
        catch (Exception ex)
        {
            ShowError("初始化失败", ex.Message);
        }

        // 上次应用自更新回滚提示（标记由更新器脚本写入；展示后删除）
        CheckRolledBackFlag();
    }

    /// <summary>检测 update\rolled-back.flag：更新器回滚后弹说明，用户不能无声无息。</summary>
    private void CheckRolledBackFlag()
    {
        try
        {
            var flag = Path.Combine(AppUpdater.UpdateDir, "rolled-back.flag");
            if (!File.Exists(flag)) return;
            File.Delete(flag);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!IsVisible) return;
                new Views.ConfirmDialog("更新回滚",
                    $"上次应用更新未能完成，已自动回滚到 v{App.AppVersion}。\n详情见日志（updater.log）。",
                    "知道了", glyph: "⚠") { Owner = this }.ShowDialog();
            });
        }
        catch
        {
            // 检测失败静默
        }
    }

    private async Task InitWebViewAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dsh-app",
            "WebView2");

        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        // 与窗口同底色，消除导航/刷新瞬间的白色闪烁
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0D, 0x11, 0x17);
        await WebView.EnsureCoreWebView2Async(env);

        var wv = WebView.CoreWebView2;
        wv.Settings.AreDefaultContextMenusEnabled = true;
        wv.Settings.IsStatusBarEnabled = false;
        // 深色偏好传递给页面：滚动条/表单控件走深色（仅偏好提示，不改页面内容）
        wv.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

        wv.NavigationCompleted += OnNavigationCompleted;
        wv.ProcessFailed += OnProcessFailed;

        AppendLog("WebView2 就绪");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            AppendLog($"页面加载完成: {WebView.CoreWebView2.Source}");
            UpdateStep(ServerStep.Load, StepStatus.Done, "界面加载完成");
            // 页面已渲染完成：显示 WebView2 再收起覆盖层，避免黑屏空窗
            WebView.Visibility = Visibility.Visible;
            HideOverlay();
            _rendererReloadTried = false;
            StartHeartbeatIfAdopted();
            StartBalanceIfEnabled();
            MaybeAutoCheckUpdate();
        }
        else
        {
            AppendLog($"页面加载失败: {e.WebErrorStatus}");
            UpdateStep(ServerStep.Load, StepStatus.Failed, $"WebView2 连接失败（{e.WebErrorStatus}）");
            ShowError("页面加载失败", $"WebView2 无法连接服务器（{e.WebErrorStatus}）。\n服务器可能已停止。", allowRetry: true);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                Dispatcher.Invoke(() =>
                {
                    // R1：先收起 WebView2，覆盖层才可见
                    WebView.Visibility = Visibility.Collapsed;
                    ShowError("页面进程已退出", "浏览器内核异常退出，服务器可能仍在运行。", allowRetry: true);
                });
                break;

            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                // 渲染进程崩溃：自动刷新一次，仍失败则交给 NavigationCompleted 的错误路径
                Dispatcher.Invoke(() =>
                {
                    AppendLog($"渲染进程异常（{e.ProcessFailedKind}），自动刷新页面");
                    if (!_rendererReloadTried && WebView.CoreWebView2 is not null)
                    {
                        _rendererReloadTried = true;
                        WebView.CoreWebView2.Reload();
                    }
                    else
                    {
                        ShowError("页面渲染异常", "页面进程反复崩溃，请重试或重启服务。", allowRetry: true);
                    }
                });
                break;
        }
    }

    // ---------------- 服务断连检测 ----------------

    /// <summary>自家拉起的 dsh 进程在就绪后中途退出（ServerController.ServerDied）。</summary>
    private void OnServerDied()
    {
        AppendLog("检测到 dsh 服务进程已退出");
        ShowServiceLostError("dsh 服务进程意外退出。");
    }

    /// <summary>接管模式下页面加载完成后启动心跳（自家模式由进程 Exited 事件覆盖）。</summary>
    private void StartHeartbeatIfAdopted()
    {
        _heartbeatMisses = 0;
        if (!_server.IsSelfStarted)
            _heartbeat.Start();
    }

    private async void OnHeartbeatTick(object? sender, EventArgs e)
    {
        try
        {
            var alive = await _server.CheckAliveAsync();
            if (alive)
            {
                _heartbeatMisses = 0;
                return;
            }
            _heartbeatMisses++;
            if (_heartbeatMisses < 2) return; // 连续 2 次失败才判定死亡，避免单次抖动误报

            _heartbeat.Stop();
            AppendLog("心跳检测：dsh 服务已停止响应");
            ShowServiceLostError("dsh 服务已停止响应（可能已被外部关闭）。");
        }
        catch (Exception ex)
        {
            // async void 零防护点：单次心跳异常不得击穿全局异常兜底
            AppendLog($"心跳检测异常：{ex.Message}");
        }
    }

    /// <summary>服务丢失统一入口：收起 WebView（airspace，WPF 覆盖层盖不住 HWND）再显示错误卡。</summary>
    private void ShowServiceLostError(string detail)
    {
        _heartbeat.Stop();
        WebView.Visibility = Visibility.Collapsed;
        ShowError("服务连接已断开", detail + "\n点击重试重新拉起服务。", allowRetry: true);
    }

    // ---------------- 覆盖层控制 ----------------

    /// <summary>淡入显示覆盖层（纯 WPF 层，不受 airspace 限制）。</summary>
    private void FadeInOverlay()
    {
        Overlay.BeginAnimation(OpacityProperty, null); // 清除残留动画，避免状态竞争
        if (Overlay.Visibility != Visibility.Visible)
        {
            Overlay.Opacity = 0;
            Overlay.Visibility = Visibility.Visible;
        }
        Overlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
    }

    private void ShowLoading()
    {
        _heartbeat.Stop(); // 启动/重试期间不做死亡判定
        ResetSteps();
        FadeInOverlay();
        CenterBrand.Visibility = Visibility.Visible;
        CenterSubtitle.Text = "正在启动 DeepSeek Harness…";
        CenterError.Visibility = Visibility.Collapsed;
        OverlayProgress.Visibility = Visibility.Visible;
        var tail = FileLog.TailText;
        OverlayDetail.Text = string.IsNullOrWhiteSpace(tail)
            ? "正在拉起 dsh web 服务并等待就绪，首次启动可能需要 10~20 秒…"
            : tail.TrimEnd();
        OverlayLogScroller.ScrollToEnd();
    }

    /// <summary>重置全部步骤为待执行（重试/重新启动时调用）。</summary>
    private void ResetSteps()
    {
        foreach (var row in _steps)
            row.Update(StepStatus.Pending, "");
    }

    /// <summary>更新某一步骤的状态（来自 ServerController 事件或本窗口）。</summary>
    private void UpdateStep(ServerStep step, StepStatus status, string detail)
    {
        var row = _steps.FirstOrDefault(r => r.Step == step);
        if (row is not null)
            row.Update(status, detail);
    }

    private void ShowError(string title, string detail, bool allowRetry = false)
    {
        FadeInOverlay();
        CenterBrand.Visibility = Visibility.Collapsed;
        CenterError.Visibility = Visibility.Visible;
        OverlayGlyph.Text = "⚠";
        OverlayGlyph.Foreground = Brushes.Orange;
        OverlayTitle.Text = title;
        OverlayErrorDetail.Text = detail;
        OverlayProgress.Visibility = Visibility.Collapsed;
        OverlayActions.Visibility = Visibility.Visible;
        RetryButton.Visibility = allowRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>淡出并收起覆盖层；调用前必须先显示 WebView2（R1 时序）。</summary>
    private void HideOverlay()
    {
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        anim.Completed += (_, _) =>
        {
            Overlay.Visibility = Visibility.Collapsed;
            Overlay.Opacity = 1; // 复位，供下次 FadeIn 从 0 起播
        };
        Overlay.BeginAnimation(OpacityProperty, anim);
    }

    // ---------------- 按钮事件 ----------------

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        try
        {
            // R1：先收起 WebView2，覆盖层才可见（审查加固：与更新流程同一时序）
            WebView.Visibility = Visibility.Collapsed;
            ShowLoading();
            _server.Shutdown(); // 若此前拉起过，先清理干净
            var ok = await _server.EnsureServerAsync();
            if (!ok)
            {
                ShowError("dsh 服务启动失败", StartupFailureDetail(), allowRetry: true);
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

    private void LogButton_Click(object sender, RoutedEventArgs e) => OpenLog();

    // ---------------- 顶栏按钮 ----------------

    private void TitleBtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        // 更新中禁止刷新：会触发页面重载与错误覆盖层，与更新流程并发 EnsureServerAsync（审查加固项）
        if (_updating) return;
        if (WebView.CoreWebView2 is not null)
            WebView.CoreWebView2.Reload();
    }

    private void TitleBtnBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{_server.Port}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"打开浏览器失败: {ex.Message}");
        }
    }

    private async void TitleBtnRestart_Click(object sender, RoutedEventArgs e)
    {
        TitleBtnRestart.IsEnabled = false;
        try
        {
            // R1：先收起 WebView2，覆盖层才可见（审查加固：与更新流程同一时序）
            WebView.Visibility = Visibility.Collapsed;
            ShowLoading();
            _server.Shutdown();
            var ok = await _server.EnsureServerAsync();
            if (!ok)
            {
                ShowError("dsh 服务启动失败", StartupFailureDetail(), allowRetry: true);
                return;
            }
            WebView.CoreWebView2.Navigate($"http://127.0.0.1:{_server.Port}");
        }
        catch (Exception ex)
        {
            ShowError("重启失败", ex.Message, allowRetry: true);
        }
        finally
        {
            TitleBtnRestart.IsEnabled = true;
        }
    }

    // ---------------- 菜单（顶栏下拉 / 托盘右键 / 余额右键共用） ----------------

    private void TitleBtnMenu_Click(object sender, RoutedEventArgs e) => MenuPopup.IsOpen = true;

    /// <summary>菜单关闭统一入口：animated=true 播收拢动画（完成后置 IsOpen=false），false 瞬关（状态卡交接/隐藏退出路径）。
    /// 收拢方向：托盘向光标方向（复用打开时的 _trayFlyFrom 方向），顶栏/余额向按钮（上方）。</summary>
    private void AnimateMenuClose(Popup popup, AppMenuPanel panel, bool animated)
    {
        if (!popup.IsOpen) return;
        if (animated) panel.PlayCloseAnimation(() => popup.IsOpen = false, ShrinkToOf(popup));
        else popup.IsOpen = false;
    }

    /// <summary>Popup → 面板映射（外部点击钩子遍历用）。</summary>
    private AppMenuPanel PanelOf(Popup popup) =>
        popup == MenuPopup ? TopMenu : popup == BalanceMenuPopup ? BalanceMenu : TrayMenu;

    /// <summary>收拢目标方向（DIP）：与打开时抛出方向一致（托盘指向光标，顶栏/余额指向按钮上方）。</summary>
    private Point ShrinkToOf(Popup popup) => popup == TrayMenuPopup
        ? new Point(Math.Sign(_trayFlyFrom.X) * 10, Math.Sign(_trayFlyFrom.Y) * 10)
        : new Point(0, -10);

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
                    $"已下载 v{_appUpdater.LatestVersion}。是否现在重启应用完成更新？\n重启时服务短暂中断（约 10~30 秒）。",
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
            // 余额告警气泡点击 → 充值页
            TrayIcon.TrayBalloonTipClicked += (_, _) => OpenTopUpPage();
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
                if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0)
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
        // 抛出起点：菜单视觉位置在光标右/下方 → 从左/上方抛出（反向展开则反向），距离固定 10px
        _trayFlyFrom = new Point(
            offsetX + AppMenuPanel.AnimSafePad > x / scale ? -10 : 10,
            offsetY + AppMenuPanel.AnimSafePad > y / scale ? -10 : 10);
        TrayMenuPopup.IsOpen = true;
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

    // ---------------- 菜单（Harness 更新） ----------------

    /// <summary>弹出检查进度窗（非模态，Owner 主窗口；重复调用先关旧的）。</summary>
    private void ShowCheckProgress(string title, string detail)
    {
        _checkProgress?.Close();
        _checkProgress = new Views.UpdateProgressWindow(title, detail) { Owner = this };
        _checkProgress.Show();
    }

    /// <summary>关闭检查进度窗（幂等）。</summary>
    private void HideCheckProgress()
    {
        _checkProgress?.Close();
        _checkProgress = null;
    }

    /// <summary>检查 Harness 更新：顶栏菜单与托盘菜单共用入口。防重入，状态经 UpdateMenuItems 反映。</summary>
    private async Task CheckUpdateAsync()
    {
        if (_checking || _updating) return;
        _checking = true;
        UpdateMenuItems(); // "检查更新…"禁用态
        ShowCheckProgress("检查更新", "正在检查 Harness 更新…");
        try
        {
            // 手动点击一律重新检查（不复用启动时的陈旧结果，审查加固项）
            var ok = await _updater.CheckAsync();
            HideCheckProgress(); // 检查完成：先关进度窗再弹结果
            if (!ok)
            {
                new Views.ConfirmDialog("检查更新",
                    $"检查更新失败：{_updater.LastError ?? "未知原因"}。\n详情见日志。",
                    "知道了", glyph: "⚠") { Owner = this }.ShowDialog();
                return;
            }

            if (_updater.HasUpdate)
            {
                var dlg = new Views.ConfirmDialog("发现 Harness 新版本",
                    $"当前版本：v{_updater.LocalVersion ?? "未知"}\n" +
                    $"最新版本：v{_updater.LatestVersion}\n\n" +
                    "更新期间服务会中断，页面将重新加载（约 1 分钟）。是否继续？",
                    "更新", "暂不") { Owner = this };
                if (dlg.ShowDialog() == true)
                    await RunHarnessUpdateAsync();
            }
            else
            {
                new Views.ConfirmDialog("检查更新",
                    _updater.LocalVersion is null
                        ? "已是最新版本（本地版本未知）。"
                        : $"已是最新版本（v{_updater.LocalVersion}）。",
                    "知道了", glyph: "ℹ") { Owner = this }.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            // fire-and-forget 入口：检查期间退出（HarnessUpdater.Dispose 释放管道流）会抛
            // ObjectDisposedException——必须在此兜住，否则直达 DispatcherUnhandledException 崩溃
            HideCheckProgress();
            AppendLog($"检查更新异常：{ex.Message}");
        }
        finally
        {
            _checking = false;
            UpdateMenuItems(); // 复位"检查更新…"文案（有新版则显示高亮提示）
        }
    }

    /// <summary>执行 Harness 更新：收起页面（R1）→ 停服 → npm install → 重启服务。</summary>
    private async Task RunHarnessUpdateAsync()
    {
        // 前置：端口服务必须由本应用管理（自家拉起或接管验证过的 dsh），非 dsh 占用时无法安全停服
        if (!_server.IsManaged)
        {
            new Views.ConfirmDialog("无法更新",
                "当前端口被非 dsh 服务占用，无法安全更新 Harness。\n请先停止该服务后再试。",
                "知道了", glyph: "⚠") { Owner = this }.ShowDialog();
            return;
        }

        _updating = true;
        _abortUpdateConfirmed = false;
        TitleBtnRestart.IsEnabled = false;
        UpdateMenuItems(); // 菜单项显示"正在更新 Harness…"
        try
        {
            // R1：WebView2 是 HWND 子窗口，覆盖层盖不住它——必须先收起页面
            WebView.Visibility = Visibility.Collapsed;
            ShowLoading();
            CenterSubtitle.Text = "正在更新 Harness…";

            _server.Shutdown(); // 必须先停服：运行中的 node 进程持有 bin.js 文件句柄，npm 覆盖安装会 EPERM
            var updated = await _updater.UpdateAsync();
            var restarted = await _server.EnsureServerAsync();

            if (updated && restarted)
            {
                AppendLog("Harness 更新成功，重新加载页面");
                UpdateMenuItems();
                new Views.ConfirmDialog("更新完成",
                    $"Harness 已更新到 v{_updater.LocalVersion}。",
                    "好的", glyph: "✓") { Owner = this }.ShowDialog();
                WebView.CoreWebView2.Navigate($"http://127.0.0.1:{_server.Port}");
            }
            else
            {
                ShowError("更新失败", updated
                    ? "服务重启失败，请重试或查看日志。"
                    : $"Harness 更新失败，已尝试恢复服务。\n{_updater.LastError ?? "详情见日志"}；若服务异常请手动执行：\nnpm install -g @deepseek-ai/dsh@latest",
                    allowRetry: true);
            }
        }
        catch (Exception ex)
        {
            ShowError("更新失败", ex.Message, allowRetry: true);
        }
        finally
        {
            _updating = false;
            TitleBtnRestart.IsEnabled = true;
            UpdateMenuItems(); // 恢复菜单文案（"正在更新 Harness…" → 检查更新/更新可用）
        }
    }

    /// <summary>启动后后台自动检查一次（默认开，设置里可关）；只提示不自动安装。</summary>
    private async void MaybeAutoCheckUpdate()
    {
        if (_autoCheckRan || _updating || !AppSettings.Current.AutoCheckUpdate) return;
        _autoCheckRan = true;
        try
        {
            var ok = await _updater.CheckAsync();
            if (ok && _updater.HasUpdate)
            {
                AppendLog($"发现 Harness 新版本：v{_updater.LocalVersion} → v{_updater.LatestVersion}");
                UpdateMenuItems();
            }
        }
        catch (Exception ex)
        {
            // 后台检查失败不打扰用户（写日志即可）；防 async void 未处理异常
            AppendLog($"自动检查更新异常：{ex.Message}");
        }

        // 应用（壳自身）自动检查：独立防重入标志，与 Harness 互不干扰
        if (_appAutoCheckRan || _appUpdating || !AppSettings.Current.AutoCheckAppUpdate) return;
        _appAutoCheckRan = true;
        try
        {
            var ok = await _appUpdater.CheckAsync();
            if (ok && _appUpdater.HasUpdate)
            {
                AppendLog($"发现应用新版本：v{_appUpdater.LocalVersion} → v{_appUpdater.LatestVersion}");
                UpdateMenuItems();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"自动检查应用更新异常：{ex.Message}");
        }
    }

    // ---------------- 菜单（应用更新：壳自身，GitHub Releases） ----------------

    /// <summary>检查应用更新：顶栏菜单与托盘菜单共用入口。防重入；就绪态（已下载待安装）时点击直接走安装确认。</summary>
    private async Task CheckAppUpdateAsync()
    {
        if (_appChecking || _appUpdating) return;
        if (_appUpdateReady)
        {
            _appUpdateReady = false;
            UpdateMenuItems();
            ConfirmInstallAppUpdate();
            return;
        }

        _appChecking = true;
        UpdateMenuItems(); // "检查应用更新…"禁用态
        ShowCheckProgress("检查应用更新", "正在检查应用更新（GitHub Releases）…");
        try
        {
            // 手动点击一律重新检查（不复用启动时的陈旧结果）
            var ok = await _appUpdater.CheckAsync();
            HideCheckProgress(); // 检查完成：先关进度窗再弹结果
            if (!ok)
            {
                new Views.ConfirmDialog("检查应用更新",
                    $"检查更新失败：{_appUpdater.LastError ?? "未知原因"}。\n详情见日志。",
                    "知道了", glyph: "⚠") { Owner = this }.ShowDialog();
                return;
            }

            if (_appUpdater.HasUpdate)
            {
                var dlg = new Views.ConfirmDialog("发现新版本",
                    $"当前版本：v{_appUpdater.LocalVersion}\n最新版本：v{_appUpdater.LatestVersion}\n\n" +
                    "下载期间服务不受影响；下载完成后应用将自动重启完成更新，重启时服务短暂中断（约 10~30 秒）。是否继续？",
                    "下载更新", "暂不") { Owner = this };
                if (dlg.ShowDialog() == true)
                    await RunAppUpdateAsync();
            }
            else
            {
                new Views.ConfirmDialog("检查应用更新",
                    $"已是最新版本（v{_appUpdater.LocalVersion}）。",
                    "知道了", glyph: "ℹ") { Owner = this }.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            // fire-and-forget 入口：检查期间退出（AppUpdater.Dispose）会抛 ObjectDisposedException，必须兜住
            HideCheckProgress();
            AppendLog($"检查应用更新异常：{ex.Message}");
        }
        finally
        {
            _appChecking = false;
            UpdateMenuItems();
        }
    }

    /// <summary>安装确认弹窗（就绪态入口）：确认后走安装段。</summary>
    private void ConfirmInstallAppUpdate()
    {
        var dlg = new Views.ConfirmDialog("应用更新已就绪",
            $"已下载 v{_appUpdater.LatestVersion}。是否现在重启应用完成更新？\n重启时服务短暂中断（约 10~30 秒）。",
            "立即重启", "稍后") { Owner = this };
        if (dlg.ShowDialog() == true)
            InstallDownloadedAppUpdate();
    }

    /// <summary>
    /// 应用更新完整流程：R1 收起页面 → 覆盖层下载（进度 + 日志 + 取消按钮）→ 校验 → 安装段。
    /// 下载不打断服务；托盘化（Hide）期间下载继续，完成置 _appUpdateReady（恢复窗口时确认）。
    /// </summary>
    private async Task RunAppUpdateAsync()
    {
        if (_appUpdating) return;
        _appUpdating = true;
        TitleBtnRestart.IsEnabled = false;
        _appDownloadCts = new CancellationTokenSource();
        UpdateMenuItems();
        try
        {
            // R1：WebView2 是 HWND 子窗口，覆盖层盖不住它——必须先收起页面
            WebView.Visibility = Visibility.Collapsed;
            ShowLoading();
            CenterSubtitle.Text = "正在下载应用更新…";
            ShowCancelDownloadButton();

            var ok = await _appUpdater.DownloadAndVerifyAsync(OnAppDownloadProgress, _appDownloadCts.Token);
            HideCancelDownloadButton();
            if (!ok)
            {
                if (_appDownloadCts.IsCancellationRequested)
                {
                    AppendLog("应用更新下载已取消");
                    RestoreAfterCancelledDownload();
                    return;
                }
                ShowError("更新失败",
                    $"下载或校验失败：{_appUpdater.LastError ?? "详情见日志"}。\n服务未受影响，可稍后重试。",
                    allowRetry: false);
                return;
            }

            // 下载完成：窗口可见 → 立即安装确认；窗口隐藏（托盘化）→ 气泡 + 待安装状态
            if (!IsVisible)
            {
                _appUpdateReady = true;
                UpdateMenuItems();
                try
                {
                    TrayIcon.ShowBalloonTip("DeepSeek Harness",
                        $"应用更新已下载完成（v{_appUpdater.LatestVersion}），恢复窗口后安装。",
                        Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                }
                catch (Exception ex)
                {
                    AppendLog($"托盘提示失败: {ex.Message}");
                }
                return;
            }

            InstallDownloadedAppUpdate();
        }
        catch (Exception ex)
        {
            ShowError("更新失败", ex.Message, allowRetry: false);
        }
        finally
        {
            // 仅未进入重启路径时复位状态（进入重启路径后 _quitForAppUpdate=true，走 OnClosing 放行退出）
            if (!_quitForAppUpdate)
            {
                _appUpdating = false;
                TitleBtnRestart.IsEnabled = true;
                UpdateMenuItems();
            }
        }
    }

    /// <summary>安装段：生成更新器脚本 → 启动脚本（不等待）→ 放行退出。更新器在旧实例退出后覆盖 exe 并重启。</summary>
    private void InstallDownloadedAppUpdate()
    {
        _appUpdating = true;
        TitleBtnRestart.IsEnabled = false;
        _appDownloadCts = null;
        UpdateMenuItems();
        try
        {
            if (WebView.Visibility == Visibility.Visible)
            {
                // R1：收起页面（覆盖层可见）
                WebView.Visibility = Visibility.Collapsed;
            }
            ShowLoading();
            CenterSubtitle.Text = "正在准备更新…";

            var scriptPath = _appUpdater.WriteUpdateScript(_server.Port);
            if (scriptPath is null)
            {
                ShowError("更新失败", _appUpdater.LastError ?? "无法生成更新脚本。", allowRetry: false);
                return;
            }

            // 启动更新器（隐藏窗口，不等待；ArgumentList 传参免引号转义问题）
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            Process.Start(psi);

            AppendLog("更新器已启动，应用即将重启完成更新");
            _quitForAppUpdate = true; // OnClosing 放行真退出（托盘化分支前短路）
            Close();
        }
        catch (Exception ex)
        {
            // 更新器启动失败：不退出应用，恢复 UI 与状态（服务不受影响）
            AppendLog($"启动更新器失败：{ex.Message}");
            ShowError("更新失败", $"无法启动更新器：{ex.Message}\n服务未受影响，可稍后重试。", allowRetry: false);
        }
        finally
        {
            if (!_quitForAppUpdate)
            {
                _appUpdating = false;
                TitleBtnRestart.IsEnabled = true;
                UpdateMenuItems();
            }
        }
    }

    /// <summary>下载进度回调（AppUpdater 已节流 250ms）：更新覆盖层副标题，不阻塞下载线程。</summary>
    private void OnAppDownloadProgress(long read, long total)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!IsVisible) return;
            CenterSubtitle.Text = total > 0
                ? $"正在下载应用更新… {read / 1048576.0:F1} / {total / 1048576.0:F0} MB（{read * 100 / total}%）"
                : $"正在下载应用更新… {read / 1048576.0:F1} MB";
        });
    }

    /// <summary>取消下载后恢复：隐藏覆盖层、恢复页面、菜单复位（状态复位在调用方 finally）。</summary>
    private void RestoreAfterCancelledDownload()
    {
        HideOverlay();
        WebView.Visibility = Visibility.Visible;
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _appDownloadCts?.Cancel();
        CancelDownloadButton.IsEnabled = false;
        CenterSubtitle.Text = "正在取消下载…";
    }

    private void ShowCancelDownloadButton()
    {
        CancelDownloadButton.Visibility = Visibility.Visible;
        CancelDownloadButton.IsEnabled = true;
    }

    private void HideCancelDownloadButton()
    {
        CancelDownloadButton.Visibility = Visibility.Collapsed;
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
        TopMenu.ItemsSource = items;
        UpdateTrayMenuItems(); // 托盘更新项与顶栏联动
    }

    // ---------------- 顶栏余额显示 ----------------

    /// <summary>页面加载完成后启动余额监控（仅设置开启时；幂等）。</summary>
    private void StartBalanceIfEnabled()
    {
        if (!_balanceRunning && AppSettings.Current.ShowBalance)
        {
            _balanceRunning = true;
            TitleBalance.Visibility = Visibility.Visible;
            TitleBalanceText.Text = "…";
            _balance.Start();
        }
    }

    /// <summary>设置窗关闭后同步余额开关状态（开 → 启动并立即刷新；关 → 停止并隐藏）。</summary>
    private void SyncBalanceFromSettings()
    {
        if (AppSettings.Current.ShowBalance)
        {
            StartBalanceIfEnabled();
            // 设置可能改了授权/手动 Key：立即刷新一次，撤销授权后顶栏不残留旧值
            _ = _balance.RefreshAsync();
        }
        else if (_balanceRunning)
        {
            _balanceRunning = false;
            _balance.Stop();
            TitleBalance.Visibility = Visibility.Collapsed;
            TitleBalanceText.Text = "";
        }
    }

    private void OnBalanceChanged(string? text)
    {
        // 托盘悬停同步余额（窗口隐藏时也能看到）
        TrayIcon.ToolTipText = text is null ? "DeepSeek Harness" : $"DeepSeek Harness — 余额 ¥{text}";

        if (text is null)
        {
            TitleBalanceText.Text = "—";
            // 失败/无值：显式弱色（SetResourceReference = DynamicResource 语义，主题切换即时跟随）
            TitleBalanceText.SetResourceReference(TextBlock.ForegroundProperty, "TextWeakBrush");
            TitleBalance.ToolTip = _balance.LastError is null
                ? "余额获取失败（点击重试）"
                : $"余额获取失败：{_balance.LastError}（点击重试）";
            if (_userRefreshPending)
            {
                _userRefreshPending = false;
                ShowBalanceStatus($"✗ 刷新失败：{_balance.LastError ?? "未知原因"}",
                    (Brush)FindResource("AccentRedBrush"));
            }
        }
        else
        {
            TitleBalanceText.Text = $"¥ {text}";
            TitleBalance.ToolTip = _balance.DetailText + "\n左键菜单：刷新 / 充值";
            // 余额状态色（规范见 docs/UI-GUIDELINES.md §4.1）：常驻公共蓝；<¥5 告警黄；<¥2 危险红。
            // SetResourceReference = DynamicResource：主题切换即时跟随（R8），不残留旧主题色
            if (_balance.LastBalance is decimal amount)
            {
                TitleBalanceText.SetResourceReference(TextBlock.ForegroundProperty,
                    amount < BalanceDangerThreshold ? "AccentRedBrush"
                    : amount < BalanceWarnThreshold ? "BalanceAlertBrush"
                    : "AccentBlueBrush");
            }
            if (_userRefreshPending)
            {
                _userRefreshPending = false;
                ShowBalanceStatus($"✓ 余额已更新：¥ {text}",
                    (Brush)FindResource("AccentGreenBrush"));
            }
        }
    }

    /// <summary>
    /// 余额告警：仅"跨越阈值"时提醒一次（上次数值高于阈值、本次低于阈值；恢复后复位），
    /// 避免每 60s 轮询反复轰炸。提醒双通道：窗口状态卡（可见时）+ 托盘气泡（点击跳充值页）。
    /// </summary>
    private void OnBalanceAmountChanged(decimal? amount)
    {
        if (amount is null || !AppSettings.Current.BalanceAlertEnabled)
            return;

        var threshold = AppSettings.Current.BalanceAlertThreshold;
        if (amount >= threshold)
        {
            _wasAboveThreshold = true; // 恢复：下次再跌破才提醒
            return;
        }
        if (!_wasAboveThreshold)
            return;

        _wasAboveThreshold = false;
        AppendLog($"余额告警：¥{amount:0.00} 低于阈值 ¥{threshold:0.00}");
        // 窗口隐藏（托盘化/最小化）时不弹状态卡（孤悬屏幕），只走托盘气泡
        if (IsVisible)
            ShowBalanceStatus($"⚠ 余额不足：¥{amount:0.00}（低于 ¥{threshold:0.00}）",
                (Brush)FindResource("AccentOrangeBrush"), stayMs: 4000);
        try
        {
            TrayIcon.ShowBalloonTip("DeepSeek 余额不足",
                $"当前余额 ¥{amount:0.00}，低于阈值 ¥{threshold:0.00}。\n点击此通知前往充值。",
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning);
        }
        catch (Exception ex)
        {
            AppendLog($"余额告警气泡失败: {ex.Message}");
        }
    }

    /// <summary>右键余额交互已删除（v1.2.1）：左键菜单内含刷新/充值；此注释占位防误加回。</summary>

    /// <summary>在余额按钮下方显示状态卡（自动关闭；序号防连续点击竞态；水平超屏左移钳位）。</summary>
    private async void ShowBalanceStatus(string text, Brush? brush = null, int stayMs = 2200)
    {
        var seq = ++_statusSeq;
        BalanceStatusText.Text = text;
        BalanceStatusText.Foreground = brush ?? (Brush)FindResource("TextSecondaryBrush");
        // 与余额菜单同锚点：先关菜单防叠显；水平偏移复位（上次钳位可能留负值）
        BalanceMenuPopup.IsOpen = false;
        BalanceStatusPopup.HorizontalOffset = 0;
        BalanceStatusPopup.IsOpen = true;
        // 水平钳位：状态卡右缘超工作区则左移（余额按钮右侧紧邻系统按钮区，文本可能溢出）
        if (BalanceStatusPopup.Child is FrameworkElement child)
        {
            try
            {
                var tl = child.PointToScreen(new Point(0, 0));
                var dpi = VisualTreeHelper.GetDpi(child).PixelsPerDip;
                var right = tl.X + child.ActualWidth * dpi;
                var workRight = SystemParameters.WorkArea.Right * dpi; // 主屏近似（状态卡短生命周期，可接受）
                if (right > workRight)
                {
                    BalanceStatusPopup.HorizontalOffset = -((right - workRight) / dpi) - 4;
                }
            }
            catch { /* 钳位失败不影响显示 */ }
        }
        await Task.Delay(stayMs);
        if (seq == _statusSeq)
        {
            // 自动消失播轻量淡出（Popup 关闭即销毁，必须先动画后关）
            if (BalanceStatusPopup.Child is FrameworkElement fadeChild)
                PopupAnimator.PlayClose(fadeChild, () => BalanceStatusPopup.IsOpen = false, PopupAnimator.LightClose);
            else
                BalanceStatusPopup.IsOpen = false;
        }
    }

    /// <summary>按下反馈：余额按钮内容轻微缩小（配合 Click 的弹性弹回，构成点击动效）。</summary>
    private void TitleBalance_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var scale = new ScaleTransform(0.94, 0.94);
        TitleBalance.RenderTransform = scale;
        var shrink = new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(80));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>弹起复位：按下后拖出按钮再松开不触发 Click，若不复位会永久卡在 0.94 缩放。</summary>
    private void TitleBalance_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (TitleBalance.RenderTransform is ScaleTransform scale)
        {
            var pop = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    private void TitleBalance_Click(object sender, RoutedEventArgs e)
    {
        if (TitleBalance.Visibility != Visibility.Visible) return;
        // 弹回动效已在 PreviewMouseUp 统一处理（含按下拖出场景）；此处只做菜单开关
        if (BalanceMenuPopup.IsOpen)
            AnimateMenuClose(BalanceMenuPopup, BalanceMenu, true);
        else
            BalanceMenuPopup.IsOpen = true;
    }

    private void TitleBtnMin_Click(object sender, RoutedEventArgs e)
    {
        CloseAllMenus(); // 最小化时菜单 Popup（独立 HWND）与钩子一并清理
        WindowState = WindowState.Minimized;
    }

    private void TitleBtnMax_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>最大化 ↔ 还原图标切换。</summary>
    private void UpdateMaxButtonGlyph()
    {
        TitleBtnMax.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        TitleBtnMax.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        // 最大化时无需 resize 热区（贴边），普通状态四周留 5px 让出热区
        var m = WindowState == WindowState.Maximized ? 0.0 : 5.0;
        ContentRoot.Margin = new Thickness(m, 0, m, m);
    }

    private void TitleBtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", FileLog.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"打开日志失败: {ex.Message}");
        }
    }

    /// <summary>启动失败详情：优先展示安装指引（环境缺失时），否则回退日志摘要。</summary>
    private string StartupFailureDetail()
    {
        if (!string.IsNullOrWhiteSpace(_server.StartupErrorHint))
            return _server.StartupErrorHint;
        var tail = FileLog.TailText.TrimEnd();
        return string.IsNullOrWhiteSpace(tail)
            ? "请查看日志确认 dsh 是否安装、端口是否被占用。"
            : tail;
    }

    // ---------------- 日志 ----------------

    /// <summary>覆盖层日志刷新 pending 标志（合并多次 AppendLog 为一次重设，避免每行重排）。</summary>
    private bool _overlayLogDirty;

    private void AppendLog(string message)
    {
        FileLog.Append(message); // 内存尾缓冲 + 后台异步落盘（UI 线程零磁盘 IO）

        // 覆盖层可见时同步展示最新日志并滚到底（节流：合并到 Background 优先级）
        if (Overlay.Visibility == Visibility.Visible)
            ScheduleOverlayLogRefresh();
    }

    private void ScheduleOverlayLogRefresh()
    {
        if (_overlayLogDirty) return;
        _overlayLogDirty = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _overlayLogDirty = false;
            if (Overlay.Visibility != Visibility.Visible) return;
            OverlayDetail.Text = FileLog.TailText.TrimEnd();
            OverlayLogScroller.ScrollToEnd();
        });
    }

    // ---------------- 生命周期 ----------------

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _heartbeat.Stop();
        WindowPlacementStore.Save(this);

        // 更新进行中：拦截关窗，经用户确认才中断安装（半安装比跑完更危险，设计文档 §8）
        if (_updating && !_abortUpdateConfirmed)
        {
            e.Cancel = true;
            var dlg = new Views.ConfirmDialog("更新正在进行",
                "Harness 更新正在进行，关闭窗口将中断安装，可能导致 Harness 包不完整。\n\n确定要中断更新并关闭吗？",
                "中断并关闭", "继续更新", okDanger: true) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _abortUpdateConfirmed = true;
                _updater.AbortRunningNpm();
                AppendLog("用户确认中断 Harness 更新，已终止 npm 安装进程");
                Close();
            }
            return;
        }

        // 未处理异常兜底：App 已请求强制退出，任何拦截都必须放行
        if (App.ForceExit)
        {
            CloseAllMenus();
            return;
        }

        // 应用自更新重启放行：更新器已启动，必须真退出
        // （否则默认"最小化到托盘"会 e.Cancel+Hide 拦截 Close，更新器等不到进程退出 60s 超时放弃覆盖——静默失败）
        if (_quitForAppUpdate)
        {
            CloseAllMenus();
            return;
        }

        // 最小化到托盘：隐藏窗口，服务继续运行（托盘菜单"退出"、设置关闭该行为、
        // 更新中断确认后（_abortUpdateConfirmed）或托盘不可用（_trayAvailable=false）才真正退出）
        if (AppSettings.Current.MinimizeToTrayOnClose && !_trayExitRequested && !_abortUpdateConfirmed && _trayAvailable)
        {
            // 托盘化不取消应用下载（后台继续，完成置 _appUpdateReady，恢复窗口时确认）
            e.Cancel = true;
            CloseAllMenus(); // Popup 是独立 HWND：不关则残留屏幕，钩子也保持常驻
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                try
                {
                    TrayIcon.ShowBalloonTip("DeepSeek Harness",
                        "已最小化到托盘，服务继续运行。\n双击托盘图标恢复窗口，右键可退出。",
                        Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                }
                catch (Exception ex)
                {
                    AppendLog($"托盘提示失败: {ex.Message}");
                }
            }
            AppendLog("窗口最小化到托盘（服务继续运行）");
        }

        // 走到这里 = 真退出（托盘"退出" / 关窗直退 / 设置关闭托盘化）：取消进行中的应用下载
        // （下载无副作用：只写 update 临时文件，取消后由下次启动清理兜底）
        if (_appUpdating && _appDownloadCts is not null)
        {
            _appDownloadCts.Cancel();
            AppendLog("应用更新下载已取消（应用退出）");
        }
    }

    /// <summary>关闭全部菜单 Popup 与余额状态卡（隐藏/退出/强制退出时调用，触发 Closed 自然卸钩）。</summary>
    private void CloseAllMenus()
    {
        AnimateMenuClose(MenuPopup, TopMenu, false);
        AnimateMenuClose(BalanceMenuPopup, BalanceMenu, false);
        AnimateMenuClose(TrayMenuPopup, TrayMenu, false);
        BalanceStatusPopup.IsOpen = false;
        UninstallDismissHookIfIdle();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        HideCheckProgress(); // 主窗口关闭：进度窗一并关闭，防残留
        _server.Shutdown();
        _server.Dispose();
        _balance.Stop();
        _balance.Dispose();
        _updater.Dispose();
        _appUpdater.Dispose();
        App.ActiveServer = null;
        try
        {
            TrayIcon.Dispose(); // Hardcodet 资源（原生图标/隐藏窗口）随窗口显式释放
        }
        catch { /* 忽略 */ }
    }

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
        ThemeManager.ThemeChanged += _ => ApplyDwmTheme();

        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // 无边框窗口最大化钳制（WM_GETMINMAXINFO）：修正最大化时顶部溢出、底部探入任务栏
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc);

        WindowPlacementStore.Restore(this);
    }

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
