using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using dsh_app.Helpers;

namespace dsh_app.Views;

/// <summary>
/// 设置窗口：主题模式（深色 / 浅色 / 跟随系统）、自动检查更新、余额显示（授权 + 手动 Key）。
/// 变更即生效并持久化。
/// </summary>
public partial class SettingsWindow : Window
{
    private const int DWM_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const string AuthMessage =
        "为显示 DeepSeek 开放平台余额，需要读取 ~/.dsh/.credentials.yaml 中的 DEEPSEEK_API_KEY。\n\n" +
        "该 Key 仅在本机内存中使用：不会写入本应用配置、不会写入日志、不会上传；" +
        "你可以在设置页随时撤销授权。";

    private readonly Action<bool> _themeHandler;

    /// <summary>构造期控件赋值会触发 Checked 事件，此标志屏蔽弹窗分支（审查加固：避免每次打开设置窗重弹授权）。</summary>
    private bool _initializing = true;

    public SettingsWindow()
    {
        InitializeComponent();

        // 按当前设置初始化控件状态（构造期事件仅刷新 UI 状态，不弹授权）
        var theme = ThemeManager.Current;
        ChkSystem.IsChecked = theme == AppTheme.System;
        RadioDark.IsChecked = theme == AppTheme.Dark;
        RadioLight.IsChecked = theme == AppTheme.Light;
        UpdateEnabled();

        ChkAutoCheckUpdate.IsChecked = AppSettings.Current.AutoCheckUpdate;
        ChkAutoCheckAppUpdate.IsChecked = AppSettings.Current.AutoCheckAppUpdate;
        ChkShowBalance.IsChecked = AppSettings.Current.ShowBalance;
        ChkTrayClose.IsChecked = AppSettings.Current.MinimizeToTrayOnClose;
        ChkBalanceAlert.IsChecked = AppSettings.Current.BalanceAlertEnabled;
        ThresholdBox.Text = AppSettings.Current.BalanceAlertThreshold.ToString("0.##");
        UpdateAlertUI();
        UpdateBalanceOptions();
        // 缓存清理已标记的状态回显
        if (WebView2CacheCleaner.IsMarked)
            CacheHint.Text = "已标记清理，将在下次启动时执行。";
        _initializing = false;

        // 主题切换时联动标题栏深色（关闭时解除，防订阅泄漏）
        _themeHandler = _ => ApplyDwm();
        ThemeManager.ThemeChanged += _themeHandler;
        Closed += (_, _) => ThemeManager.ThemeChanged -= _themeHandler;
    }

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        var theme = ChkSystem.IsChecked == true
            ? AppTheme.System
            : RadioLight.IsChecked == true ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.SetTheme(theme);
        UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        var isSystem = ChkSystem.IsChecked == true;
        RadioDark.IsEnabled = !isSystem;
        RadioLight.IsEnabled = !isSystem;
    }

    // ---------------- 自动检查更新 ----------------

    private void OnAutoCheckUpdateChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.AutoCheckUpdate = ChkAutoCheckUpdate.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnAutoCheckAppUpdateChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.AutoCheckAppUpdate = ChkAutoCheckAppUpdate.IsChecked == true;
        AppSettings.Current.Save();
    }

    // ---------------- 常驻（最小化到托盘） ----------------

    private void OnTrayCloseChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.MinimizeToTrayOnClose = ChkTrayClose.IsChecked == true;
        AppSettings.Current.Save();
    }

    // ---------------- 余额告警 ----------------

    private void OnBalanceAlertChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.BalanceAlertEnabled = ChkBalanceAlert.IsChecked == true;
        AppSettings.Current.Save();
        UpdateAlertUI();
    }

    private void UpdateAlertUI()
    {
        AlertThresholdPanel.Visibility = ChkBalanceAlert.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>阈值失焦即保存；非法输入回显当前值。</summary>
    private void ThresholdBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (decimal.TryParse(ThresholdBox.Text.Trim(), out var value) && value > 0)
        {
            AppSettings.Current.BalanceAlertThreshold = value;
            AppSettings.Current.Save();
        }
        ThresholdBox.Text = AppSettings.Current.BalanceAlertThreshold.ToString("0.##");
    }

    // ---------------- 余额显示 ----------------

    private void OnShowBalanceChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.ShowBalance = ChkShowBalance.IsChecked == true;
        AppSettings.Current.Save();
        UpdateBalanceOptions();

        // 构造期赋值不弹窗（_initializing）；仅用户主动开启且未授权且存在凭据文件时弹授权确认
        if (_initializing) return;
        if (ChkShowBalance.IsChecked == true
            && !AppSettings.Current.AllowReadDshCredentials
            && CredentialsReader.CredentialsFileExists)
        {
            var dlg = new ConfirmDialog("允许 dsh-app 读取 dsh 凭据？", AuthMessage, "允许", "暂不")
            {
                Owner = this,
            };
            if (dlg.ShowDialog() == true)
            {
                AppSettings.Current.AllowReadDshCredentials = true;
                AppSettings.Current.Save();
                UpdateBalanceOptions();
            }
        }
    }

    /// <summary>按授权状态刷新余额区：授权行 / 撤销或重新授权按钮 / 手动 Key 输入。</summary>
    private void UpdateBalanceOptions()
    {
        var show = ChkShowBalance.IsChecked == true;
        BalanceOptions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var allowed = AppSettings.Current.AllowReadDshCredentials;
        if (allowed)
        {
            AuthStatusText.Text = "已授权读取 dsh 凭据（~/.dsh/.credentials.yaml），余额将自动获取。";
            BtnAuthAction.Content = "撤销 dsh 凭据读取授权";
            BtnAuthAction.Visibility = Visibility.Visible;
            ManualKeyPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            AuthStatusText.Text = CredentialsReader.CredentialsFileExists
                ? "未授权读取 dsh 凭据，可点击重新授权；或手动填写 API Key。"
                : "未检测到 dsh 凭据文件，可手动填写 API Key。";
            BtnAuthAction.Content = "重新授权";
            BtnAuthAction.Visibility = CredentialsReader.CredentialsFileExists
                ? Visibility.Visible : Visibility.Collapsed;
            ManualKeyPanel.Visibility = Visibility.Visible;
            ApiKeyHint.Text = AppSettings.Current.EncryptedApiKey is null
                ? "自动读取不可用时在此填写 DeepSeek 开放平台 API Key（platform.deepseek.com）。"
                : "已保存手动 API Key（DPAPI 加密存储）。";
        }
    }

    private void BtnAuthAction_Click(object sender, RoutedEventArgs e)
    {
        if (AppSettings.Current.AllowReadDshCredentials)
        {
            // 撤销授权：清除标记，回退手动模式
            AppSettings.Current.AllowReadDshCredentials = false;
            AppSettings.Current.Save();
            UpdateBalanceOptions();
        }
        else
        {
            var dlg = new ConfirmDialog("允许 dsh-app 读取 dsh 凭据？", AuthMessage, "允许", "暂不")
            {
                Owner = this,
            };
            if (dlg.ShowDialog() == true)
            {
                AppSettings.Current.AllowReadDshCredentials = true;
                AppSettings.Current.Save();
                UpdateBalanceOptions();
            }
        }
    }

    private void ApiKeyLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://platform.deepseek.com/api_keys") { UseShellExecute = true });
        }
        catch
        {
            // 打不开链接不影响使用
        }
    }

    // ---------------- 数据目录与缓存 ----------------

    /// <summary>打开应用数据目录（%LOCALAPPDATA%\dsh-app）；不存在先创建（与 MainWindow.OpenLog 同模式）。</summary>
    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(WebView2CacheCleaner.AppDataDir);
            Process.Start(new ProcessStartInfo("explorer.exe",
                $"\"{WebView2CacheCleaner.AppDataDir}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打开失败不影响使用
        }
    }

    /// <summary>清理页面缓存：运行中目录被 WebView2 锁定，确认后只写标记，下次启动时执行清理。</summary>
    private void BtnClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (WebView2CacheCleaner.IsMarked)
        {
            CacheHint.Text = "已标记清理，将在下次启动时执行。";
            return;
        }
        var dlg = new ConfirmDialog("清理页面缓存",
            "将清理 WebView2 页面缓存（Cache / Code Cache / GPUCache），不动 Cookies 与网站数据，登录态与页面设置保留。\n\n清理在下次启动时自动执行。是否标记？",
            "标记清理", "取消") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            WebView2CacheCleaner.MarkForCleanup();
            CacheHint.Text = "已标记清理，将在下次启动时执行。";
        }
        catch (Exception ex)
        {
            CacheHint.Text = $"标记失败：{ex.Message}";
        }
    }

    /// <summary>失焦即保存（DPAPI 加密），输入框清空防明文残留；加密失败必须如实提示。</summary>
    private void ApiKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            AppSettings.Current.EncryptedApiKey = null;
            AppSettings.Current.Save();
            return;
        }
        var encrypted = DpapiHelper.Encrypt(key);
        if (encrypted is null)
        {
            // 加密失败：不落盘、不覆盖原密文、不清空输入，明确提示（不得假装"已保存"）
            ApiKeyHint.Text = "加密失败，未保存。请重试（或检查系统凭据服务）。";
            return;
        }
        AppSettings.Current.EncryptedApiKey = encrypted;
        AppSettings.Current.Save();
        ApiKeyBox.Clear();
        ApiKeyHint.Text = "已保存（DPAPI 加密存储）。";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDwm();
    }

    private void ApplyDwm()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int dark = ThemeManager.IsDarkNow ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWM_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
