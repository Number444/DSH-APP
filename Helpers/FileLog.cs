using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace dsh_app.Helpers;

/// <summary>
/// 统一日志写入（%LOCALAPPDATA%\dsh-app\app.log）：
/// - 线程安全：任意线程可 Append（App 异常路径与 MainWindow UI 线程共用同一锁与队列，
///   消除"双写者并发 IOException 丢行"）；
/// - 异步落盘：入队后后台线程批量写，UI 线程零磁盘 IO（日志洪峰不卡 UI）；
/// - 内存尾缓冲：TailText 供启动覆盖层/失败详情展示。
/// </summary>
public static class FileLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dsh-app", "app.log");

    private static readonly object Sync = new();
    private static readonly StringBuilder Tail = new(4096);
    private static readonly ConcurrentQueue<string> Queue = new();
    private static Task? _flushTask;
    private static bool _bomEnsured;

    /// <summary>日志文件路径（供"打开日志"等直接引用）。</summary>
    public static string LogFilePath => FilePath;

    /// <summary>追加一行日志（任意线程；立即入内存尾缓冲，落盘异步批量）。</summary>
    public static void Append(string message)
    {
        lock (Sync)
        {
            Tail.AppendLine(message);
            if (Tail.Length > 8_000)
                Tail.Remove(0, Tail.Length - 8_000);
        }
        Queue.Enqueue(message);
        StartFlush();
    }

    /// <summary>最近日志文本快照（供覆盖层/失败详情；线程安全）。</summary>
    public static string TailText
    {
        get
        {
            lock (Sync)
                return Tail.ToString();
        }
    }

    private static void StartFlush()
    {
        lock (Sync)
        {
            // 已有运行中的 FlushLoop 会继续 drain 新条目；仅在其结束后重启
            if (_flushTask is null || _flushTask.IsCompleted)
                _flushTask = Task.Run(FlushLoop);
        }
    }

    private static void FlushLoop()
    {
        try
        {
            EnsureBom();
            var batch = new List<string>(64);
            // drain 到空：执行期间的新条目也会被取走，不会遗留
            while (Queue.TryDequeue(out var line))
                batch.Add(line);
            if (batch.Count > 0)
                File.AppendAllLines(FilePath, batch, new UTF8Encoding(false));
        }
        catch
        {
            // 写盘失败不影响主流程；队列条目丢弃（内存尾缓冲仍在）
        }
    }

    private static void EnsureBom()
    {
        if (_bomEnsured) return;
        if (!File.Exists(FilePath))
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, "", new UTF8Encoding(true));
        }
        _bomEnsured = true;
    }
}
