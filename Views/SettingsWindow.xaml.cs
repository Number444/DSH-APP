using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using dsh_app.Helpers;

namespace dsh_app.Views;

/// <summary>
/// 设置窗口：主题模式（深色 / 浅色 / 跟随系统），变更即生效并持久化。
/// </summary>
public partial class SettingsWindow : Window
{
    private const int DWM_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public SettingsWindow()
    {
        InitializeComponent();

        // 按当前设置初始化控件状态
        var theme = ThemeManager.Current;
        ChkSystem.IsChecked = theme == AppTheme.System;
        RadioDark.IsChecked = theme == AppTheme.Dark;
        RadioLight.IsChecked = theme == AppTheme.Light;
        UpdateEnabled();

        // 主题切换时联动标题栏深色
        ThemeManager.ThemeChanged += _ => ApplyDwm();
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
