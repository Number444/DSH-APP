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
    /// <summary>菜单"退出APP（保留服务）"：退出壳但不停止后台 dsh 服务（OnClosed 跳过 Shutdown，
    /// 仅 Dispose 释放进程句柄；服务成孤儿，下次启动按既有探测逻辑重新接管）。</summary>
    private bool _exitKeepServer;
    /// <summary>首次隐藏到托盘时是否已提示。</summary>
    private bool _trayHintShown;
    /// <summary>余额是否高于告警阈值（初始视为高：首次刷新低于阈值即告警；恢复后复位）。</summary>
    private bool _wasAboveThreshold = true;
    /// <summary>余额状态色阈值（固定）：低于此值变黄（告警）。</summary>
    private const decimal BalanceWarnThreshold = 5m;
    /// <summary>余额状态色阈值（固定）：低于此值变红（危险）。</summary>
    private const decimal BalanceDangerThreshold = 2m;
    /// <summary>Kimi 额度状态色阈值（已用百分比，取 5h/7d 两窗口较高值）：达到此值变黄（告警）。</summary>
    private const decimal KimiWarnPercent = 80m;
    /// <summary>Kimi 额度状态色阈值（已用百分比）：达到此值变红（危险）。</summary>
    private const decimal KimiDangerPercent = 90m;
    /// <summary>用户点击余额触发的刷新待结果（仅此场景弹状态窗；后台轮询不弹）。</summary>
    private bool _userRefreshPending;
    /// <summary>状态窗序号：防快速连续点击时旧延迟关闭误关新弹窗。</summary>
    private int _statusSeq;
    /// <summary>托盘悬停文案的余额段（最近成功值；失败置 null 不残留旧值）。</summary>
    private string? _trayBalanceText;
    /// <summary>托盘悬停文案的服务状态段（启动中 / 运行中 / 已断开）。</summary>
    private string _serviceStateText = "启动中";

    // ---- 菜单"点击外部关闭"（低级鼠标钩子） ----
    // WebView2 是原生 HWND：点击页面区域不进入 WPF 输入路由，Popup 的 StaysOpen=False
    // 检测不到"外部点击"（托盘场景 Popup 捕获还可能被托盘交互抢占）→ 用 WH_MOUSE_LL 全局兜底。
    private IntPtr _mouseHook;
    private LowLevelMouseProc? _mouseHookProc;
    /// <summary>托盘菜单打开时记录的托盘图标位置（物理像素；作锚点命中判定）。</summary>
    private Point? _trayIconScreenPos;
    /// <summary>托盘图标是否挂载成功（失败时关窗不得走"最小化到托盘"）。</summary>
    private bool _trayAvailable;
    /// <summary>托盘菜单打开动画的抛出起点（相对菜单位置；由 OpenTrayMenu 按光标方向计算）。</summary>
    private Point _trayFlyFrom;

    public MainWindow()
    {
        InitializeComponent();
        VersionFooter.Text = $"dsh-app v{App.AppVersion} · DeepSeek Harness 桌面壳";

        // 事件订阅统一走 DispatchUi（BeginInvoke 异步派发）：npm install 输出洪峰时
        // 不再阻塞管道读取线程等 UI；同优先级 FIFO 保证日志行顺序。
        // 注意：步骤状态与日志的相对先后可能错开（冒烟时重点观察覆盖层步骤行与日志区）。
        _server.Log += msg => DispatchUi(() => AppendLog(msg));
        _server.StepChanged += (step, status, detail) => DispatchUi(() => UpdateStep(step, status, detail));
        _server.ServerDied += () => DispatchUi(OnServerDied);

        // Harness 更新器：日志走同一通道
        _updater.Log += msg => DispatchUi(() => AppendLog(msg));

        // 应用（壳自身）更新器：日志走同一通道
        _appUpdater.Log += msg => DispatchUi(() => AppendLog(msg));

        // 余额监控：回调经 Dispatcher 上 UI（R4 线程铁律）
        _balance.BalanceChanged += text => DispatchUi(() => OnBalanceChanged(text));
        _balance.BalanceAmountChanged += amount => DispatchUi(() => OnBalanceAmountChanged(amount));
        _balance.RefreshFailed += err => DispatchUi(() => OnBalanceRefreshFailed(err));

        // 会话完成通知：直连服务事件流（托盘化/页面挂起期间照常工作），回调同样经 Dispatcher 上 UI
        _completionNotify.Log += msg => DispatchUi(() => AppendLog(msg));
        _completionNotify.SessionFinished += (id, err) => DispatchUi(() => OnSessionFinished(id, err));

        // 顶栏菜单：公共菜单控件 + 数据驱动菜单项（更新状态高亮由 UpdateMenuItems 维护）
        TopMenu.ItemClicked += OnTopMenuClicked;
        UpdateMenuItems();

        // 余额左键菜单（刷新 / 充值或用量页；文案按显示来源动态重建，见 RefreshBalanceMenuItems）
        BalanceMenu.ItemClicked += OnTopMenuClicked;
        RefreshBalanceMenuItems();

        // 托盘菜单：与 APP 内菜单同一公共控件（Popup 定位，弃用 ContextMenu）；内容由 UpdateMenuItems 联动
        TrayMenu.ItemClicked += OnTopMenuClicked;

        // 菜单键盘可达：焦点在 Popup 内时 Esc 经面板 DismissRequested 关闭（Popup 独立 HWND，
        // 窗口级 PreviewKeyDown 收不到）；焦点在主窗口时走 OnWindowPreviewKeyDown 兜底
        TopMenu.DismissRequested += () => AnimateMenuClose(MenuPopup, TopMenu, true);
        BalanceMenu.DismissRequested += () => AnimateMenuClose(BalanceMenuPopup, BalanceMenu, true);
        TrayMenu.DismissRequested += () => AnimateMenuClose(TrayMenuPopup, TrayMenu, true);
        PreviewKeyDown += OnWindowPreviewKeyDown;

        // 菜单"点击外部关闭"：三个 Popup 全部接入（打开时装钩子，关闭时自动卸载）
        // 打开动画经 Opened 统一驱动：顶栏/余额从按钮上方抛出（(0,-24)），托盘按光标方向
        MenuPopup.Opened += (_, _) => { InstallDismissHook(); TopMenu.PlayOpenAnimation(new Point(0, -24)); TopMenu.FocusFirstItem(); };
        MenuPopup.Closed += (_, _) => UninstallDismissHookIfIdle();
        BalanceMenuPopup.Opened += (_, _) => { InstallDismissHook(); RefreshBalanceMenuItems(); BalanceMenu.PlayOpenAnimation(new Point(0, -24)); BalanceMenu.FocusFirstItem(); };
        BalanceMenuPopup.Closed += (_, _) => UninstallDismissHookIfIdle();
        TrayMenuPopup.Opened += (_, _) => { InstallDismissHook(); TrayMenu.PlayOpenAnimation(_trayFlyFrom); TrayMenu.FocusFirstItem(); };
        TrayMenuPopup.Closed += (_, _) => UninstallDismissHookIfIdle();
        // 余额状态卡：轻量档打开动画（淡入 + 轻放大，无位移无弹性）；StaysOpen=True，
        // 点击外部经全局钩子动画淡出（与菜单同一钩子），关闭后按空闲卸载钩子
        BalanceStatusPopup.Opened += (_, _) =>
        {
            InstallDismissHook();
            if (BalanceStatusPopup.Child is FrameworkElement child)
                PopupAnimator.PlayOpen(child, options: PopupAnimator.LightOpen);
        };
        BalanceStatusPopup.Closed += (_, _) => UninstallDismissHookIfIdle();

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

        // 进度条彗尾光柱群 + 条宽变化即时同步（窗口尺寸/DPI 变化）；
        // 彗尾锚点按填充宽比例分布：填充宽动画期间逐帧重分布（光柱随条平滑右移）
        BuildProgressTail();
        OverlayProgress.SizeChanged += (_, _) => OnProgressTrackSizeChanged();
        ProgressFill.SizeChanged += (_, _) => RedistributeTail();

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

            // 完成通知监听不依赖页面加载：服务就绪即启动（WebView2 挂起/崩溃期间照样通知）
            StartCompletionNotifyIfEnabled();

            WebView.CoreWebView2.Navigate($"http://127.0.0.1:{_server.Port}");
        }
        catch (Exception ex)
        {
            if (IsWebView2RuntimeMissing(ex))
                // Runtime 缺失（Win10/精简系统）：给专用引导卡而非笼统的"初始化失败"
                ShowError("缺少 WebView2 Runtime",
                    "当前系统没有安装 WebView2 Runtime，界面无法显示。\n点下方按钮下载官方安装包，装好后点「重试」，不用重启应用。",
                    allowRetry: true, showRuntimeDownload: true);
            else
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
        // 页面 target=_blank 外链：壳内无多窗口概念，接管抛系统浏览器
        wv.NewWindowRequested += OnNewWindowRequested;
        // 页面权限请求：WebView2 无浏览器式询问 UI，未处理的请求一律静默拒绝——
        // 对本机 Harness 源放行通知权限（浏览器端通知插件可用），其余权限不触碰（默认拒绝）
        wv.PermissionRequested += OnPermissionRequested;
        // 旧版本未处理权限事件时"拒绝"会被持久化进 profile，持久化记录会短路事件本身
        // （handler 永远收不到请求、permission 恒为 denied）——启动时复位本机端口段的历史记录
        _ = ResetStaleNotificationPermissionsAsync(wv);
        // 页面 → 壳通知桥：浏览器端插件经 chrome.webview.postMessage 上报，壳弹系统样式 toast
        wv.WebMessageReceived += OnWebMessageReceived;

        wv.NavigationCompleted += OnNavigationCompleted;
        wv.ProcessFailed += OnProcessFailed;

        ApplyZoomFromSettings();

        AppendLog("WebView2 就绪");
    }

    /// <summary>页面新开窗口请求（target=_blank 等）：一律抛系统浏览器，仅放行 http/https。</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"打开外链失败: {ex.Message}");
        }
    }

    /// <summary>
    /// WebView2 权限请求：宿主不处理则一律静默拒绝（无浏览器式询问 UI）。
    /// 仅对本机 Harness 源（loopback + 当前服务端口）放行通知权限，其余权限/来源不触碰
    /// （State 保持 Default = 拒绝，fail-closed）；授权持久化于壳的独立 profile。
    /// </summary>
    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind != CoreWebView2PermissionKind.Notifications) return;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.IsLoopback && uri.Port == _server.Port)
            e.State = CoreWebView2PermissionState.Allow;
    }

    /// <summary>
    /// 复位历史遗留的通知权限记录：旧版本未处理 PermissionRequested 时，WebView2 会把"拒绝"
    /// 持久化进 profile，而持久化记录会短路权限请求事件（handler 收不到、permission 恒为 denied）。
    /// 启动时把本机 Harness 探测端口段（3080~3090，同 ServerController 扫描范围）loopback 源的
    /// 通知权限复位为 Default（= 擦除持久化记录），让 OnPermissionRequested 重新接管。
    /// 尽力而为（异步不等待），失败仅记日志。
    /// </summary>
    private async Task ResetStaleNotificationPermissionsAsync(CoreWebView2 wv)
    {
        try
        {
            var settings = await wv.Profile.GetNonDefaultPermissionSettingsAsync();
            foreach (var s in settings)
            {
                if (s.PermissionKind != CoreWebView2PermissionKind.Notifications) continue;
                if (!Uri.TryCreate(s.PermissionOrigin, UriKind.Absolute, out var uri)) continue;
                if (!uri.IsLoopback || uri.Port is < 3080 or > 3090) continue;
                await wv.Profile.SetPermissionStateAsync(
                    CoreWebView2PermissionKind.Notifications, s.PermissionOrigin,
                    CoreWebView2PermissionState.Default);
                AppendLog($"已复位历史通知权限记录：{s.PermissionOrigin}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"复位历史通知权限记录失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 页面 → 壳消息桥：浏览器端插件经 chrome.webview.postMessage 上报通知请求，
    /// 壳用托盘气球弹系统 toast（WebView2 自绘通知非 Win11 样式；气球通道由系统渲染且不依赖页面通知权限）。
    /// 仅接受本机 Harness 源（loopback + 当前服务端口）的消息；格式 {type:"dsh-notify", title, body}。
    /// </summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var src)
                || !src.IsLoopback || src.Port != _server.Port)
                return;
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "dsh-notify") return;
            static string Clip(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];
            var title = Clip(root.TryGetProperty("title", out var t) ? t.GetString() : null, 80);
            var body = Clip(root.TryGetProperty("body", out var b) ? b.GetString() : null, 200);
            if (title.Length == 0 && body.Length == 0) return;
            _balloonKind = BalloonKind.Completion; // 插件过渡期的完成通知同属此类（点击恢复窗口）
            TrayIcon.ShowBalloonTip(title, body, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
        }
        catch (Exception ex)
        {
            AppendLog($"页面通知桥处理失败: {ex.Message}");
        }
    }

    /// <summary>应用设置中的界面缩放（100/125/150% → ZoomFactor；初始化与设置窗关闭后各调一次）。</summary>
    private void ApplyZoomFromSettings()
    {
        try
        {
            WebView.ZoomFactor = Math.Clamp(AppSettings.Current.ZoomPercent, 50, 300) / 100.0;
        }
        catch
        {
            // WebView 未就绪等瞬时态：忽略，下次设置窗口关闭时再应用
        }
    }

    /// <summary>
    /// 托盘化页面挂起：窗口隐藏到托盘时挂起渲染进程（省电省 CPU），恢复窗口时唤醒。
    /// 页面活动（音频等）可能拒绝挂起——尽力而为，失败仅记日志；真退出路径无需 Resume（进程即毁）。
    /// </summary>
    private async void TrySetWebViewSuspended(bool suspend)
    {
        try
        {
            var wv = WebView.CoreWebView2;
            if (wv is null) return;
            if (suspend)
            {
                if (wv.IsSuspended) return;
                if (!await wv.TrySuspendAsync())
                    AppendLog("页面挂起被页面活动拒绝（忽略，继续运行）");
            }
            else if (wv.IsSuspended)
            {
                wv.Resume();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"页面挂起/恢复异常: {ex.Message}");
        }
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
            _serviceStateText = "运行中";
            UpdateTrayToolTip();
            _rendererReloadTried = false;
            StartHeartbeatIfAdopted();
            StartBalanceIfEnabled();
            MaybeAutoCheckUpdate();
        }
        else
        {
            AppendLog($"页面加载失败: {e.WebErrorStatus}");
            var errText = WebErrorText(e.WebErrorStatus);
            UpdateStep(ServerStep.Load, StepStatus.Failed, errText);
            ShowError("页面加载失败", $"WebView2 {errText}。\n服务器可能已停止。", allowRetry: true);
        }
    }

    /// <summary>WebView2 导航错误码 → 中文短语（错误卡/步骤行不直显英文枚举）。</summary>
    private static string WebErrorText(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.CannotConnect => "无法连接服务（可能未启动）",
        CoreWebView2WebErrorStatus.ConnectionAborted => "连接被中断",
        CoreWebView2WebErrorStatus.ConnectionReset => "连接被重置",
        CoreWebView2WebErrorStatus.HostNameNotResolved => "地址解析失败",
        CoreWebView2WebErrorStatus.OperationCanceled => "操作已取消",
        CoreWebView2WebErrorStatus.Timeout => "连接超时",
        CoreWebView2WebErrorStatus.CertificateIsInvalid => "证书校验失败",
        CoreWebView2WebErrorStatus.Disconnected or CoreWebView2WebErrorStatus.ServerUnreachable
            => "服务不可达（可能已停止）",
        _ => $"网络错误（{status}）",
    };

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                Dispatcher.Invoke(() =>
                {
                    _rendererReloadTried = false; // 整个浏览器进程将重建，复位渲染崩溃自动刷新的一次性标志
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
        _serviceStateText = "已断开";
        UpdateTrayToolTip();
        ShowError("服务连接已断开", detail + "\n点「重试」重新拉起服务。", allowRetry: true);
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
        _serviceStateText = "启动中";
        UpdateTrayToolTip();
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
        // 入场动画：开头 0.3s 纯空白静待，品牌区上方抛入，进度卡片再压后 250ms 下方抛入（R2 主次有序）
        PopupAnimator.PlayOpen(CenterBrand, new Point(0, -24), Entrance);
        PopupAnimator.PlayOpen(ProgressCard, new Point(0, 24), EntranceDelayed);
    }

    /// <summary>启动页入场档：菜单同款抛出回弹，但时长拉到 1.2s——启动头 0.5~1s WebView2
    /// 初始化有卡顿，480ms 菜单档会整段埋在卡顿里（等于没播）；拉长后才真正可见。
    /// BeginMs 300：开头先留 0.3s 纯空白（静待），再抛入——盖住启动卡顿最重的头段。
    /// StartScale 收到 0.85：大块品牌区慢速从 0.5 放大观感是"变焦"而非"抛出"；
    /// 模糊半径/时长同步收小拉长，避免大面积长时逐帧模糊加重启动期渲染压力。</summary>
    private static readonly PopupAnimator.OpenOptions Entrance = new()
    { DurationMs = 1200, FadeMs = 450, BlurRadius = 14, BlurMs = 800, StartScale = 0.85, BeginMs = 300 };

    /// <summary>进度卡片入场档：同 Entrance + 再压后 250ms（迟于品牌区，主次有序；全程约 1.75s）。</summary>
    private static readonly PopupAnimator.OpenOptions EntranceDelayed = new()
    { DurationMs = 1200, FadeMs = 450, BlurRadius = 14, BlurMs = 800, StartScale = 0.85, BeginMs = 550 };

    /// <summary>重置全部步骤为待执行（重试/重新启动时调用）。</summary>
    private void ResetSteps()
    {
        foreach (var row in _steps)
            row.Update(StepStatus.Pending, "");
        SetProgress(0, smooth: false); // 进度条同步归零
    }

    /// <summary>更新某一步骤的状态（来自 ServerController 事件或本窗口）。</summary>
    private void UpdateStep(ServerStep step, StepStatus status, string detail)
    {
        var row = _steps.FirstOrDefault(r => r.Step == step);
        if (row is not null)
        {
            row.Update(status, detail);
            // 进度条真实驱动：完成 N 步 → N/总步数（400ms 平滑推移，与步骤行点亮同步）
            SetProgress(_steps.Count(r => r.Status == StepStatus.Done) * 100.0 / _steps.Count);
        }
    }

    /// <summary>
    /// 异步派发到 UI 线程（BeginInvoke，不阻塞来源线程）。
    /// 关闭期派发异常就地吞掉（与 BalanceMonitor.Dispatch 同一惯例）：
    /// Dispatcher 关闭后 BeginInvoke/回调执行都可能抛，静默忽略即可。
    /// </summary>
    private void DispatchUi(Action action)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // 关闭期回调异常属正常（不击穿全局异常兜底）；非关闭期记日志，防编程错误被静默吞掉
                    if (!Dispatcher.HasShutdownStarted)
                        AppendLog($"UI 派发回调异常: {ex.Message}");
                }
            });
        }
        catch
        {
            // Dispatcher 已关闭：忽略
        }
    }

    private void ShowError(string title, string detail, bool allowRetry = false, bool showRuntimeDownload = false)
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
        RuntimeButton.Visibility = showRuntimeDownload ? Visibility.Visible : Visibility.Collapsed;
        // 错误区入场（同启动页慢入场档，上方抛入；进度卡片不重播——它可能早已静止在场）
        PopupAnimator.PlayOpen(CenterError, new Point(0, -24), Entrance);
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
            // 服务重新拉起：完成通知监听幂等重启（旧连接已随服务中断，确保存活）
            StartCompletionNotifyIfEnabled();
            if (WebView.CoreWebView2 is null)
            {
                // Runtime 缺失等场景：WebView2 从未初始化成功，直接 Navigate 会 NRE 报天书
                ShowError("页面内核未就绪", "WebView2 未能初始化，请重启应用；若反复出现请检查 WebView2 Runtime。", allowRetry: false);
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

    /// <summary>打开 WebView2 Runtime 官方下载页（与"打开浏览器/充值页"同模式）。</summary>
    private void RuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"打开 Runtime 下载页失败: {ex.Message}");
        }
    }

    /// <summary>判定异常链中是否为 WebView2 Runtime 缺失（CreateAsync/EnsureCoreWebView2Async 抛出，含内层包装）。</summary>
    private static bool IsWebView2RuntimeMissing(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is WebView2RuntimeNotFoundException) return true;
        }
        return false;
    }

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
            // 服务重新拉起：完成通知监听幂等重启（旧连接已随服务中断，确保存活）
            StartCompletionNotifyIfEnabled();
            if (WebView.CoreWebView2 is null)
            {
                // Runtime 缺失等场景：WebView2 从未初始化成功，直接 Navigate 会 NRE 报天书
                ShowError("页面内核未就绪", "WebView2 未能初始化，请重启应用；若反复出现请检查 WebView2 Runtime。", allowRetry: false);
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
                "Harness 正在更新，现在关闭会中断安装，可能留下不完整的包。\n\n确定中断并关闭？",
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
            TrySetWebViewSuspended(true); // 挂起渲染进程，托盘驻留不空转（页面活动可拒绝，尽力而为）
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                try
                {
                    TrayIcon.ShowBalloonTip("DeepSeek Harness",
                        "已最小化到托盘，服务继续运行。\n双击图标恢复窗口，右键退出。",
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
        // 局部捕获防竞态：下载完成的 finally 可能并发将字段置 null
        var downloadCts = _appDownloadCts;
        if (_appUpdating && downloadCts is not null)
        {
            downloadCts.Cancel();
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
        ThemeManager.ThemeChanged -= OnThemeChangedApplyDwm; // 静态事件解除订阅，防窗口实例被挂住
        // "退出APP（保留服务）"：跳过 Shutdown，服务成孤儿继续跑（下次启动探测接管）；
        // 其余退出路径（托盘"退出"/关窗直退/强制退出/自更新重启）照旧停服
        if (_exitKeepServer)
        {
            AppendLog("退出APP：保留后台 dsh 服务运行");
            _exitKeepServer = false; // 一次性语义：防御性复位（窗口关闭即销毁，此处仅为未来复用不留残留）
        }
        else
        {
            _server.Shutdown();
        }
        _server.Dispose();
        _balance.Stop();
        _balance.Dispose();
        _completionNotify.Dispose(); // 先停事件流监听，迟到的完成帧回调经 DispatchUi 空转不碰已销毁 UI
        _updater.Dispose();
        _appUpdater.Dispose();
        App.ActiveServer = null;
        try
        {
            TrayIcon.Dispose(); // Hardcodet 资源（原生图标/隐藏窗口）随窗口显式释放
        }
        catch { /* 忽略 */ }
    }
}
