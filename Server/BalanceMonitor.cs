using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using dsh_app.Helpers;

namespace dsh_app.Server;

/// <summary>
/// DeepSeek 开放平台余额监控：定时轮询（60s）+ 手动刷新。
/// 生命周期由 MainWindow 管理：ShowBalance 开启后 Start()，关窗/开关关闭时 Stop()。
/// 安全红线：任何日志/异常信息不得包含 API Key 明文（如需要打码 sk-***）。
/// </summary>
public sealed class BalanceMonitor : IDisposable
{
    /// <summary>余额查询接口地址（官方 Get User Balance）。</summary>
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";

    /// <summary>轮询间隔（失败不加速，固定 60s）。</summary>
    private const int PollIntervalMs = 60_000;

    /// <summary>手动刷新防抖窗口：距上次请求不足 2s 忽略。</summary>
    private const long DebounceTicks = TimeSpan.TicksPerSecond * 2;

    /// <summary>HTTP 超时（10s）。</summary>
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();

    private Timer? _timer;
    private HttpClient? _http;
    private bool _disposed;
    private int _started;
    private int _refreshing;
    private long _lastRequestTicks;
    private string? _lastBalanceText;

    /// <summary>已停止标志（Stop/Dispose 后置真；在途请求完成回调时忽略）。
    /// 全部通过 Volatile.Read/Write 访问（不使用 volatile 关键字以免 CS0420）。</summary>
    private bool _stopped = true;

    /// <summary>余额变化/刷新回调：参数为格式化后的金额文本（"23.50"，不含货币符号），失败/未配置/账户不可用为 null。</summary>
    public event Action<string?>? BalanceChanged;

    /// <summary>余额数值变化回调（供告警等数值逻辑）：成功为金额，失败/清除为 null。</summary>
    public event Action<decimal?>? BalanceAmountChanged;

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

    /// <summary>
    /// 手动刷新（余额文本点击触发）。返回 true = 实际发起请求（结果经 BalanceChanged 回调）；
    /// false = 被防抖（距上次请求 &lt;2s）/ 防重入（在途请求）/ 已停止 忽略，调用方据此提示用户。
    /// </summary>
    public async Task<bool> RefreshAsync()
    {
        if (Volatile.Read(ref _stopped))
            return false;

        // 防抖：距上次请求不足 2s 忽略（CAS 抢占，并发调用仅一个进入）
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref _lastRequestTicks);
        if (now - previous < DebounceTicks)
            return false;
        if (Interlocked.CompareExchange(ref _lastRequestTicks, now, previous) != previous)
            return false;

        // 防重入：已有在途请求则忽略
        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
            return false;

        try
        {
            await RefreshCoreAsync().ConfigureAwait(false);
            return true; // 请求已实际发起（结果由回调体现）
        }
        catch
        {
            // 兜底：轮询由 Timer 驱动，未预期异常不得外泄（也不记录，红线）；请求已发起，仍按 true 计
            return true;
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

    /// <summary>单次刷新：取 key → 请求余额 → 上 UI 发布结果。</summary>
    private async Task RefreshCoreAsync()
    {
        // ① 每次刷新前重取 key（凭据文件可能被 dsh 侧 Web Models 页更新）
        var key = ResolveKey(out var keyError, out var source);

        if (string.IsNullOrEmpty(key))
        {
            PublishFailure(keyError ?? "未配置 API Key", source);
            return;
        }

        var outcome = await FetchBalanceAsync(key).ConfigureAwait(false);

        // 已停止：在途请求完成回调忽略（不碰已销毁的 UI）
        if (Volatile.Read(ref _stopped))
            return;

        if (outcome.Ok)
        {
            Dispatch(() =>
            {
                if (Volatile.Read(ref _stopped)) return;
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
            ActiveSource = source;
            LastError = outcome.Error;
            if (outcome.KeepOld && _lastBalanceText is not null)
                return; // 保留上次显示值，仅更新错误原因
            DetailText = null;
            _lastBalanceText = null;
            LastBalance = null;
            BalanceChanged?.Invoke(null);
            BalanceAmountChanged?.Invoke(null);
        });
    }

    /// <summary>发布失败结果（清空旧值并回调 null）。</summary>
    private void PublishFailure(string error, string? source)
    {
        Dispatch(() =>
        {
            if (Volatile.Read(ref _stopped)) return;
            ActiveSource = source;
            DetailText = null;
            _lastBalanceText = null;
            LastBalance = null;
            LastError = error;
            BalanceChanged?.Invoke(null);
            BalanceAmountChanged?.Invoke(null);
        });
    }

    /// <summary>Key 来源链：① 凭据文件（需授权）→ ② 环境变量 → ③ 手动（DPAPI）。任一命中即用。</summary>
    private static string? ResolveKey(out string? error, out string? source)
    {
        error = null;
        source = null;

        // ① ~/.dsh/.credentials.yaml（仅用户显式授权后读取）
        if (AppSettings.Current.AllowReadDshCredentials)
        {
            var key = CredentialsReader.ReadDeepSeekApiKey();
            if (!string.IsNullOrWhiteSpace(key))
            {
                source = "凭据文件";
                return key;
            }
        }

        // ② 环境变量（无需授权，壳读自身进程环境）
        var envKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            source = "环境变量";
            return envKey;
        }

        // ③ 设置页手动填写（DPAPI 密文，仅本机当前用户可解；加解密统一走 DpapiHelper）
        var encrypted = AppSettings.Current.EncryptedApiKey;
        if (!string.IsNullOrWhiteSpace(encrypted))
        {
            var key = DpapiHelper.Decrypt(encrypted);
            if (!string.IsNullOrEmpty(key))
            {
                source = "手动";
                return key;
            }
            error = "API Key 解密失败";
        }

        return null;
    }

    /// <summary>请求余额并解析；内部消化所有网络/解析异常，返回结构化结果。</summary>
    private async Task<FetchOutcome> FetchBalanceAsync(string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var http = _http;
        if (http is null)
            return new FetchOutcome { Error = "网络错误", KeepOld = true };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return new FetchOutcome { Error = "网络错误", KeepOld = true }; // 超时
        }
        catch (HttpRequestException)
        {
            return new FetchOutcome { Error = "网络错误", KeepOld = true };
        }
        catch (Exception)
        {
            // 停止时释放 HttpClient 等未预期情况，同样按网络失败处理（结果会被 stopped 忽略）
            return new FetchOutcome { Error = "网络错误", KeepOld = true };
        }

        using (response)
        {
            // 401/403：Key 无效（业务失败，清空旧值）
            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new FetchOutcome { Error = "API Key 无效" };
            }

            // 其他非成功状态按网络失败处理（保留上次值）
            if (!response.IsSuccessStatusCode)
                return new FetchOutcome { Error = "网络错误", KeepOld = true };

            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return new FetchOutcome { Error = "网络错误", KeepOld = true };
            }

            return ParseBalance(json);
        }
    }

    /// <summary>解析余额 JSON：is_available / balance_infos（CNY 优先）→ 金额与明细。</summary>
    private static FetchOutcome ParseBalance(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // is_available=false → 账户不可用（欠费/封禁等）
            if (root.TryGetProperty("is_available", out var available) &&
                available.ValueKind == JsonValueKind.False)
            {
                return new FetchOutcome { Error = "账户不可用" };
            }

            // balance_infos 缺失/非数组/为空 → 格式异常
            if (!root.TryGetProperty("balance_infos", out var infos) ||
                infos.ValueKind != JsonValueKind.Array ||
                infos.GetArrayLength() == 0)
            {
                return new FetchOutcome { Error = "余额数据格式异常" };
            }

            // currency == "CNY" 优先，无则取第一个
            var chosen = default(JsonElement);
            var found = false;
            foreach (var item in infos.EnumerateArray())
            {
                if (item.TryGetProperty("currency", out var currency) && currency.GetString() == "CNY")
                {
                    chosen = item;
                    found = true;
                    break;
                }
            }
            if (!found)
                chosen = infos[0];

            // 总额严格解析；赠送/充值为展示字段，缺省按 0 处理
            if (!TryGetDecimal(chosen, "total_balance", out var total))
                return new FetchOutcome { Error = "余额数据格式异常" };
            TryGetDecimal(chosen, "granted_balance", out var granted);
            TryGetDecimal(chosen, "topped_up_balance", out var toppedUp);

            var balanceText = total.ToString("0.00", CultureInfo.InvariantCulture);
            var detail = $"总额 ¥{Money(total)} · 赠送 ¥{Money(granted)} · 充值 ¥{Money(toppedUp)} · 更新 {DateTime.Now:HH:mm:ss}";

            return new FetchOutcome { Ok = true, BalanceText = balanceText, BalanceAmount = total, Detail = detail };
        }
        catch (JsonException)
        {
            return new FetchOutcome { Error = "余额数据格式异常" };
        }
        catch (Exception)
        {
            // 兜底：不输出异常细节（红线：不得含 key 明文；数据不可信按格式异常处理）
            return new FetchOutcome { Error = "余额数据格式异常" };
        }
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static bool TryGetDecimal(JsonElement item, string property, out decimal value)
    {
        value = 0m;
        if (!item.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        return decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>在 UI 线程执行；无 UI 上下文（Application.Current 为 null）或已在 UI 线程时直接执行。</summary>
    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    // ---- 结果模型 ----

    /// <summary>一次余额查询的结构化结果。</summary>
    private sealed class FetchOutcome
    {
        /// <summary>是否成功。</summary>
        public bool Ok;

        /// <summary>网络失败时是否保留上次显示值（true 则仅更新错误原因，不回调）。</summary>
        public bool KeepOld;

        /// <summary>成功：格式化金额文本（"23.50"）。</summary>
        public string? BalanceText;

        /// <summary>成功：金额数值（告警比较用）。</summary>
        public decimal? BalanceAmount;

        /// <summary>成功：ToolTip 明细文本。</summary>
        public string? Detail;

        /// <summary>失败原因文案（null = 成功）。</summary>
        public string? Error;
    }
}
