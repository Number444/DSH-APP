using System.IO;

namespace dsh_app.Helpers;

/// <summary>
/// WebView2 页面缓存清理：运行中缓存目录被 Runtime 锁定，采用"标记 + 下次启动清"——
/// 设置页按钮只写标记文件（clear-cache.flag），App.OnStartup 在 WebView2 初始化前执行清理。
/// 只清缓存子目录（Cache / Code Cache / GPUCache，按目录名递归匹配），
/// 不动 Cookies / Local Storage，保住页面登录态与网站设置。清理失败不影响启动。
/// </summary>
public static class WebView2CacheCleaner
{
    /// <summary>应用数据目录（%LOCALAPPDATA%\dsh-app；供"打开数据目录"等直接引用）。</summary>
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dsh-app");

    private static readonly string WebView2Dir = Path.Combine(AppDataDir, "WebView2");
    private static readonly string FlagPath = Path.Combine(AppDataDir, "clear-cache.flag");

    /// <summary>视为缓存的目录名（WebView2 UDF 内 EBWebView\Default\ 等任意深度命中即清）。</summary>
    private static readonly string[] CacheDirNames = { "Cache", "Code Cache", "GPUCache" };

    /// <summary>是否已标记待清理（设置页按钮状态回显）。</summary>
    public static bool IsMarked => File.Exists(FlagPath);

    /// <summary>写入清理标记（下次启动时执行；内容为标记时间，仅供排查）。</summary>
    public static void MarkForCleanup()
    {
        Directory.CreateDirectory(AppDataDir);
        File.WriteAllText(FlagPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    /// <summary>启动早期调用（单实例判定之后、WebView2 初始化之前）：有标记则清缓存，最后删除标记。</summary>
    public static void RunPendingCleanup()
    {
        if (!File.Exists(FlagPath)) return;
        try
        {
            var deleted = 0;
            if (Directory.Exists(WebView2Dir))
            {
                // 先快照再删：边枚举边 Delete 会让枚举器抛 IOException
                foreach (var dir in Directory.GetDirectories(WebView2Dir, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(dir);
                    if (!CacheDirNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        deleted++;
                    }
                    catch
                    {
                        // 单个目录删除失败继续清其余的
                    }
                }
            }
            FileLog.Append($"[Cache] 已清理 WebView2 页面缓存（{deleted} 个缓存目录）");
        }
        catch (Exception ex)
        {
            FileLog.Append($"[Cache] 清理页面缓存失败：{ex.Message}");
        }
        finally
        {
            try { File.Delete(FlagPath); } catch { /* 标记删除失败下次重清，无害 */ }
        }
    }
}
