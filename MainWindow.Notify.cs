using System.Windows;
using dsh_app.Helpers;

namespace dsh_app;

/// <summary>主窗口分部：壳原生会话完成通知（CompletionNotifier 接线 / 气泡点击路由）。
/// 直连 dsh 服务 WebSocket 事件流检测 running→idle 边沿，托盘气泡弹系统样式通知；
/// 窗口托盘化/页面挂起期间照常工作（不依赖 WebView2，取代浏览器插件方案）。</summary>
public partial class MainWindow
{
    private readonly Server.CompletionNotifier _completionNotify = new();
    private bool _completionNotifyRunning;
    /// <summary>监听当前连接的服务端口（服务重启可能换端口：3080 被占时顺延，旧连接连的是死端口须重启跟随）。</summary>
    private int _completionNotifyPort;

    /// <summary>最近一个气泡的种类：点击路由用（余额 → 充值页；完成通知 → 恢复主窗口）。
    /// Windows toast 可能排队，种类记录是近似值（新气泡覆盖旧记录），误判代价低（多点一次/少跳一次）。</summary>
    private BalloonKind _balloonKind;

    private enum BalloonKind
    {
        None,
        Balance,
        Completion,
    }

    /// <summary>服务就绪后启动完成通知监听（仅设置开启时；幂等，端口变化时自动重启跟随）。</summary>
    private void StartCompletionNotifyIfEnabled()
    {
        if (!AppSettings.Current.SessionCompletionNotify) return;
        if (_completionNotifyRunning && _completionNotifyPort == _server.Port) return;
        if (_completionNotifyRunning)
            _completionNotify.Stop(); // 端口变了：旧监听连的是死端口，停了重启（下方重新 Start）
        _completionNotifyRunning = true;
        _completionNotifyPort = _server.Port;
        _completionNotify.Start(_server.Port);
    }

    /// <summary>设置窗关闭后同步完成通知开关状态（开 → 启动；关 → 停止）。</summary>
    private void SyncCompletionNotifyFromSettings()
    {
        if (AppSettings.Current.SessionCompletionNotify)
            StartCompletionNotifyIfEnabled();
        else if (_completionNotifyRunning)
        {
            _completionNotifyRunning = false;
            _completionNotify.Stop();
        }
    }

    /// <summary>会话运行结束（CompletionNotifier 回调，已 DispatchUi 上 UI 线程）：弹系统通知。</summary>
    private void OnSessionFinished(string sessionId, string? error)
    {
        var shortId = sessionId.Length <= 8 ? sessionId : sessionId[..8];
        try
        {
            _balloonKind = BalloonKind.Completion;
            if (error is null)
                TrayIcon.ShowBalloonTip("DSH 输出完成",
                    $"会话 {shortId} 的模型输出已完成。\n点击此通知恢复窗口。",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            else
                TrayIcon.ShowBalloonTip("DSH 会话出错",
                    $"会话 {shortId} 运行出错：{error}",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
        }
        catch (Exception ex)
        {
            AppendLog($"完成通知气泡失败: {ex.Message}");
        }
    }

    /// <summary>气泡点击路由：按最近气泡种类分派（余额 → 充值页；完成通知 → 恢复主窗口；其余不动作）。</summary>
    private void OnBalloonClicked()
    {
        var kind = _balloonKind;
        _balloonKind = BalloonKind.None;
        switch (kind)
        {
            case BalloonKind.Balance:
                OpenTopUpPage();
                break;
            case BalloonKind.Completion:
                ShowMainWindow();
                break;
        }
    }
}
