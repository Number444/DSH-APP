using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using dsh_app.Helpers;

namespace dsh_app.Views;

/// <summary>
/// 通用自绘弹窗（GitHub Dark 风格）：标题 + 正文 + 按钮。
/// 双按钮模式（确认/取消，如更新确认、凭据授权、更新中断确认）；
/// 单按钮模式（cancelText 传 null，如"已是最新版本/检查失败/更新完成"等消息提示）。
/// 可选 glyph（⚠/ℹ/✓）作为消息类型图标。DWM 深色标题栏随主题联动（R8）。
/// </summary>
public partial class ConfirmDialog : Window
{
    private const int DWM_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly Action<bool> _themeHandler;

    /// <param name="cancelText">取消按钮文案；传 null 为单按钮模式（仅"确定"）。</param>
    /// <param name="glyph">消息类型图标：⚠（警告/橙）、ℹ（信息/蓝）、✓（成功/绿）；null = 不显示。</param>
    /// <param name="okDanger">主按钮为破坏性操作（红色 AccentRedBrush），如"中断并关闭"。</param>
    public ConfirmDialog(string title, string message, string okText = "确定", string? cancelText = null,
        string? glyph = null, bool okDanger = false)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;

        if (okDanger)
        {
            OkButton.Background = (Brush)FindResource("AccentRedBrush");
        }

        if (cancelText is null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            CancelButton.Content = cancelText;
        }

        if (!string.IsNullOrEmpty(glyph))
        {
            GlyphText.Text = glyph;
            GlyphText.Foreground = glyph switch
            {
                "⚠" => (Brush)FindResource("AccentOrangeBrush"),
                "✓" => (Brush)FindResource("ButtonGreenBrush"),
                _ => (Brush)FindResource("AccentBlueBrush"), // ℹ 等默认蓝色
            };
            GlyphText.Visibility = Visibility.Visible;
            TitleText.Margin = new Thickness(10, 0, 0, 0);
        }

        // 主题切换时联动标题栏深色（关闭时解除，防订阅泄漏）
        _themeHandler = _ => ApplyDwm();
        ThemeManager.ThemeChanged += _themeHandler;
        Closed += (_, _) => ThemeManager.ThemeChanged -= _themeHandler;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

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
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
