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

    /// <summary>
    /// 启动早期调用：日志文件超阈值（2MB）时截断、保留尾部约 500KB。
    /// 必须在首次 Append 之前调用（此时 FlushLoop 尚未启动，无写盘竞争）；
    /// 内存尾缓冲 Tail 不受影响。截断失败静默，不影响启动。
    /// </summary>
    public static void TrimIfOversize()
    {
        const long MaxBytes = 2L * 1024 * 1024;
        const int KeepBytes = 500 * 1024;
        lock (Sync)
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var len = new FileInfo(FilePath).Length;
                if (len <= MaxBytes) return;

                byte[] tail;
                using (var rd = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    rd.Seek(-KeepBytes, SeekOrigin.End);
                    tail = new byte[KeepBytes];
                    var read = 0;
                    while (read < KeepBytes)
                    {
                        var n = rd.Read(tail, read, KeepBytes - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < tail.Length) Array.Resize(ref tail, read);
                }

                // 丢弃首条残行：定位第一个 '\n'（UTF-8 中 0x0A 不会出现在多字节序列内，字节级查找安全）
                var nl = Array.IndexOf(tail, (byte)'\n');
                var body = nl >= 0 && nl + 1 < tail.Length ? tail[(nl + 1)..] : tail;

                using (var wr = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bom = new UTF8Encoding(true).GetPreamble();
                    wr.Write(bom, 0, bom.Length);
                    wr.Write(body, 0, body.Length);
                }
                _bomEnsured = true;

                Append($"[FileLog] app.log 超过 2MB，已截断保留末尾约 500KB（原大小 {len / 1024.0 / 1024:F1}MB）");
            }
            catch
            {
                // 截断失败不影响启动
            }
        }
    }

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
            while (true)
            {
                // drain 到空：执行期间的新条目也会被取走，不会遗留
                while (Queue.TryDequeue(out var line))
                    batch.Add(line);
                if (batch.Count > 0)
                {
                    File.AppendAllLines(FilePath, batch, new UTF8Encoding(false));
                    batch.Clear();
                }
                // 退出条件必须在锁内与置空原子完成：否则"判空后、退出前"新入队的条目
                // 会滞留到下次 Append 才落盘（应用退出时丢最后几行日志）
                lock (Sync)
                {
                    if (Queue.IsEmpty)
                    {
                        _flushTask = null;
                        return;
                    }
                }
            }
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
