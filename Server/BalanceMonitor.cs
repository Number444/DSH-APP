using System.Net;
using System.Net.Http;
using System.Windows;

namespace dsh_app.Server;

/// <summary>
/// 顶栏余额/额度监控：定时轮询（60s）+ 手动刷新。
/// 生命周期由 MainWindow 管理：ShowBalance 开启后 Start()，关窗/开关关闭时 Stop()。
/// 查询与解析按设置委派给 IBalanceProvider（DeepSeek ¥ 余额 / Kimi 配额，见 BalanceProviders.cs），可热切换。
/// 安全红线：任何日志/异常信息不得包含 API Key 明文（如需要打码 sk-***）。
/// </summary>
public sealed class BalanceMonitor : IDisposable
{
    /// <summary>轮询间隔（失败不加速，固定 60s）。</summary>
    private const int PollIntervalMs = 60_000;

    /// <summary>手动刷新防抖窗口（毫秒）：距上次请求不足 2s 忽略。</summary>
    private const long DebounceMs = 2_000;

    /// <summary>HTTP 超时（10s）。</summary>
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();

    private Timer? _timer;
    private HttpClient? _http;
    private bool _disposed;
    private int _started;
    private int _refreshing;
    private long _lastRequestMs;
    private string? _lastBalanceText;

    /// <summary>已停止标志（Stop/Dispose 后置真；在途请求完成回调时忽略）。
    /// 全部通过 Volatile.Read/Write 访问（不使用 volatile 关键字以免 CS0420）。</summary>
    private bool _stopped = true;

    /// <summary>余额变化/刷新回调：参数为格式化后的金额文本（"23.50"，不含货币符号），失败/未配置/账户不可用为 null。</summary>
    public event Action<string?>? BalanceChanged;

    /// <summary>余额数值变化回调（供告警等数值逻辑）：成功为金额，失败/清除为 null。</summary>
    public event Action<decimal?>? BalanceAmountChanged;

    /// <summary>刷新失败但保留旧显示值（网络类失败 KeepOld 路径）时通知：参数为失败原因。
    /// 供手动刷新的状态卡反馈；后台轮询的失败订阅方应静默（顶栏保留旧值不动）。</summary>
    public event Action<string>? RefreshFailed;

    /// <summary>明细文本（总额/赠送/充值/更新时间），供悬停 ToolTip。</summary>
    public string? DetailText { get; private set; }

    /// <summary>最近一次成功获取的余额数值（null = 无）。</summary>
    public decimal? LastBalance { get; private set; }

    /// <summary>最近一次失败原因（null = 无）。</summary>
    public string? LastError { get; private set; }

    /// <summary>当前生效的 Key 来源：凭据文件 / 环境变量 / 手动（无 key 时为 null）。</summary>
    public string? ActiveSource { get; private set; }

    /// <summary>启动轮询：立即查一次 + 60s 定时。幂等：已启动不重复。</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        lock (_sync)
        {
            if (_disposed)
            {
                Interlocked.Exchange(ref _started, 0);
                return;
            }
            Volatile.Write(ref _stopped, false);
            _http ??= CreateHttpClient();
            _timer ??= new Timer(TimerTick, null, PollIntervalMs, PollIntervalMs);
        }

        // 立即查一次（异步，不阻塞调用线程）
        _ = RefreshAsync();
    }

    /// <summary>停止轮询，释放 Timer 与 HttpClient；在途请求完成时其回调被忽略。</summary>
    public void Stop()
    {
        lock (_sync)
        {
            Volatile.Write(ref _stopped, true);
            _timer?.Dispose();
            _timer = null;
            _http?.Dispose();
            _http = null;
        }
        Interlocked.Exchange(ref _started, 0); // 允许再次 Start
    }

    public void Dispose()
    {
        lock (_sync) _disposed = true;
        Stop();
    }

    /// <summary>刷新请求结果：Started=已实际发起（结果经回调）；其余为被忽略及原因。</summary>
    public enum RefreshResult
    {
        Started,
        /// <summary>距上次请求不足 2s（防抖，无在途请求）。</summary>
        Debounced,
        /// <summary>已有在途请求（如 60s 轮询正占通道），其结果仍会经回调到达。</summary>
        InFlight,
        /// <summary>监控已停止。</summary>
        Stopped,
    }

    /// <summary>
    /// 手动刷新（余额菜单触发）。返回 Started = 已实际发起请求（结果经 BalanceChanged 回调）；
    /// Debounced/InFlight/Stopped = 被忽略及原因，调用方据此提示（区分"在途"与"防抖"，
    /// 避免轮询占通道时误报"操作太频繁"）。
    /// </summary>
    public async Task<RefreshResult> RefreshAsync()
    {
        if (Volatile.Read(ref _stopped))
            return RefreshResult.Stopped;

        // 防抖：距上次请求不足 2s 忽略（CAS 抢占，并发调用仅一个进入）
        // 单调时钟（TickCount64）：系统时间回拨（NTP 校时等）不影响防抖判定
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastRequestMs);
        if (now - previous < DebounceMs)
            return RefreshResult.Debounced;
        if (Interlocked.CompareExchange(ref _lastRequestMs, now, previous) != previous)
            return RefreshResult.Debounced;

        // 防重入：已有在途请求则忽略（其结果仍会到来，非防抖）
        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
            return RefreshResult.InFlight;

        try
        {
            await RefreshCoreAsync().ConfigureAwait(false);
            return RefreshResult.Started; // 请求已实际发起（结果由回调体现）
        }
        catch
        {
            // 兜底：轮询由 Timer 驱动，未预期异常不得外泄（也不记录，红线）；请求已发起，仍按 Started 计
            return RefreshResult.Started;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    // ---- 内部实现 ----

    private void TimerTick(object? state)
    {
        if (Volatile.Read(ref _stopped))
            return;
        _ = RefreshAsync();
    }

    private static HttpClient CreateHttpClient() => new(
        new HttpClientHandler { Proxy = WebRequest.GetSystemWebProxy(), UseProxy = true })
    { Timeout = HttpTimeout };

    /// <summary>单次刷新：按设置选 provider → 取 key → 查询 → 上 UI 发布结果。</summary>
    private async Task RefreshCoreAsync()
    {
        // 每次刷新重选 provider（设置页切换来源后无需重启监控）；key 同样每次重取
        var provider = BalanceProviders.Current;
        // 来源快照：请求在途期间用户在设置页切换来源时，旧来源的迟到响应不得覆盖新来源 UI
        // （文本格式与色阶语义两套体系，串来源会显示错乱）
        var sourceKimi = BalanceProviders.IsKimi;
        var key = provider.ResolveKey(out var keyError, out var source);

        if (string.IsNullOrEmpty(key))
        {
            PublishFailure(keyError ?? "未配置 API Key", source, sourceKimi);
            return;
        }

        var outcome = await provider.FetchAsync(_http, key).ConfigureAwait(false);

        // 已停止：在途请求完成回调忽略（不碰已销毁的 UI）
        if (Volatile.Read(ref _stopped))
            return;

        if (outcome.Ok)
        {
            Dispatch(() =>
            {
                if (Volatile.Read(ref _stopped)) return;
                if (BalanceProviders.IsKimi != sourceKimi) return; // 来源已切换：丢弃旧来源迟到响应
                ActiveSource = source;
                DetailText = outcome.Detail;
                _lastBalanceText = outcome.BalanceText;
                LastBalance = outcome.BalanceAmount;
                LastError = null;
                BalanceChanged?.Invoke(outcome.BalanceText);
                BalanceAmountChanged?.Invoke(outcome.BalanceAmount);
            });
            return;
        }

        // 失败：网络类失败保留上次值（DetailText 保留）继续轮询；业务失败清空显示
        Dispatch(() =>
        {
            if (Volatile.Read(ref _stopped)) return;
            if (BalanceProviders.IsKimi != sourceKimi) return; // 来源已切换：丢弃旧来源迟到响应
            ActiveSource = source;
            LastError = outcome.Error;
            if (outcome.KeepOld && _lastBalanceText is not null)
            {
                // 保留上次显示值，仅更新错误原因；通知刷新失败（手动刷新的状态卡靠它反馈，
                // 否则用户点刷新遇网络错误时永远停在"正在刷新…"，且 pending 标志遗留到下次轮询误弹成功卡）
                RefreshFailed?.Invoke(outcome.Error ?? "未知原因");
                return;
            }
            DetailText = null;
            _lastBalanceText = null;
            LastBalance = null;
            BalanceChanged?.Invoke(null);
            BalanceAmountChanged?.Invoke(null);
        });
    }

    /// <summary>发布失败结果（清空旧值并回调 null）。</summary>
    private void PublishFailure(string error, string? source, bool sourceKimi)
    {
        Dispatch(() =>
        {
            if (Volatile.Read(ref _stopped)) return;
            if (BalanceProviders.IsKimi != sourceKimi) return; // 来源已切换：丢弃旧来源结果
            ActiveSource = source;
            DetailText = null;
            _lastBalanceText = null;
            LastBalance = null;
            LastError = error;
            BalanceChanged?.Invoke(null);
            BalanceAmountChanged?.Invoke(null);
        });
    }

    /// <summary>
    /// 在 UI 线程执行；无 UI 上下文（Application.Current 为 null）或已在 UI 线程时直接执行。
    /// 应用退出（Dispatcher 关闭）期间：InvokeAsync 会抛 TaskCanceledException/InvalidOperationException
    /// ——回调内容本身又检查 _stopped（Stop/Dispose 后忽略），关闭期派发异常就地吞掉即可。
    /// </summary>
    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        try
        {
            // 异步派发：不阻塞线程池延续（60s 轮询路径下避免占用线程池线程等待 UI）
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    action();
                }
                catch
                {
                    // 窗口销毁/关闭期回调异常：忽略（不击穿全局异常兜底）
                }
            });
        }
        catch
        {
            // 关闭期派发失败：无 UI 可更新，忽略
        }
    }
}
