using dsh_app.Helpers;
using dsh_app.Server;

namespace dsh_app;

/// <summary>
/// 主窗口分部：局域网共享代理（LanShareProxy）生命周期接线。
/// 启停时机：服务首次就绪 / 服务重启后 / 设置页变更后；退出时随 OnClosed 释放。
/// </summary>
public partial class MainWindow
{
    private readonly LanShareProxy _lanShare = new();

    /// <summary>同步串行化：设置页快速连拨开关时启停不并发（Kestrel 绑定/释放互斥）。</summary>
    private readonly SemaphoreSlim _lanShareLock = new(1, 1);

    /// <summary>构造期订阅代理日志/状态事件（与 _server/_balance 同一 DispatchUi 模式）。</summary>
    private void InitLanShare()
    {
        _lanShare.Log += msg => DispatchUi(() => AppendLog(msg));
        _lanShare.StateChanged += () => DispatchUi(UpdateTrayToolTip);
    }

    /// <summary>
    /// 按设置同步代理状态：开 → 启动/重启（目标端口取当前服务端口）；关 → 停止。
    /// 幂等，供 OnLoaded / 重试 / 重启 / Harness 更新后 / 设置变更统一调用。
    /// </summary>
    private async Task SyncLanShareFromSettingsAsync()
    {
        await _lanShareLock.WaitAsync();
        try
        {
            if (AppSettings.Current.LanShareEnabled)
            {
                var ok = await _lanShare.StartAsync(
                    AppSettings.Current.LanSharePort, _server.Port, AppSettings.Current.LanShareToken);
                if (!ok)
                    AppendLog($"LAN 共享未能启动（端口 {AppSettings.Current.LanSharePort} 可能被占用），详见日志");
            }
            else
            {
                await _lanShare.StopAsync();
            }
        }
        finally
        {
            _lanShareLock.Release();
        }
    }
}
