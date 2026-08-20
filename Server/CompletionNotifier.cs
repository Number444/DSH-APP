using System.IO;
using System.Net.WebSockets;
using System.Text.Json;

namespace dsh_app.Server;

/// <summary>
/// 会话完成通知：直连 dsh 服务的 WebSocket 事件流（/api/events.host），检测会话 running→idle
/// 边沿后回调（由 MainWindow 弹系统通知）。相比页面内插件：窗口托盘化/页面挂起期间照常工作
/// （事件流在壳进程，不依赖 WebView2 渲染进程）。
///
/// 协议锚点（dsh node_modules 实地查证）：
/// - dsh-client-connection：/api/events.host 只接受 WebSocket upgrade（普通 GET 返回 426，无 SSE 回退）；
///   下行帧为 ServerRequest 信封 {type:"server-request", rpcId, method, payload}，只取 payload 的 host 帧；
///   客户端发消息属协议违规（被 1008 关闭）——本类只收不发；本机 loopback + 不带 Origin 即过信任栅栏
/// - dsh-host-apiproxy hostFrameSchema：host/session-status{sessionId,running} 等；
///   agent/status 仅两值（idle/running）且严格交替（重复状态是 host 硬断言），流只发真实翻转、无初始快照
///
/// 宽容解析：坏帧/未知帧跳过不杀流（与官方客户端 readSse 同一策略）；断流指数退避重连（2s/5s/15s 封顶）。
/// 生命周期由 MainWindow 管理：服务就绪后 Start(port)，设置开关关闭时 Stop()，窗口销毁时 Dispose()。
/// </summary>
public sealed class CompletionNotifier : IDisposable
{
    /// <summary>重连退避档位（2s → 5s → 15s 封顶）。</summary>
    private static readonly TimeSpan[] BackoffSteps =
        { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) };

    /// <summary>WebSocket 握手超时（10s；正常重连由退避控制节奏）。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>单条消息接收缓冲（16KB；分片消息累计到 EndOfMessage 再解析）。</summary>
    private const int ReceiveBufferSize = 16 * 1024;

    private readonly object _sync = new();

    /// <summary>running 边沿表：会话 → 最近一次记录的运行态（仅两类帧维护：status / session-removed）。</summary>
    private readonly Dictionary<string, bool> _lastRunning = new();

    /// <summary>运行中会话的最近错误（host/agent-error）：完成帧到达时换"出错"文案；新一轮 running 时清除。</summary>
    private readonly Dictionary<string, string> _pendingError = new();

    /// <summary>子代理会话（host/session-added 的 origin=="subagent"）：完成不弹通知（后台任务非用户主动发起）。</summary>
    private readonly HashSet<string> _muted = new();

    private CancellationTokenSource? _cts;
    private ClientWebSocket? _socket;
    private int _started;
    private bool _disposed;
    /// <summary>已停止标志（Stop/Dispose 后置真；在途帧回调时忽略）。Volatile 访问（同 BalanceMonitor 惯例）。</summary>
    private bool _stopped = true;
    private int _port;

    /// <summary>日志（事件流无凭据；异常消息不含敏感内容）。</summary>
    public event Action<string>? Log;

    /// <summary>
    /// 会话运行结束回调：sessionId 全文 + 错误消息（null = 正常完成）。
    /// 触发即"进入 idle"边沿（含壳启动前就在运行、连接后只见收尾帧的会话）；调用方负责上 UI 线程与展示。
    /// </summary>
    public event Action<string, string?>? SessionFinished;

    /// <summary>启动事件流监听。幂等：已启动不重复。</summary>
    public void Start(int port)
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
            _port = port;
            _lastRunning.Clear();
            _pendingError.Clear();
            _muted.Clear();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    /// <summary>停止监听：中断在途接收并取消重连；后台循环自行收尾（不等待，避免 UI 线程阻塞）。</summary>
    public void Stop()
    {
        lock (_sync)
        {
            Volatile.Write(ref _stopped, true);
            _socket?.Abort(); // 立即中断 ReceiveAsync（Cancel 之外的双保险）
            _cts?.Cancel();
        }
        Interlocked.Exchange(ref _started, 0); // 允许再次 Start
    }

    public void Dispose()
    {
        lock (_sync) _disposed = true;
        Stop();
        // _cts 不显式 Dispose：后台循环可能仍持有其 Token（Task.Delay 注册），交给 GC 随循环收尾回收
    }

    // ---- 内部实现 ----

    /// <summary>连接主循环：连接 → 收帧 → 断流退避重连，直到取消。</summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var gotFrame = await ConnectAndReceiveAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                // 收到过帧的连接按健康计：断开后立即重连（退避归零）
                failures = gotFrame ? 0 : failures + 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                failures++;
                // 前 3 次与之后每 10 次记一条日志：可读且不刷屏（服务长期关闭时不轰炸日志）
                if (failures <= 3 || failures % 10 == 0)
                    Log?.Invoke($"完成通知事件流重连中（第 {failures} 次）：{ex.Message}");
            }

            var delay = BackoffSteps[Math.Min(Math.Max(failures, 1), BackoffSteps.Length) - 1];
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>建连并收帧直到断流；返回本次连接是否健康（收到过帧，或持续存活超 30s——
    /// 长时间存活后断开属正常网络波动，下次重连不应被退避惩罚）。</summary>
    private async Task<bool> ConnectAndReceiveAsync(CancellationToken ct)
    {
        var connectedAt = Environment.TickCount64;
        using var ws = new ClientWebSocket();
        // 本机回环：显式禁用系统代理（Clash 全局代理可能劫持握手，loopback 也不保证绕过）
        ws.Options.Proxy = null;

        lock (_sync)
        {
            if (Volatile.Read(ref _stopped)) return false;
            _socket = ws;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/api/events.host"), timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // 握手失败：摘掉 socket 引用后交外层退避重连
            lock (_sync)
            {
                if (ReferenceEquals(_socket, ws)) _socket = null;
            }
            throw;
        }

        try
        {
            var gotAny = await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
            return gotAny || Environment.TickCount64 - connectedAt > 30_000;
        }
        finally
        {
            // 断流/收尾：摘掉当前 socket 引用（Stop 的 Abort 幂等，重复无碍）
            lock (_sync)
            {
                if (ReferenceEquals(_socket, ws)) _socket = null;
            }
        }
    }

    /// <summary>收帧循环：累计分片到完整消息再解析；Close 帧/断流返回（由外层退避重连）。</summary>
    private async Task<bool> ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        var gotAny = false;
        while (!ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // 收到服务端 Close：按 RFC 6455 回一个 Close 完成握手（失败无碍，Dispose/Abort 兜底）
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* 尽力而为 */ }
                    return gotAny; // 交外层重连
                }
                ms.Write(buffer.AsSpan(0, result.Count));
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue; // 二进制帧不属于本协议，跳过

            gotAny = true;
            ProcessMessage(ms.GetBuffer().AsMemory(0, (int)ms.Length));
        }
        return gotAny;
    }

    /// <summary>解析一条下行消息（宽容：任何解析失败/未知帧只跳过，不杀流）。</summary>
    private void ProcessMessage(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8Json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            // 信封 {type:"server-request", rpcId, method, payload}：只取 payload 的 host 帧
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return;
            if (!payload.TryGetProperty("type", out var typeEl)) return;

            switch (typeEl.GetString())
            {
                case "host/session-status":
                    if (payload.TryGetProperty("sessionId", out var sidEl)
                        && sidEl.GetString() is { Length: > 0 } sid)
                    {
                        var running = payload.TryGetProperty("running", out var rEl)
                                      && rEl.ValueKind == JsonValueKind.True;
                        OnSessionStatus(sid, running);
                    }
                    break;

                case "host/agent-error":
                    if (payload.TryGetProperty("sessionId", out var eidEl)
                        && eidEl.GetString() is { Length: > 0 } eid
                        && payload.TryGetProperty("message", out var msgEl)
                        && msgEl.GetString() is { Length: > 0 } msg)
                    {
                        OnAgentError(eid, msg);
                    }
                    break;

                case "host/session-added":
                    // 子代理会话静音：后台任务完成不弹通知（非用户主动发起，刷屏）
                    if (payload.TryGetProperty("sessionId", out var aidEl)
                        && aidEl.GetString() is { Length: > 0 } aid
                        && payload.TryGetProperty("origin", out var originEl)
                        && originEl.GetString() == "subagent")
                    {
                        lock (_sync) _muted.Add(aid);
                    }
                    break;

                case "host/session-removed":
                    if (payload.TryGetProperty("sessionId", out var ridEl)
                        && ridEl.GetString() is { Length: > 0 } rid)
                    {
                        lock (_sync)
                        {
                            _lastRunning.Remove(rid);
                            _pendingError.Remove(rid);
                            _muted.Remove(rid);
                        }
                    }
                    break;

                // 其余帧（workspace-* / stream/error / 未知类型）：与本功能无关，跳过
            }
        }
        catch (Exception ex)
        {
            // 坏帧跳过不杀流（JsonException 的消息只含位置信息，无敏感内容）
            Log?.Invoke($"完成通知事件流坏帧已跳过：{ex.Message}");
        }
    }

    /// <summary>running 边沿判定：false 帧恒为一次真实运行的结束（host 侧严格交替、只发翻转）——
    /// 连接前就在运行的会话只见收尾帧，同样要通知；仅已记录 false 的重复帧跳过。</summary>
    private void OnSessionStatus(string sessionId, bool running)
    {
        string? error = null;
        var finished = false;
        lock (_sync)
        {
            if (running)
            {
                _lastRunning[sessionId] = true;
                _pendingError.Remove(sessionId); // 新一轮运行：清掉旧错误标记
                return;
            }
            if (_lastRunning.TryGetValue(sessionId, out var prev) && !prev)
                return; // 防御：已记录 idle 的重复帧
            _lastRunning[sessionId] = false;
            if (_muted.Contains(sessionId))
                return; // 子代理会话：记录状态但不通知
            finished = true;
            _pendingError.Remove(sessionId, out error);
        }
        if (finished && !Volatile.Read(ref _stopped))
            SessionFinished?.Invoke(sessionId, error);
    }

    /// <summary>记录运行中会话的最近错误（完成帧到达时换文案；已 idle 的迟到错误不挂账）。</summary>
    private void OnAgentError(string sessionId, string message)
    {
        lock (_sync)
        {
            if (_lastRunning.TryGetValue(sessionId, out var r) && r)
                _pendingError[sessionId] = message.Length <= 200 ? message : message[..200];
        }
    }
}
