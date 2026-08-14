using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using dsh_app.Helpers;

namespace dsh_app.Views;

/// <summary>
/// 通用下拉菜单面板：渲染 AppMenuItem 列表（MenuItemButton 样式），点击经 ItemClicked 路由。
/// 顶栏菜单 Popup 与托盘右键 ContextMenu 均基于同一视觉。
/// 代码生成按钮以支持每项独立 Foreground（高亮）与 IsEnabled（进行中置灰）。
/// </summary>
public partial class AppMenuPanel : UserControl
{
    public AppMenuPanel()
    {
        InitializeComponent();
    }

    /// <summary>菜单项被点击（参数为该项 Tag）。</summary>
    public event Action<string>? ItemClicked;

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
}
