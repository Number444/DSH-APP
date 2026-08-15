using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using dsh_app.Helpers;

namespace dsh_app.Server;

/// <summary>
/// 壳自身（dsh-app.exe）更新：检查 GitHub Releases → 下载 → SHA256 校验 → 生成更新器脚本。
/// 事件模式与 HarnessUpdater 一致（Log 透出，UI 与落盘由调用方处理）；
/// 安装动作由调用方确认后发起，下载只覆盖 <c>%LOCALAPPDATA%\dsh-app\update</c> 下的临时文件，
/// 不触碰运行中的 exe；最终覆盖/重启由独立更新器脚本（apply-update.ps1）在旧实例退出后完成。
/// </summary>
public sealed class AppUpdater : IDisposable
{
    private const string Repo = "Number444/DSH-APP";
    private const string ReleaseApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string AssetName = "dsh-app.exe";
    private const string ShaAssetName = "dsh-app.exe.sha256";

    /// <summary>检查超时（GitHub API，60s）。</summary>
    private const int CheckTimeoutMs = 60_000;
    /// <summary>下载硬超时（15 分钟：代理慢速下 148MB 约 8 分钟，留足余量）。</summary>
    private const int DownloadTimeoutMs = 900_000;
    /// <summary>下载缓冲（128KB 分块写盘 + 同遍哈希）。</summary>
    private const int BufferSize = 128 * 1024;
    /// <summary>下载前磁盘可用空间下限（峰值=新148 + 备份148 + 运行旧148 ≈ 450MB）。</summary>
    private const long MinFreeSpaceBytes = 600L * 1024 * 1024;
    /// <summary>进度回调节流（毫秒，防 Dispatcher 风暴阻塞下载线程）。</summary>
    private const int ProgressIntervalMs = 250;

    /// <summary>Release tag 格式校验（prerelease/draft 天然被 releases/latest 语义排除）。</summary>
    private static readonly Regex TagRegex = new(@"^v\d+\.\d+\.\d+$", RegexOptions.Compiled);
    /// <summary>SHA256 校验文件严格格式：64 位 hex（发布侧 Get-FileHash 只输出裸哈希，契约三处钉死：发布脚本/壳解析/文档）。</summary>
    private static readonly Regex Sha256Regex = new(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    /// <summary>串行化检查/下载，防并发（仿 HarnessUpdater）。</summary>
    private readonly SemaphoreSlim _runLock = new(1, 1);
    /// <summary>共享 HttpClient（连接池复用）；所有请求超时走 per-call CTS（实例 Timeout 不设，避免限制 15 分钟下载）。</summary>
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private bool _disposed;

    /// <summary>日志回调（与 ServerController.Log 同一通道：UI + app.log）。</summary>
    public event Action<string>? Log;

    /// <summary>本地壳版本（= App.AppVersion，构造时解析）。</summary>
    public string LocalVersion { get; } = App.AppVersion;

    /// <summary>最近一次检查到的 Release 版本（未检查为 null；404 无 Release 也为 null）。</summary>
    public string? LatestVersion { get; private set; }

    /// <summary>最近一次检查是否发现有新版（任一侧版本无法比较时不误报）。</summary>
    public bool HasUpdate { get; private set; }

    /// <summary>最近一次检查是否成功（网络失败/解析失败/发布不完整为 false；404 无 Release 视为成功无更新）。</summary>
    public bool LastCheckSucceeded { get; private set; }

    /// <summary>最近一次检查/下载的失败原因文案（null = 无；UI 提示用）。</summary>
    public string? LastError { get; private set; }

    /// <summary>最近一次检查到的 exe 下载地址（同 release）。</summary>
    public string? LatestDownloadUrl { get; private set; }

    /// <summary>最近一次检查到的 SHA256 校验文件地址（同 release）。</summary>
    public string? LatestShaUrl { get; private set; }

    /// <summary>下载校验通过后的新 exe 路径（.new；未下载为 null）。</summary>
    public string? DownloadedNewExePath { get; private set; }

    /// <summary>更新工作目录（%LOCALAPPDATA%\dsh-app\update）。</summary>
    public static string UpdateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dsh-app", "update");

    public AppUpdater()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-app-updater");
        WriteLog($"本地应用版本：{LocalVersion}");
    }

    /// <summary>
    /// 检查最新版：GET GitHub releases/latest → LatestVersion + HasUpdate。
    /// 网络策略（环境自适应）：默认 handler（直连/系统代理）；直连失败且 127.0.0.1:7890
    /// TCP 可达时经显式代理重试一次——兼容"直连可达"（当前环境 7890 失效）与"仅代理可达"两种环境。
    /// </summary>
    /// <returns>true = 检查成功（是否有新版看 HasUpdate；404 无 Release 视为成功无更新）；false = 检查失败。</returns>
    public async Task<bool> CheckAsync()
    {
        await _runLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                LastCheckSucceeded = false;
                HasUpdate = false;
                return false;
            }

            WriteLog("检查应用更新：GitHub Releases…");
            var fetch = await GetReleaseJsonAsync(useProxy: false);
            if (!fetch.Reached && await IsProxyAliveAsync())
            {
                WriteLog("直连 GitHub 失败，尝试经代理 127.0.0.1:7890…");
                fetch = await GetReleaseJsonAsync(useProxy: true);
            }
            if (!fetch.Reached)
            {
                LastError = "无法访问 GitHub Releases（网络不可用或代理异常）";
                WriteLog($"检查应用更新失败：{LastError}");
                LastCheckSucceeded = false;
                HasUpdate = false;
                return false;
            }
            if (fetch.NotFound)
            {
                WriteLog("GitHub 尚无 Release（404），视为无更新");
                LastCheckSucceeded = true;
                HasUpdate = false;
                LatestVersion = null;
                LatestDownloadUrl = null;
                LatestShaUrl = null;
                LastError = null;
                return true;
            }
            if (fetch.Doc is null)
            {
                LastError = "GitHub API 返回异常状态";
                LastCheckSucceeded = false;
                HasUpdate = false;
                return false;
            }

            using var doc = fetch.Doc;
            var root = doc.RootElement;

            // tag 校验：vX.Y.Z 严格格式，非法即检查失败（不误报）
            if (!root.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String)
            {
                WriteLog("Release 数据缺少 tag_name，判定检查失败");
                LastError = "Release 数据异常（缺少版本号）";
                LastCheckSucceeded = false;
                HasUpdate = false;
                return false;
            }
            var tag = tagEl.GetString() ?? "";
            if (!TagRegex.IsMatch(tag))
            {
                WriteLog($"Release tag 格式非法：{tag}，判定检查失败");
                LastError = "Release 版本号格式异常";
                LastCheckSucceeded = false;
                HasUpdate = false;
                return false;
            }
            var version = tag[1..]; // 去 v 前缀

            // 版本比较先行：Release 版本不高于本地 → 无需更新，资产是否完整无关紧要
            // （否则旧 Release 无资产会误报"发布不完整"，实测：v1.2.0 无资产 + 本地 1.3.0）
            if (SemVer.Compare(version, LocalVersion) <= 0)
            {
                WriteLog($"已是最新版本：v{version}（本地 v{LocalVersion}）");
                LatestVersion = version;
                LatestDownloadUrl = null;
                LatestShaUrl = null;
                LastCheckSucceeded = true;
                HasUpdate = false;
                LastError = null;
                return true;
            }

            // 有新版：资产校验——exe 与 sha256 必须同时存在（发布不完整 = fail-closed 判检查失败，不误报）
            string? exeUrl = null;
            string? shaUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (!a.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                    if (!a.TryGetProperty("browser_download_url", out var u) || u.ValueKind != JsonValueKind.String) continue;
                    var name = n.GetString();
                    if (name == AssetName) exeUrl = u.GetString();
                    else if (name == ShaAssetName) shaUrl = u.GetString();
                }
            }
            if (exeUrl is null || shaUrl is null)
            {
                WriteLog($"Release {tag} 资产不完整（exe/sha256 缺失），判定检查失败");
                LastError = "发布不完整（缺少更新资产）";
                LastCheckSucceeded = false;
                HasUpdate = false;
                return false;
            }

            LatestVersion = version;
            LatestDownloadUrl = exeUrl;
            LatestShaUrl = shaUrl;
            LastCheckSucceeded = true;
            LastError = null;
            HasUpdate = true;
            WriteLog($"发现新版：本地 v{LocalVersion} → 最新 v{version}");
            return true;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>
    /// 下载新 exe（流式 + 同遍 SHA256）并比对同 release 的 .sha256 校验文件。
    /// 校验通过后 .part → .new；失败/取消即删 .part（fail-closed：不安装未校验文件）。
    /// 磁盘预检：可用空间 ≥ 600MB，不足快速失败。下载不打断运行中的服务。
    /// </summary>
    /// <returns>true = 下载并校验通过（新 exe 在 DownloadedNewExePath）；false = 失败/取消（LastError 有因）。</returns>
    public async Task<bool> DownloadAndVerifyAsync(Action<long, long>? progress, CancellationToken ct)
    {
        await _runLock.WaitAsync();
        try
        {
            if (_disposed) return false;
            if (LatestDownloadUrl is null || LatestShaUrl is null || LatestVersion is null)
            {
                LastError = "尚未检查到可用更新";
                return false;
            }

            var dir = UpdateDir;
            Directory.CreateDirectory(dir);

            // 磁盘预检（失败不阻断，下载时兜底）
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(dir)!);
                if (drive.IsReady && drive.AvailableFreeSpace < MinFreeSpaceBytes)
                {
                    WriteLog($"磁盘空间不足（剩余 {drive.AvailableFreeSpace / 1048576.0:F0} MB，需 ≥ 600 MB），取消下载");
                    LastError = "磁盘空间不足";
                    return false;
                }
            }
            catch { /* 忽略预检异常 */ }

            var partPath = Path.Combine(dir, $"dsh-app.{LatestVersion}.part");
            var newPath = Path.Combine(dir, $"dsh-app.{LatestVersion}.new");
            DeleteQuietly(partPath);
            DeleteQuietly(newPath);

            // ① 下载 exe（同遍哈希，零二次读盘）
            string? hash;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(DownloadTimeoutMs);
                hash = await DownloadToFileAsync(LatestDownloadUrl, partPath, progress, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                WriteLog("下载应用更新超时（15 分钟），已取消");
                DeleteQuietly(partPath);
                LastError = "下载超时";
                return false;
            }
            catch (OperationCanceledException)
            {
                WriteLog("下载已取消");
                DeleteQuietly(partPath);
                return false;
            }
            catch (Exception ex)
            {
                WriteLog($"下载失败：{ex.Message}");
                DeleteQuietly(partPath);
                LastError = "下载失败";
                return false;
            }
            if (hash is null)
            {
                DeleteQuietly(partPath);
                return false;
            }

            // ② 下载 .sha256 并严格比对
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(30_000);
                var expected = await DownloadTextAsync(LatestShaUrl, cts.Token);
                expected = expected?.Trim();
                if (expected is null || !Sha256Regex.IsMatch(expected))
                {
                    WriteLog("校验文件非法（非 64 位 hex），拒绝安装");
                    DeleteQuietly(partPath);
                    LastError = "校验文件异常";
                    return false;
                }
                if (!string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
                {
                    WriteLog($"SHA256 校验失败：期望 {expected}，实际 {hash}，拒绝安装");
                    DeleteQuietly(partPath);
                    LastError = "文件校验失败";
                    return false;
                }
            }
            catch (Exception ex)
            {
                WriteLog($"校验文件下载失败：{ex.Message}");
                DeleteQuietly(partPath);
                LastError = "校验文件下载失败";
                return false;
            }

            File.Move(partPath, newPath);
            DownloadedNewExePath = newPath;
            WriteLog($"下载并校验通过：dsh-app.{LatestVersion}.new（{hash[..12]}…）");
            return true;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>
    /// 生成更新器脚本（从嵌入资源提取 apply-update.ps1 模板写盘，参数内联渲染）。
    /// 由调用方在下载校验通过后调用，随后启动该脚本并退出应用。
    /// </summary>
    /// <param name="port">当前服务端口（更新器启动验证用，新实例将接管同端口）。</param>
    /// <returns>脚本路径；失败返回 null（LastError 有因）。</returns>
    public string? WriteUpdateScript(int port)
    {
        try
        {
            var dir = UpdateDir;
            Directory.CreateDirectory(dir);
            var scriptPath = Path.Combine(dir, "apply-update.ps1");

            var template = ReadEmbeddedTemplate();
            if (template is null)
            {
                LastError = "更新器模板缺失（程序集内嵌资源损坏）";
                WriteLog(LastError);
                return null;
            }

            // 参数注入到模板的 param 块之后（标记位替换）：
            // 必须在 $procName = $WaitProcessName 之前生效，否则注入值来不及使用
            var backupDir = Path.Combine(dir, "backup");
            var logFile = Path.Combine(dir, "updater.log");
            var newExe = DownloadedNewExePath ?? "";
            var appExe = Environment.ProcessPath ?? "";

            var assignments =
                  "# ---- injected by AppUpdater ----" + Environment.NewLine
                + $"$NewExe = '{EscapeSingleQuote(newExe)}'" + Environment.NewLine
                + $"$AppExe = '{EscapeSingleQuote(appExe)}'" + Environment.NewLine
                + $"$BackupDir = '{EscapeSingleQuote(backupDir)}'" + Environment.NewLine
                + $"$LogFile = '{EscapeSingleQuote(logFile)}'" + Environment.NewLine
                + $"$Port = {port}" + Environment.NewLine;

            const string injectMarker = "# __INJECT_PARAMS_HERE__";
            if (!template.Contains(injectMarker))
            {
                LastError = "更新器模板缺少注入标记";
                WriteLog(LastError);
                return null;
            }
            var injected = template.Replace(injectMarker, assignments.TrimEnd());

            // UTF-8 带 BOM 写盘：Windows PowerShell 5.1 对无 BOM 的 UTF-8 按 ANSI 解析，
            // 中文日志会因 GBK 误读导致语法错误（实测踩坑）
            File.WriteAllText(scriptPath, injected, new System.Text.UTF8Encoding(true));
            WriteLog($"更新器脚本已生成：{scriptPath}");
            return scriptPath;
        }
        catch (Exception ex)
        {
            LastError = $"生成更新脚本失败：{ex.Message}";
            WriteLog(LastError);
            return null;
        }
    }

    /// <summary>从嵌入资源读取更新器模板（scripts/apply-update.ps1，与仓库文件保持同步）。</summary>
    private static string? ReadEmbeddedTemplate()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("dsh_app.Scripts.apply-update.ps1");
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>单引号转义（PowerShell 字符串字面量内）。</summary>
    private static string EscapeSingleQuote(string s) => s.Replace("'", "''");

    /// <summary>流式下载到文件并同遍计算 SHA256；进度经委托节流上报。</summary>
    private async Task<string?> DownloadToFileAsync(string url, string path,
        Action<long, long>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            WriteLog($"下载 HTTP {(int)resp.StatusCode}");
            return null;
        }
        var total = resp.Content.Headers.ContentLength ?? -1;
        using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[BufferSize];
        long read = 0;
        var lastProgress = DateTime.UtcNow;
        var lastLog = DateTime.UtcNow;
        while (true)
        {
            var n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n == 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            hash.AppendData(buf, 0, n);
            read += n;
            var now = DateTime.UtcNow;
            if (progress is not null && (now - lastProgress).TotalMilliseconds >= ProgressIntervalMs)
            {
                lastProgress = now;
                progress(read, total);
            }
            if ((now - lastLog).TotalMilliseconds >= 10_000)
            {
                lastLog = now;
                WriteLog(total > 0
                    ? $"下载进度：{read / 1048576.0:F1} / {total / 1048576.0:F0} MB（{read * 100 / total}%）"
                    : $"下载进度：{read / 1048576.0:F1} MB");
            }
        }
        await dst.FlushAsync(ct);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>下载小文本（.sha256 校验文件）。</summary>
    private async Task<string?> DownloadTextAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            WriteLog($"校验文件下载 HTTP {(int)resp.StatusCode}");
            return null;
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>检查请求执行：直连或显式代理（代理重试专用 handler，用完即弃，低频可接受）。</summary>
    private async Task<ReleaseFetch> GetReleaseJsonAsync(bool useProxy)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CheckTimeoutMs));
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = useProxy
                ? await SendViaProxyAsync(req, cts.Token)
                : await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new ReleaseFetch(true, true, null);
            if (!resp.IsSuccessStatusCode)
            {
                WriteLog($"GitHub API HTTP {(int)resp.StatusCode}");
                return new ReleaseFetch(true, false, null);
            }
            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cts.Token),
                cancellationToken: cts.Token);
            return new ReleaseFetch(true, false, doc);
        }
        catch (OperationCanceledException)
        {
            WriteLog("访问 GitHub 超时（60s）");
            return new ReleaseFetch(false, false, null);
        }
        catch (Exception ex)
        {
            WriteLog($"访问 GitHub 失败：{ex.Message}");
            return new ReleaseFetch(false, false, null);
        }
    }

    /// <summary>经显式代理 127.0.0.1:7890 发送（检查重试路径）。</summary>
    private static async Task<HttpResponseMessage> SendViaProxyAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy("127.0.0.1", 7890),
            UseProxy = true,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        return await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>127.0.0.1:7890 是否可连通（代理重试前置条件）。</summary>
    private static async Task<bool> IsProxyAliveAsync()
    {
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", 7890);
            var done = await Task.WhenAny(connect, Task.Delay(1500));
            if (done != connect) return false;
            await connect;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>带时间戳写日志（与 ServerController.WriteLog 同格式）。</summary>
    private void WriteLog(string message) => Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>静默删除（失败容忍，残留由下次启动清理兜底）。</summary>
    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    /// <summary>
    /// 释放资源：仅释放信号量与 HttpClient；不取消在途下载的职责在调用方
    /// （下载任务内 catch ObjectDisposedException 即可——SemaphoreSlim 不 Dispose，
    /// 仿 HarnessUpdater 置 _disposed 标志复查）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    /// <summary>检查请求结果：Reached=网络是否访问成功；NotFound=仓库尚无 Release；Doc=解析后的 JSON。</summary>
    private sealed record ReleaseFetch(bool Reached, bool NotFound, JsonDocument? Doc);
}
