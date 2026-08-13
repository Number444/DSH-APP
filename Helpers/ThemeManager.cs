using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace dsh_app.Helpers;

/// <summary>应用主题模式。</summary>
public enum AppTheme
{
    Dark,   // 固定深色
    Light,  // 固定浅色
    System, // 跟随系统深浅色
}

/// <summary>
/// 主题管理：深色 / 浅色 / 跟随系统。
/// 切换时替换 App 资源字典 [0]（Colors.Dark/Light.xaml），样式全部走 DynamicResource 自动跟随。
/// 设置持久化到 %LOCALAPPDATA%\dsh-app\settings.json。
/// </summary>
public static class ThemeManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dsh-app", "settings.json");

    /// <summary>当前设置的主题模式。</summary>
    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>当前实际生效是否为深色（System 模式下按系统判断）。</summary>
    public static bool IsDarkNow { get; private set; } = true;

    /// <summary>主题实际生效变化（供窗口做额外适配，如 DWM 标题栏深色）。</summary>
    public static event Action<bool>? ThemeChanged;

    /// <summary>应用启动时调用：读设置 → 应用 → 订阅系统主题变化。</summary>
    public static void Initialize()
    {
        Current = Load();
        ApplyNow();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>切换主题模式（持久化 + 立即生效）。</summary>
    public static void SetTheme(AppTheme theme)
    {
        Current = theme;
        Save();
        ApplyNow();
    }

    /// <summary>系统当前是否为浅色（读取系统主题注册表）。</summary>
    public static bool SystemIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme") ?? 0) == 1;
        }
        catch
        {
            return false;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || Current != AppTheme.System)
            return;
        Application.Current?.Dispatcher.Invoke(ApplyNow);
    }

    /// <summary>替换配色字典（[0] 固定为 Colors.*.xaml），样式经 DynamicResource 自动跟随。</summary>
    private static void ApplyNow()
    {
        IsDarkNow = Current == AppTheme.System ? !SystemIsLight() : Current == AppTheme.Dark;

        var dicts = Application.Current?.Resources.MergedDictionaries;
        if (dicts is null || dicts.Count == 0)
            return;

        dicts[0] = new ResourceDictionary
        {
            Source = new Uri(
                IsDarkNow ? "Resources/Colors.Dark.xaml" : "Resources/Colors.Light.xaml",
                UriKind.Relative)
        };
        ThemeChanged?.Invoke(IsDarkNow);
    }

    private static AppTheme Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))?.Theme
                       ?? AppTheme.System;
        }
        catch
        {
            // 损坏配置按默认处理
        }
        return AppTheme.System;
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings { Theme = Current }));
        }
        catch
        {
            // 保存失败不影响运行
        }
    }

    private sealed class Settings
    {
        public AppTheme Theme { get; set; } = AppTheme.System;
    }
}
