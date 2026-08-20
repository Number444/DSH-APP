using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using dsh_app.Helpers;

namespace dsh_app.Server;

/// <summary>一次余额/额度查询的结构化结果（DeepSeek 与 Kimi 共用）。</summary>
internal sealed class FetchOutcome
{
    /// <summary>是否成功。</summary>
    public bool Ok;

    /// <summary>网络失败时是否保留上次显示值（true 则仅更新错误原因，不回调）。</summary>
    public bool KeepOld;

    /// <summary>成功：顶栏主文本。DeepSeek = 格式化金额（"23.50"，MainWindow 加 ¥ 前缀）；
    /// Kimi = 双窗口百分比（"5h: 87% / 1w: 44%"，完整文本无前缀）。</summary>
    public string? BalanceText;

    /// <summary>成功：状态色/告警用的数值。DeepSeek = 金额（元）；Kimi = 两窗口中较高的已用百分比（0~100）。</summary>
    public decimal? BalanceAmount;

    /// <summary>成功：ToolTip 明细文本。</summary>
    public string? Detail;

    /// <summary>失败原因文案（null = 成功）。</summary>
    public string? Error;
}

/// <summary>余额来源：Key 解析链 + 查询与解析。每次刷新由 BalanceMonitor 按设置选用，可热切换。</summary>
internal interface IBalanceProvider
{
    /// <summary>Key 来源链：任一命中即用；全部未命中返回 null 并给出错误文案。</summary>
    string? ResolveKey(out string? error, out string? source);

    /// <summary>查询并解析；内部消化所有网络/解析异常，返回结构化结果（不得外泄 key）。</summary>
    Task<FetchOutcome> FetchAsync(HttpClient? http, string key);
}

/// <summary>当前设置选中的来源（默认 DeepSeek）。</summary>
internal static class BalanceProviders
{
    public static readonly IBalanceProvider DeepSeek = new DeepSeekBalanceProvider();
    public static readonly IBalanceProvider Kimi = new KimiUsageProvider();

    /// <summary>按 AppSettings.BalanceSource 选择；非法值按 DeepSeek 兜底。</summary>
    public static IBalanceProvider Current =>
        AppSettings.Current.BalanceSource == "kimi" ? Kimi : DeepSeek;

    /// <summary>当前是否为 Kimi 额度模式（UI 文案/状态色分支用）。</summary>
    public static bool IsKimi => AppSettings.Current.BalanceSource == "kimi";
}

/// <summary>HTTP 公共段：Bearer 请求 → 状态码归一（401/403 = Key 无效；其余非 2xx = 网络错误保留旧值）。</summary>
internal abstract class BalanceProviderBase : IBalanceProvider
{
    public abstract string? ResolveKey(out string? error, out string? source);

    public async Task<FetchOutcome> FetchAsync(HttpClient? http, string key)
    {
        if (http is null)
            return new FetchOutcome { Error = "网络错误", KeepOld = true };

        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

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

            return Parse(json);
        }
    }

    /// <summary>查询接口地址。</summary>
    protected abstract string Url { get; }

    /// <summary>解析成功响应体；实现内不得输出异常细节（红线：不得含 key 明文）。</summary>
    protected abstract FetchOutcome Parse(string json);
}

/// <summary>DeepSeek 开放平台余额（GET api.deepseek.com/user/balance）。</summary>
internal sealed class DeepSeekBalanceProvider : BalanceProviderBase
{
    protected override string Url => "https://api.deepseek.com/user/balance";

    /// <summary>Key 来源链：① 凭据文件（需授权）→ ② 环境变量 → ③ 手动（DPAPI）。任一命中即用。</summary>
    public override string? ResolveKey(out string? error, out string? source)
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
        var envKey = Environment.GetEnvironmentVariable(CredentialsReader.DeepSeekApiKeyName);
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

    /// <summary>解析余额 JSON：is_available / balance_infos（CNY 优先）→ 金额与明细。</summary>
    protected override FetchOutcome Parse(string json)
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
        if (!item.TryGetProperty(property, out var element))
            return false;
        // 兼容 JSON 数值与字符串两种形态（API 微调不至于整窗解析失败）
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetDecimal(out value);
        if (element.ValueKind == JsonValueKind.String)
            return decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return false;
    }
}

/// <summary>
/// Kimi for Coding 额度（GET api.kimi.com/coding/v1/usages）：配额制（次数），非 ¥ 余额。
/// 响应含两个窗口：limits[] 中 300 分钟（5h）窗口 + 根 usage（周级总额度）；
/// 顶栏显示双窗口已用百分比（"5h: 13% / 7d: 56%"，与 Kimi 控制台语义一致），状态色取两者较高值。
/// </summary>
internal sealed class KimiUsageProvider : BalanceProviderBase
{
    protected override string Url => "https://api.kimi.com/coding/v1/usages";

    /// <summary>Key 来源链与 DeepSeek 同构：① 凭据文件 KIMI_CODING_API_KEY（需授权）→ ② 环境变量 → ③ 手动（DPAPI）。</summary>
    public override string? ResolveKey(out string? error, out string? source)
    {
        error = null;
        source = null;

        if (AppSettings.Current.AllowReadDshCredentials)
        {
            var key = CredentialsReader.ReadKimiCodingApiKey();
            if (!string.IsNullOrWhiteSpace(key))
            {
                source = "凭据文件";
                return key;
            }
        }

        var envKey = Environment.GetEnvironmentVariable(CredentialsReader.KimiCodingApiKeyName);
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            source = "环境变量";
            return envKey;
        }

        var encrypted = AppSettings.Current.EncryptedKimiApiKey;
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

    /// <summary>解析额度 JSON：limits[300 分钟窗口] + 根 usage（周）→ 双窗口百分比与明细。</summary>
    protected override FetchOutcome Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 5h 窗口：limits[] 中 window.duration==300 且 timeUnit=="TIME_UNIT_MINUTE"（兜底取第一项）
            QuotaWindow? fiveHour = null;
            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                var first = default(JsonElement);
                var hasFirst = false;
                foreach (var item in limits.EnumerateArray())
                {
                    if (!hasFirst) { first = item; hasFirst = true; }
                    // duration 用宽容读取（TryGetLong：数值/字符串/浮点都接），避免 300.0 这类形态直接炸掉整窗解析
                    if (item.TryGetProperty("window", out var window) &&
                        TryGetLong(window, "duration", out var durMinutes) &&
                        durMinutes == 300 &&
                        window.TryGetProperty("timeUnit", out var unit) &&
                        unit.GetString() == "TIME_UNIT_MINUTE")
                    {
                        fiveHour = ReadWindow(item);
                        break;
                    }
                }
                // 未匹配到 300 分钟窗口时兜底第一项（字段结构变化不至于全灭）
                if (fiveHour is null && hasFirst)
                    fiveHour = ReadWindow(first);
            }

            // 周额度：根 usage
            QuotaWindow? weekly = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                weekly = ReadWindow(usage);

            if (fiveHour is null && weekly is null)
                return new FetchOutcome { Error = "额度数据格式异常" };

            // 顶栏文本：双窗口齐全 → "5h: 13% / 7d: 56%"（已用百分比）；缺哪个显哪个
            var text = (fiveHour, weekly) switch
            {
                ({ } h, { } w) => $"5h: {h.Percent:0}% / 7d: {w.Percent:0}%",
                ({ } h, null) => $"5h: {h.Percent:0}%",
                (null, { } w) => $"7d: {w.Percent:0}%",
                _ => null,
            };
            if (text is null)
                return new FetchOutcome { Error = "额度数据格式异常" };

            // 状态色数值 = 两窗口较高（较紧张）的已用百分比
            var worst = (fiveHour, weekly) switch
            {
                ({ } h, { } w) => Math.Max(h.Percent, w.Percent),
                ({ } h, null) => h.Percent,
                (null, { } w) => w.Percent,
                _ => 0m,
            };

            var parts = new List<string>(3);
            if (fiveHour is { } fh)
                parts.Add($"5h 窗口 已用 {fh.Used}/{fh.Limit} · 重置 {FormatReset(fh.ResetTime)}");
            if (weekly is { } wk)
                parts.Add($"7d 额度 已用 {wk.Used}/{wk.Limit} · 重置 {FormatReset(wk.ResetTime)}");
            parts.Add($"更新 {DateTime.Now:HH:mm:ss}");

            return new FetchOutcome
            {
                Ok = true,
                BalanceText = text,
                BalanceAmount = worst,
                Detail = string.Join("；", parts),
            };
        }
        catch (JsonException)
        {
            return new FetchOutcome { Error = "额度数据格式异常" };
        }
        catch (Exception)
        {
            return new FetchOutcome { Error = "额度数据格式异常" };
        }
    }

    /// <summary>读取一个配额窗口（detail 子对象优先，根对象兜底）：limit/used/resetTime。</summary>
    private static QuotaWindow? ReadWindow(JsonElement node)
    {
        // limits[] 条目的数值在 detail 子对象；根 usage 直接在节点上
        var holder = node.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object
            ? detail
            : node;

        if (!TryGetLong(holder, "limit", out var limit) || limit <= 0)
            return null;
        if (!TryGetLong(holder, "used", out var used))
            return null;

        // 钳位 0~100：API 统计延迟可能使 used 短暂大于 limit，百分比破百会让用户困惑
        var percent = Math.Clamp(used * 100m / limit, 0m, 100m);
        DateTime? reset = null;
        if (holder.TryGetProperty("resetTime", out var resetTime))
        {
            if (resetTime.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(resetTime.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed))
            {
                // 无时区标记按 UTC 处理（服务端时间均为 UTC；RoundtripKind 下带 Z 为 Utc，无标记为 Unspecified）
                reset = (parsed.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                    : parsed).ToLocalTime();
            }
            else if (resetTime.ValueKind == JsonValueKind.Number && resetTime.TryGetInt64(out var ts))
            {
                // 兼容 Unix 时间戳（>1e12 按毫秒，否则按秒）
                reset = (ts > 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ts)
                    : DateTimeOffset.FromUnixTimeSeconds(ts)).LocalDateTime;
            }
        }
        return new QuotaWindow(limit, used, percent, reset);
    }

    private static bool TryGetLong(JsonElement item, string property, out long value)
    {
        value = 0;
        if (!item.TryGetProperty(property, out var element))
            return false;
        if (element.ValueKind == JsonValueKind.Number)
        {
            // 优先整数；浮点（如 300.0）截断兜底——API 序列化差异不至于丢弃整个窗口
            if (element.TryGetInt64(out value))
                return true;
            if (element.TryGetDouble(out var d))
            {
                value = (long)d;
                return true;
            }
            return false;
        }
        if (element.ValueKind == JsonValueKind.String)
            return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        return false;
    }

    private static string FormatReset(DateTime? reset) =>
        reset is { } t ? t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    /// <summary>一个配额窗口的解析结果。</summary>
    private sealed record QuotaWindow(long Limit, long Used, decimal Percent, DateTime? ResetTime);
}
