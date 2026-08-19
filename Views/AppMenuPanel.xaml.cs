using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using dsh_app.Helpers;

namespace dsh_app.Views;

/// <summary>
/// 通用下拉菜单面板：渲染 AppMenuItem 列表（MenuItemButton 样式），点击经 ItemClicked 路由。
/// 顶栏下拉、托盘右键、余额左键三处菜单共用（v1.2.1 起托盘同为 Popup，弃用 ContextMenu）。
/// 代码生成按钮以支持每项独立 Foreground（高亮）与 IsEnabled（进行中置灰）。
/// 打开/关闭动画委托 PopupAnimator（与余额状态卡等所有 Popup 复用同一套实现）。
/// 键盘：打开后焦点落首项（↑↓ 由 WPF 方向导航天然支持，禁用项自动跳过），Esc 经 DismissRequested 上抛。
/// </summary>
public partial class AppMenuPanel : UserControl
{
    /// <summary>动画安全区（DIP）：RootCard 外层的透明留白，防抛出/过冲被 Popup HWND 裁切。
    /// 与 PopupAnimator 的过冲幅度（≈10%）与抛出距离（≤24px）配套；定位代码须同步减此留白。</summary>
    public const double AnimSafePad = 40;

    public AppMenuPanel()
    {
        InitializeComponent();
        // Popup 是独立 HWND，主窗口的 PreviewKeyDown 收不到面板内按键，Esc 必须就地捕获上抛
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DismissRequested?.Invoke();
                e.Handled = true;
            }
        };
    }

    /// <summary>菜单项被点击（参数为该项 Tag）。</summary>
    public event Action<string>? ItemClicked;

    /// <summary>用户请求关闭菜单（Esc；由宿主动画关闭所属 Popup）。</summary>
    public event Action? DismissRequested;

    /// <summary>打开后将键盘焦点移入首个可用菜单项（Background 优先级：等 Popup 布局完成后执行）。</summary>
    public void FocusFirstItem()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            foreach (var child in ItemsHost.Children)
            {
                if (child is Button { IsEnabled: true } btn)
                {
                    btn.Focus();
                    break;
                }
            }
        });
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<AppMenuItem>), typeof(AppMenuPanel),
        new PropertyMetadata(null, (d, _) => ((AppMenuPanel)d).Rebuild()));

    /// <summary>菜单项集合。</summary>
    public IEnumerable<AppMenuItem>? ItemsSource
    {
        get => (IEnumerable<AppMenuItem>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private void Rebuild()
    {
        ItemsHost.Children.Clear();
        if (ItemsSource is null) return;

        var itemStyle = (Style)FindResource("MenuItemButton");
        foreach (var item in ItemsSource)
        {
            var btn = new Button
            {
                Content = item.Content,
                Style = itemStyle,
                IsEnabled = item.Enabled,
            };
            // 仅高亮项显式覆盖 Foreground；默认项不设值以保持样式作用（设 null 会遮蔽样式）
            if (item.Foreground is not null)
                btn.Foreground = item.Foreground;
            btn.Click += (_, _) => ItemClicked?.Invoke(item.Tag);
            ItemsHost.Children.Add(btn);
        }
    }

    /// <summary>最近一次打开动画的抛出起点（供关闭倒放使用：沿来路飞回锚点）。</summary>
    private Point _lastFlyFrom;

    /// <summary>打开动画（抛出放大 + 模糊渐清 + 惯性回弹）：flyFrom = 抛出起点相对最终位置的偏移（DIP）。</summary>
    public void PlayOpenAnimation(Point flyFrom)
    {
        _lastFlyFrom = flyFrom;
        PopupAnimator.PlayOpen(RootCard, flyFrom);
    }

    /// <summary>关闭动画（打开动画的严格倒放：沿垂直原路飞回锚点 + 缩小 + 模糊 + 渐隐），完成后回调。</summary>
    public void PlayCloseAnimation(Action? done = null) =>
        PopupAnimator.PlayClose(RootCard, done, null, _lastFlyFrom);
}
