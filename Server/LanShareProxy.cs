using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace dsh_app.Server;

/// <summary>
/// 局域网共享代理（LAN Share）：Kestrel 迷你宿主 + YARP 反向代理。
/// 监听 0.0.0.0:{LanPort}，把流量转发到本机 dsh 服务 127.0.0.1:{Port}：
/// - token 门禁：?key= 首次校验通过后种 Cookie，后续请求（含 WebSocket 握手）凭 Cookie 放行；
/// - Host 头重写为 127.0.0.1:{Port}：使请求通过 harness 的 browser-trust 围栏（按本机对待）；
/// - 特权方法 LAN 侧拦截：settings/credentials/agentPreset/host.pickDirectory 等写操作仍 403
///   （Host 重写会让 harness 视为完全本地，官方"钉死 loopback"的底线由本层代为保留）；
/// - HTTP / SSE / WebSocket 由 YARP 全透传（含 300MB 大附件上传）。
/// 生命周期归主窗口：设置开关即启停，服务重启后重同步目标端口，壳退出时 Dispose 停服。
/// </summary>
public sealed class LanShareProxy : IDisposable
{
    private const string CookieName = "dsh_lan_key";
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// LAN 侧拦截清单：只拦写操作与宿主原生动作——token 门禁已提供鉴权，只读方法
    /// （settings.describe / credentials.describe / agentPreset.read）是 GUI 初始化的命脉，
    /// 拦截会导致远端界面大片"加载中/报错"（v1 实测教训）。
    /// 保留拦截的语义：改你电脑的设置/凭据/预设、弹本机原生对话框、让宿主代发请求探测内网——
    /// 这些即使持有 token 也不该从远端触发。
    /// </summary>
    private static readonly HashSet<string> PrivilegedMethods = new(StringComparer.Ordinal)
    {
        "agentPreset.copy", "agentPreset.openDocument", "agentPreset.remove",
        "host.pickDirectory", "host.openPath",
        "settings.openDocument", "settings.update", "settings.replace", "settings.mutate",
        "credentials.set", "credentials.unset",
        "llm.discoverModels",
    };

    /// <summary>
    /// 注入 index.html 的 polyfill：harness 前端 RPC 客户端（dsh-client-connection/client.js）用
    /// crypto.randomUUID 生成请求 ID，而该 API 仅限安全上下文（HTTPS/localhost）——手机走
    /// http://192.168.x.x 时它不存在，所有 /api 调用当场抛 "crypto.randomUUID is not a function"
    /// （页面能开、会话/设置全空）。getRandomValues 不受安全上下文限制，用它补出合规 v4 UUID。
    /// </summary>
    private const string SecureContextPolyfill =
        "<script>if(self.crypto&&!crypto.randomUUID)crypto.randomUUID=function(){return([1e7]+-1e3+-4e3+-8e3+-1e11)" +
        ".replace(/[018]/g,function(c){return(c^crypto.getRandomValues(new Uint8Array(1))[0]&15>>c/4).toString(16)})};</script>";

    private WebApplication? _app;

    /// <summary>运行日志回调（UI 状态区 + 日志文件）。</summary>
    public event Action<string>? Log;

    /// <summary>启停状态变化（托盘悬停文案联动）。</summary>
    public event Action? StateChanged;

    /// <summary>当前监听端口（0 = 未运行）。</summary>
    public int ActivePort { get; private set; }

    public bool IsRunning => _app is not null;

    private void WriteLog(string message) => Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>
    /// 启动（或重启）代理：先停旧实例再绑新端口。目标端口随 dsh 服务实际端口（3080~3090）。
    /// 绑定失败（端口被占等）返回 false，日志有详情；不产生半运行状态。
    /// </summary>
    public async Task<bool> StartAsync(int listenPort, int targetPort, string token)
    {
        await StopAsync();
        try
        {
            var builder = WebApplication.CreateBuilder();
            // 静默：Kestrel/Host 的 console 日志与启动横幅全部关掉（壳是 GUI 进程，输出走自己的 Log 通道）
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Any, listenPort));

            var target = $"http://127.0.0.1:{targetPort}/";
            builder.Services.AddReverseProxy().LoadFromMemory(
                [
                    new RouteConfig
                    {
                        RouteId = "lan-share",
                        ClusterId = "dsh",
                        Match = new RouteMatch { Path = "/{**catchall}" },
                        // Host 重写 → 通过 harness browser-trust 围栏（等价于 --trusted-host 的效果，但无需改 dsh 启动参数）；
                        // Origin 同步重写：围栏要求 Origin.host === Host.host（isTrustedApiRequest），
                        // 浏览器 POST/WS 会带 LAN 地址的 Origin，不改写必 403
                        Transforms =
                        [
                            new Dictionary<string, string> { ["RequestHeader"] = "Host", ["Set"] = $"127.0.0.1:{targetPort}" },
                            new Dictionary<string, string> { ["RequestHeader"] = "Origin", ["Set"] = $"http://127.0.0.1:{targetPort}" },
                            // 关掉压缩协商：polyfill 注入需要对 HTML 做字符串替换，不能让上游返回 gzip/br
                            new Dictionary<string, string> { ["RequestHeader"] = "Accept-Encoding", ["Set"] = "identity" },
                        ],
                    },
                ],
                [
                    new ClusterConfig
                    {
                        ClusterId = "dsh",
                        Destinations = new Dictionary<string, DestinationConfig> { ["d1"] = new() { Address = target } },
                    },
                ])
                .AddTransforms(transforms =>
                {
                    transforms.AddResponseTransform(async ctx =>
                    {
                        // 仅改写 HTML 文档（SPA 入口）：注入 randomUUID polyfill 后自己写响应体
                        var proxyResp = ctx.ProxyResponse;
                        if (proxyResp?.Content.Headers.ContentType?.MediaType != "text/html") return;
                        var html = await proxyResp.Content.ReadAsStringAsync();
                        var idx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) return;
                        ctx.SuppressResponseBody = true;
                        // 体长变了：掐掉 Content-Length（变换时已复制到响应头），Kestrel 按实际体重算
                        ctx.HttpContext.Response.ContentLength = null;
                        proxyResp.Content.Headers.ContentLength = null;
                        await ctx.HttpContext.Response.WriteAsync(html.Insert(idx + 6, SecureContextPolyfill));
                    });
                });

            var app = builder.Build();

            app.Use(async (ctx, next) =>
            {
                // 特权方法拦截（先于门禁：未授权设备得到同样的 403，不泄露"方法存在性"之外的差异）
                var path = ctx.Request.Path.Value ?? "";
                if (path.StartsWith("/api/", StringComparison.Ordinal)
                    && PrivilegedMethods.Contains(path[5..]))
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsync("该操作仅限本机。");
                    return;
                }

                // token 门禁：Cookie 命中直接放行；?key= 命中则种 Cookie 后跳转到干净地址
                if (KeyEquals(ctx.Request.Cookies[CookieName], token))
                {
                    await next();
                    return;
                }
                if (ctx.Request.Query.TryGetValue("key", out var key) && KeyEquals(key.ToString(), token))
                {
                    ctx.Response.Cookies.Append(CookieName, token,
                        new CookieOptions { Path = "/", Expires = DateTimeOffset.UtcNow.AddYears(1) });
                    ctx.Response.Redirect(ctx.Request.Path.Value ?? "/", permanent: false);
                    return;
                }

                ctx.Response.StatusCode = 403;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(
                    "<html><body style=\"font-family:sans-serif;background:#0d1117;color:#e6edf3;" +
                    "display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
                    "<div style=\"text-align:center\"><h2>需要访问密钥</h2>" +
                    "<p>请使用本机 dsh-app 设置页「局域网共享」中的完整访问地址。</p></div></body></html>");
            });
            app.MapReverseProxy();

            // 先挂到字段再 StartAsync：绑定失败（端口被占）时 catch 里的 StopAsync 才能释放该实例，不泄漏
            _app = app;
            await app.StartAsync();
            ActivePort = listenPort;
            WriteLog($"LAN 共享已启动：0.0.0.0:{listenPort} → 127.0.0.1:{targetPort}");
            StateChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            WriteLog($"LAN 共享启动失败（端口 {listenPort}）：{ex.Message}");
            await StopAsync(); // 清理半启动状态（_app 已提前挂字段，此处负责释放，幂等）
            return false;
        }
    }

    /// <summary>停止代理（幂等）；Kestrel 关闭限时 2s，不拖住壳退出。</summary>
    public async Task StopAsync()
    {
        var app = _app;
        if (app is null) return;
        _app = null;
        ActivePort = 0;
        try
        {
            using var cts = new CancellationTokenSource(ShutdownTimeout);
            await app.StopAsync(cts.Token);
            await app.DisposeAsync();
            WriteLog("LAN 共享已停止");
        }
        catch
        {
            // 停止失败静默：进程退出在即
        }
        StateChanged?.Invoke();
    }

    /// <summary>访问密钥常量时间比较（防时序侧信道；两边等长才比较）。</summary>
    private static bool KeyEquals(string? a, string b)
        => a is not null
           && a.Length == b.Length
           && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    /// <summary>
    /// 枚举本机内网 IPv4 地址（仅私有网段，排除回环/链路本地；192.168 优先——家用路由最常见，
    /// 虚拟网卡（VMware/WSL 多为 172/10 段）自然靠后）。无网卡/异常时返回空表。
    /// </summary>
    public static IReadOnlyList<string> GetLanIPv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && IsPrivateIPv4(ip))
                .Select(ip => ip.ToString())
                .OrderBy(s => s.StartsWith("192.168.", StringComparison.Ordinal) ? 0 : 1)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsPrivateIPv4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 172 && b[1] is >= 16 and <= 31);
    }

    /// <summary>壳退出调用：同步等待停服（限 3s）。StopAsync 内部 await 会捕获 WPF 同步上下文，
    /// 直接在 UI 线程 .Wait 会死锁 → 扔到线程池执行再等待（池线程无同步上下文，续体直接完成）。</summary>
    public void Dispose()
    {
        try
        {
            Task.Run(StopAsync).Wait(ShutdownTimeout + TimeSpan.FromSeconds(1));
        }
        catch
        {
            // 退出路径静默
        }
    }
}
