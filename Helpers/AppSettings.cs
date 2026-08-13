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

    /// <summary>顶栏显示余额（默认关）。</summary>
    public bool ShowBalance { get; set; }

    /// <summary>允许读取 ~/.dsh/.credentials.yaml（用户确认弹窗授权后置真，可撤销）。</summary>
    public bool AllowReadDshCredentials { get; set; }

    /// <summary>手动兜底模式的 API Key（DPAPI 密文 Base64，绝不明文落盘）。</summary>
    public string? EncryptedApiKey { get; set; }

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
