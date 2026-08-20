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

/// <summary>主窗口分部：顶栏余额显示（轮询启停 / 状态色 / 告警 / 状态卡 / 按钮交互）（自 MainWindow.xaml.cs 拆出，纯移动无逻辑变更）。</summary>
public partial class MainWindow
{
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
        // 托盘悬停同步余额（窗口隐藏时也能看到；状态段由 UpdateTrayToolTip 组合）
        _trayBalanceText = text;
        UpdateTrayToolTip();

        var isKimi = BalanceProviders.IsKimi;
        if (text is null)
        {
            TitleBalanceText.Text = "—";
            // 失败/无值：显式弱色（SetResourceReference = DynamicResource 语义，主题切换即时跟随）
            TitleBalanceText.SetResourceReference(TextBlock.ForegroundProperty, "TextWeakBrush");
            var label = isKimi ? "额度" : "余额";
            TitleBalance.ToolTip = _balance.LastError is null
                ? $"{label}获取失败（点击重试）"
                : $"{label}获取失败：{_balance.LastError}（点击重试）";
            if (_userRefreshPending)
            {
                _userRefreshPending = false;
                ShowBalanceStatus($"✗ 刷新失败：{_balance.LastError ?? "未知原因"}",
                    (Brush)FindResource("AccentRedBrush"));
            }
        }
        else
        {
            // DeepSeek：金额文本（"23.50"）加 ¥ 前缀；Kimi：完整双窗口已用百分比文本（"5h: 13% / 7d: 56%"）
            TitleBalanceText.Text = isKimi ? text : $"¥ {text}";
            TitleBalance.ToolTip = _balance.DetailText +
                (isKimi ? "\n左键菜单：刷新 / 用量页" : "\n左键菜单：刷新 / 充值");
            // 状态色（规范见 docs/UI-GUIDELINES.md §4.1）：
            // DeepSeek 按金额 ¥5/¥2 分级（越低越紧张）；Kimi 按两窗口较高的已用百分比 80%/90% 分级（越高越紧张）。
            // SetResourceReference = DynamicResource：主题切换即时跟随（R8），不残留旧主题色
            if (_balance.LastBalance is decimal amount)
            {
                TitleBalanceText.SetResourceReference(TextBlock.ForegroundProperty,
                    isKimi
                        ? (amount >= KimiDangerPercent ? "AccentRedBrush"
                            : amount >= KimiWarnPercent ? "BalanceAlertBrush"
                            : "AccentBlueBrush")
                        : (amount < BalanceDangerThreshold ? "AccentRedBrush"
                            : amount < BalanceWarnThreshold ? "BalanceAlertBrush"
                            : "AccentBlueBrush"));
            }
            if (_userRefreshPending)
            {
                _userRefreshPending = false;
                ShowBalanceStatus(isKimi ? $"✓ 额度已更新：{text}" : $"✓ 余额已更新：¥ {text}",
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
        // 低额告警仅 DeepSeek ¥ 余额生效（Kimi 是周期配额，跌破阈值提醒意义不大）
        if (amount is null || !AppSettings.Current.BalanceAlertEnabled || BalanceProviders.IsKimi)
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

    /// <summary>托盘悬停文案：标题 + 服务状态（启动中/运行中/已断开）+ 余额段（有值时）。</summary>
    private void UpdateTrayToolTip()
    {
        var text = $"DeepSeek Harness · {_serviceStateText}";
        if (_trayBalanceText is not null)
            text += BalanceProviders.IsKimi ? $" — Kimi {_trayBalanceText}" : $" — 余额 ¥{_trayBalanceText}";
        TrayIcon.ToolTipText = text;
    }

    /// <summary>按当前显示来源重建余额菜单项文案（DeepSeek：刷新余额/打开充值页；Kimi：刷新额度/打开 Kimi 用量页）。
    /// 在构造与每次打开余额菜单前调用（来源可能在设置页刚切换）。</summary>
    private void RefreshBalanceMenuItems()
    {
        var isKimi = BalanceProviders.IsKimi;
        BalanceMenu.ItemsSource = new[]
        {
            new AppMenuItem("refresh", isKimi ? "刷新额度" : "刷新余额"),
            new AppMenuItem("topup", isKimi ? "打开 Kimi 用量页" : "打开充值页"),
        };
    }

    /// <summary>右键余额交互已删除（v1.2.1）：左键菜单内含刷新/充值；此注释占位防误加回。</summary>

    /// <summary>手动刷新遇网络类失败（KeepOld 保留旧值路径）：仅用户触发的刷新弹状态卡反馈；
    /// 后台轮询的同类失败静默（顶栏保留旧值，不打扰）。</summary>
    private void OnBalanceRefreshFailed(string error)
    {
        if (!_userRefreshPending) return;
        _userRefreshPending = false;
        ShowBalanceStatus($"✗ 刷新失败：{error}（保留上次值）",
            (Brush)FindResource("AccentRedBrush"));
    }

    /// <summary>在余额按钮下方显示状态卡（自动关闭；序号防连续点击竞态；水平超屏左移钳位）。</summary>
    private async void ShowBalanceStatus(string text, Brush? brush = null, int stayMs = 2200)
    {
        var seq = ++_statusSeq;
        BalanceStatusText.Text = text;
        BalanceStatusText.Foreground = brush ?? (Brush)FindResource("TextSecondaryBrush");
        // 与余额菜单同锚点，防叠显：菜单仍打开且未在收拢时瞬关；水平偏移复位（上次钳位可能留负值）。
        // 收拢动画在跑（本次点击正来自"刷新"菜单项）时不动它——瞬关会杀掉退出动画
        if (BalanceMenuPopup.IsOpen && !PopupAnimator.IsClosing(BalanceMenu.RootCard))
            BalanceMenuPopup.IsOpen = false;
        BalanceStatusPopup.HorizontalOffset = 0;
        // 收拢动画在飞时收到新内容（点外部淡出中立刻又刷新）：取消收拢并播回打开动画。
        // PlayOpen 的 BeginAnimation 替换语义使旧收拢时钟静默作废——其完成回调不会触发，不会误关新卡
        var closingChild = BalanceStatusPopup.IsOpen ? BalanceStatusPopup.Child as FrameworkElement : null;
        var reopenDuringClose = closingChild is not null && PopupAnimator.IsClosing(closingChild);
        BalanceStatusPopup.IsOpen = true;
        if (reopenDuringClose)
            PopupAnimator.PlayOpen(closingChild!, options: PopupAnimator.LightOpen);
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
        // IsLoaded 守卫：窗口在延迟期间已关闭时不再触碰 Popup（托盘隐藏 IsLoaded 仍为 true，正常收拢不受影响）
        if (seq == _statusSeq && IsLoaded)
            DismissBalanceStatus();
    }

    /// <summary>动画关闭余额状态卡（自动消失与外部点击钩子共用入口）：
    /// 递增序号使待决的自动关闭失效；PlayClose 幂等（收拢在跑时重入直接返回，由原回调收尾）。</summary>
    private void DismissBalanceStatus()
    {
        if (!BalanceStatusPopup.IsOpen) return;
        _statusSeq++;
        // 淡出动画完成后再置 IsOpen=false（Popup 关闭即销毁视觉树，顺序不可反）
        if (BalanceStatusPopup.Child is FrameworkElement child)
            PopupAnimator.PlayClose(child, () => BalanceStatusPopup.IsOpen = false, PopupAnimator.LightClose);
        else
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
}
