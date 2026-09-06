using System.Windows;
using dsh_app.Helpers;

namespace dsh_app;

/// <summary>主窗口分部：会话完成通知 v2（dsh-notify 插件）与托盘气泡点击路由。
/// 通知本体已由 harness 进程内插件承担（Server/NotifyPluginInstaller 首启幂等安装，
/// 方向文件 docs/NEXT-NOTIFY-PLUGIN.md）；壳只负责安装/版本更新与设置开关同步。
/// 旧 v1（CompletionNotifier 直连 /api/events.host）随新版 harness 移除该端点而退役删除。</summary>
public partial class MainWindow
{
    /// <summary>最近一个气泡的种类：点击路由用（余额 → 充值页）。
    /// Windows toast 可能排队，种类记录是近似值（新气泡覆盖旧记录），误判代价低（多点一次/少跳一次）。</summary>
    private BalloonKind _balloonKind;

    private enum BalloonKind
    {
        None,
        Balance,
        BalanceError,
    }

    /// <summary>服务拉起前安装/更新 dsh-notify 插件（bundle 只在 harness 启动时装载，必须先装后拉），
    /// 并把壳侧开关同步进 settings.yaml。幂等可重入。通知是增值功能，失败只记日志。</summary>
    private void EnsureNotifyPluginInstalled()
    {
        if (Server.NotifyPluginInstaller.EnsureInstalled(msg => DispatchUi(() => AppendLog(msg))))
            AppendLog("会话完成通知插件 dsh-notify 已安装/更新，随本次 harness 启动装载");
        Server.NotifyPluginInstaller.SyncEnabled(AppSettings.Current.SessionCompletionNotify,
            msg => DispatchUi(() => AppendLog(msg)));
    }

    /// <summary>设置窗关闭后同步完成通知开关（写入 settings.yaml 的 dsh-notify.enabled；
    /// 插件在完成边沿重读配置，改动即时生效，无需重启 dsh 服务）。</summary>
    private void SyncCompletionNotifyFromSettings()
    {
        Server.NotifyPluginInstaller.SyncEnabled(AppSettings.Current.SessionCompletionNotify,
            msg => DispatchUi(() => AppendLog(msg)));
        AppendLog(AppSettings.Current.SessionCompletionNotify
            ? "会话完成通知已开启（dsh-notify.enabled=true）"
            : "会话完成通知已关闭（dsh-notify.enabled=false）");
    }

    /// <summary>气泡点击路由：按最近气泡种类分派（余额 → 充值页；余额异常 → 设置页；其余不动作）。</summary>
    private void OnBalloonClicked()
    {
        var kind = _balloonKind;
        _balloonKind = BalloonKind.None;
        switch (kind)
        {
            case BalloonKind.Balance:
                OpenTopUpPage();
                break;
            case BalloonKind.BalanceError:
                ShowMainWindow();
                OpenSettingsDialog();
                break;
        }
    }
}
