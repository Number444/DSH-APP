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
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using dsh_app.Helpers;
using dsh_app.Server;
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
    private readonly BalanceMonitor _balance = new();
    private readonly StringBuilder _logTail = new();
    private readonly string _logFilePath;
    private readonly ObservableCollection<StepRow> _steps = new();
    private readonly DispatcherTimer _heartbeat;
    private int _heartbeatMisses;
    private bool _rendererReloadTried;
    private bool _autoCheckRan;
    private bool _checking;
    private bool _updating;
    private bool _balanceRunning;
    private bool _abortUpdateConfirmed;
    /// <summary>用户点击余额触发的刷新待结果（仅此场景弹状态窗；后台轮询不弹）。</summary>
    private bool _userRefreshPending;
    /// <summary>状态窗序号：防快速连续点击时旧延迟关闭误关新弹窗。</summary>
    private int _statusSeq;

    public MainWindow()
    {
        InitializeComponent();

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dsh-app");
        Directory.CreateDirectory(logDir);
        _logFilePath = Path.Combine(logDir, "app.log");

        _server.Log += msg => Dispatcher.Invoke(() => AppendLog(msg));
        _server.StepChanged += (step, status, detail) => Dispatcher.Invoke(() => UpdateStep(step, status, detail));
        _server.ServerDied += () => Dispatcher.Invoke(OnServerDied);

        // Harness 更新器：日志走同一通道
        _updater.Log += msg => Dispatcher.Invoke(() => AppendLog(msg));

        // 余额监控：回调经 Dispatcher 上 UI（R4 线程铁律）
        _balance.BalanceChanged += text => Dispatcher.Invoke(() => OnBalanceChanged(text));

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
        OverlayDetail.Text = string.IsNullOrWhiteSpace(_logTail.ToString())
            ? "正在拉起 dsh web 服务并等待就绪，首次启动可能需要 10~20 秒…"
            : _logTail.ToString().TrimEnd();
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

    // ---------------- 菜单（关于 / 设置） ----------------

    private void TitleBtnMenu_Click(object sender, RoutedEventArgs e) => MenuPopup.IsOpen = true;

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        new Views.AboutWindow { Owner = this }.ShowDialog();
    }

    private void MenuLog_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenLog();
    }

    private void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        var dlg = new Views.SettingsWindow { Owner = this };
        dlg.ShowDialog();
        // 设置可能改了余额开关，关闭后同步启动/停止
        SyncBalanceFromSettings();
    }

    // ---------------- 菜单（Harness 更新） ----------------

    private async void MenuCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        if (_checking || _updating) return;
        _checking = true;
        MenuCheckUpdate.IsEnabled = false;
        MenuCheckUpdate.Content = "检查更新…";
        try
        {
            // 手动点击一律重新检查（不复用启动时的陈旧结果，审查加固项）
            var ok = await _updater.CheckAsync();
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
        finally
        {
            _checking = false;
            MenuCheckUpdate.IsEnabled = true;
            UpdateMenuButtonText(); // 复位"检查更新…"文案（有新版则显示高亮提示）
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
        MenuCheckUpdate.IsEnabled = false;
        MenuCheckUpdate.Content = "正在更新 Harness…";
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
                UpdateMenuButtonText();
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
            MenuCheckUpdate.IsEnabled = true;
            UpdateMenuButtonText(); // 恢复菜单文案（"正在更新 Harness…" → 检查更新/更新可用）
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
                UpdateMenuButtonText();
            }
        }
        catch (Exception ex)
        {
            // 后台检查失败不打扰用户（写日志即可）；防 async void 未处理异常
            AppendLog($"自动检查更新异常：{ex.Message}");
        }
    }

    /// <summary>菜单项文案：有新版时高亮提示（主题色走资源，R8），否则复位。</summary>
    private void UpdateMenuButtonText()
    {
        if (_updater.LastCheckSucceeded && _updater.HasUpdate)
        {
            MenuCheckUpdate.Content = $"更新可用：v{_updater.LocalVersion} → v{_updater.LatestVersion}";
            MenuCheckUpdate.SetResourceReference(Button.ForegroundProperty, "AccentBlueBrush");
        }
        else
        {
            MenuCheckUpdate.Content = "检查更新";
            MenuCheckUpdate.ClearValue(Button.ForegroundProperty);
        }
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
        if (text is null)
        {
            TitleBalanceText.Text = "—";
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
            TitleBalance.ToolTip = _balance.DetailText + "\n点击刷新";
            if (_userRefreshPending)
            {
                _userRefreshPending = false;
                ShowBalanceStatus($"✓ 余额已更新：¥ {text}",
                    (Brush)FindResource("AccentGreenBrush"));
            }
        }
    }

    /// <summary>在余额按钮下方显示状态卡（自动关闭；序号防连续点击竞态）。</summary>
    private async void ShowBalanceStatus(string text, Brush? brush = null, int stayMs = 2200)
    {
        var seq = ++_statusSeq;
        BalanceStatusText.Text = text;
        BalanceStatusText.Foreground = brush ?? (Brush)FindResource("TextSecondaryBrush");
        BalanceStatusPopup.IsOpen = true;
        await Task.Delay(stayMs);
        if (seq == _statusSeq)
            BalanceStatusPopup.IsOpen = false;
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

    private async void TitleBalance_Click(object sender, RoutedEventArgs e)
    {
        if (TitleBalance.Visibility != Visibility.Visible) return;
        // 弹回动效（BackEase 轻微过冲，模拟弹性按键）
        if (TitleBalance.RenderTransform is ScaleTransform scale)
        {
            var pop = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        // 状态卡：先显示"刷新中"，结果由 BalanceChanged 回调更新（仅用户点击场景弹窗）
        _userRefreshPending = true;
        ShowBalanceStatus("正在刷新余额…");
        var requested = await _balance.RefreshAsync();
        if (!requested)
        {
            // 防抖/防重入忽略：无回调会来，直接提示并复位
            _userRefreshPending = false;
            ShowBalanceStatus("操作太频繁，请稍后再试",
                (Brush)FindResource("AccentOrangeBrush"));
        }
    }

    private void TitleBtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

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
            Process.Start(new ProcessStartInfo("notepad.exe", _logFilePath) { UseShellExecute = true });
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
        var tail = _logTail.ToString().TrimEnd();
        return string.IsNullOrWhiteSpace(tail)
            ? "请查看日志确认 dsh 是否安装、端口是否被占用。"
            : tail;
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

        // 覆盖层可见时同步展示最新日志并滚到底
        if (Overlay.Visibility == Visibility.Visible)
        {
            OverlayDetail.Text = _logTail.ToString().TrimEnd();
            OverlayLogScroller.ScrollToEnd();
        }
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
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _server.Shutdown();
        _server.Dispose();
        _balance.Stop();
        _balance.Dispose();
        _updater.Dispose();
        App.ActiveServer = null;
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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    /// <summary>
    /// WindowStyle=None + WindowChrome 下，WPF 最大化默认按整个屏幕扩展窗口
    /// （顶部溢出屏幕、底部盖住任务栏）。此处把最大化位置/尺寸钳制到窗口
    /// 所在显示器的工作区（物理像素，系统层面生效，与 DPI 无关）。
    /// </summary>
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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
