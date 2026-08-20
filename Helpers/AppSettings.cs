using System.IO;
using System.Text.Json;

namespace dsh_app.Helpers;

/// <summary>
/// 应用设置：主题模式、更新机制、余额显示等。
/// 持久化到 %LOCALAPPDATA%\dsh-app\settings.json，全应用共享
/// （ThemeManager / HarnessUpdater / BalanceMonitor）。
/// </summary>
public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dsh-app", "settings.json");

    private static readonly Lazy<AppSettings> LazyCurrent = new(Load);

    /// <summary>当前设置（懒加载；主题在启动早期访问时触发读盘）。</summary>
    public static AppSettings Current => LazyCurrent.Value;

    /// <summary>主题模式（默认跟随系统，与 v1.0 行为一致）。</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>启动后自动检查 Harness 更新（默认开）。</summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>启动后自动检查应用（壳自身）更新：GitHub Releases（默认开）。</summary>
    public bool AutoCheckAppUpdate { get; set; } = true;

    /// <summary>顶栏显示余额（默认关）。</summary>
    public bool ShowBalance { get; set; }

    /// <summary>关闭窗口时最小化到托盘（服务继续运行；默认开）。关 = 关窗即退出（v1.1 行为）。</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>余额低于阈值时提醒（默认开，仅在余额监控运行时生效）。</summary>
    public bool BalanceAlertEnabled { get; set; } = true;

    /// <summary>余额告警阈值（元，默认 5）。</summary>
    public decimal BalanceAlertThreshold { get; set; } = 5m;

    /// <summary>允许读取 ~/.dsh/.credentials.yaml（用户确认弹窗授权后置真，可撤销）。</summary>
    public bool AllowReadDshCredentials { get; set; }

    /// <summary>界面缩放百分比（100 / 125 / 150；映射 WebView2 ZoomFactor，默认 100）。</summary>
    public int ZoomPercent { get; set; } = 100;

    /// <summary>手动兜底模式的 API Key（DPAPI 密文 Base64，绝不明文落盘）。</summary>
    public string? EncryptedApiKey { get; set; }

    /// <summary>顶栏余额显示来源："deepseek"（默认，¥ 余额）| "kimi"（Kimi for Coding 配额百分比）。</summary>
    public string BalanceSource { get; set; } = "deepseek";

    /// <summary>Kimi 手动兜底模式的 API Key（DPAPI 密文 Base64，绝不明文落盘）。</summary>
    public string? EncryptedKimiApiKey { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                       ?? new AppSettings();
        }
        catch
        {
            // 损坏配置按默认处理
        }
        return new AppSettings();
    }

    /// <summary>原子持久化：先写临时文件再替换，崩溃/断电不会截断配置（审查加固项）。</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            if (File.Exists(SettingsPath))
                File.Replace(tmp, SettingsPath, null);
            else
                File.Move(tmp, SettingsPath);
        }
        catch
        {
            // 保存失败不影响运行
        }
    }
}
