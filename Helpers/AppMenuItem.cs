using System.Windows.Media;

namespace dsh_app.Helpers;

/// <summary>
/// 通用下拉菜单项：Tag 用于事件路由（谁点了哪个菜单项），Content 为显示文本；
/// Foreground 非空时覆盖默认文字色（如"更新可用"高亮），Enabled=false 置灰禁用。
/// 顶栏菜单与托盘右键菜单共用同一数据模型。
/// </summary>
public sealed record AppMenuItem(string Tag, string Content, Brush? Foreground = null, bool Enabled = true);
