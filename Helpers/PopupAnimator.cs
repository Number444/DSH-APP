using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace dsh_app.Helpers;

/// <summary>
/// Popup 内容打开/关闭动画的公共实现——任何 Popup 的 Child 均可复用：
/// - 打开：从锚点方向抛出（两轴不同缓动合成抛物线）+ 小→1 放大（ElasticEase 惯性回弹）+ 模糊渐清
/// - 关闭：收拢缩小 + 模糊 + 渐隐（完成后回调，由调用方置 IsOpen=false——Popup 关闭即销毁视觉树）
/// 菜单类用默认档（DefaultOpen/DefaultClose）；短生命周期提示类（余额状态卡）用轻量档（LightOpen/LightClose）。
///
/// 实现关键（v1.3.1 修复）：必须用 Animatable.BeginAnimation 直调（Transform/Effect 均为 Animatable），
/// 不能用 Storyboard.SetTarget —— Storyboard 对非元素目标（Transform/Effect 等 Freezable）的动画
/// 会被静默丢弃，导致只有元素属性（Opacity）动画生效、缩放/位移/模糊全部定格在起始态。
/// BeginAnimation 的同属性替换语义天然取消旧动画；被替换/重置的旧时钟不触发 Completed，
/// 孤儿动画（RenderTransform 被替换后仍跑完的旧 Transform 时钟）完成回调用 ReferenceEquals 防误清。
/// 尊重系统"菜单动画"设置；重新打开会取消进行中的收拢（IsClosing 复位）。
/// </summary>
public static class PopupAnimator
{
    /// <summary>菜单默认档：全套（抛出抛物线 + 弹性回弹 + 模糊渐清）。</summary>
    public static readonly OpenOptions DefaultOpen = new();

    /// <summary>轻量档：淡入 + 轻放大，无位移无弹性（状态卡等短生命周期提示）。</summary>
    public static readonly OpenOptions LightOpen = new()
    { DurationMs = 200, StartScale = 0.9, BlurRadius = 6, BlurMs = 160, Elastic = false, Fly = false };

    /// <summary>菜单默认关闭档：收拢缩小 + 模糊 + 渐隐。</summary>
    public static readonly CloseOptions DefaultClose = new();

    /// <summary>轻量关闭：快速淡出 + 轻微缩小。</summary>
    public static readonly CloseOptions LightClose = new() { DurationMs = 120, EndScale = 0.9, BlurRadius = 6 };

    /// <summary>收拢动画进行中标志（幂等防重入；重新打开时复位）。</summary>
    private static readonly DependencyProperty IsClosingProperty = DependencyProperty.RegisterAttached(
        "IsClosing", typeof(bool), typeof(PopupAnimator), new PropertyMetadata(false));

    /// <summary>
    /// 播放打开动画。flyFrom = 抛出起点相对最终位置的偏移（DIP）；菜单传 (0,-12)（从按钮上方抛出），
    /// 托盘按光标方向计算；轻量档可传 null（无位移）。
    /// 起始态在同步段内设置并立即播放，渲染首帧即动画起点（不闪帧）。
    /// </summary>
    public static void PlayOpen(FrameworkElement el, Point? flyFrom = null, OpenOptions? options = null)
    {
        options ??= DefaultOpen;
        el.SetValue(IsClosingProperty, false); // 重新打开：取消收拢态（进行中的收拢动画由下方 BeginAnimation 替换移除）
        ResetVisual(el);

        if (!SystemParameters.MenuAnimation) return; // 系统关闭动画：直接以最终态显示

        // 起始态：透明 + 小 + 模糊 + 锚点方向偏移
        var scale = new ScaleTransform(options.StartScale, options.StartScale);
        var translate = new TranslateTransform(
            options.Fly ? flyFrom?.X ?? 0 : 0,
            options.Fly ? flyFrom?.Y ?? 0 : 0);
        el.RenderTransform = new TransformGroup { Children = { scale, translate } };
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.Opacity = 0;
        var blur = new BlurEffect { Radius = options.BlurRadius };
        el.Effect = blur;

        var dur = TimeSpan.FromMilliseconds(options.DurationMs);
        var fadeIn = new DoubleAnimation(0, 1, new Duration(dur))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        // ElasticEase：先冲过目标再弹回（Oscillations=1 单次回弹，Springiness=5 过冲≈12%，
        // 有"惯性"感但克制）；轻量档退回纯缓出。过冲幅度与 AnimSafePad 配套（40px 安全区覆盖抛出 + 过冲）
        DoubleAnimation growX, growY;
        if (options.Elastic)
        {
            var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 };
            growX = new DoubleAnimation(options.StartScale, 1, new Duration(dur)) { EasingFunction = ease };
            growY = new DoubleAnimation(options.StartScale, 1, new Duration(dur)) { EasingFunction = ease };
        }
        else
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            growX = new DoubleAnimation(options.StartScale, 1, new Duration(dur)) { EasingFunction = ease };
            growY = new DoubleAnimation(options.StartScale, 1, new Duration(dur)) { EasingFunction = ease };
        }
        // 抛物线：水平先快后慢（抛出）+ 垂直先慢后快（下落），两轴合成弧线
        var glideX = new DoubleAnimation(translate.X, 0, new Duration(dur))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var glideY = new DoubleAnimation(translate.Y, 0, new Duration(dur))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        var clear = new DoubleAnimation(options.BlurRadius, 0, new Duration(TimeSpan.FromMilliseconds(options.BlurMs)))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        // 先挂完成回调再开播；主动画（fadeIn，360ms 最晚）完成时释放 Effect
        fadeIn.Completed += (_, _) =>
        {
            // 孤儿动画完成时不误清新动画的 Effect
            if (ReferenceEquals(el.Effect, blur)) el.Effect = null;
        };
        el.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, growX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, growY);
        translate.BeginAnimation(TranslateTransform.XProperty, glideX);
        translate.BeginAnimation(TranslateTransform.YProperty, glideY);
        blur.BeginAnimation(BlurEffect.RadiusProperty, clear);
    }

    /// <summary>
    /// 播放关闭动画：向锚点收拢（缩小 + 朝 shrinkTo 方向位移）+ 模糊 + 渐隐，完成后回调
    /// （调用方在回调里置 IsOpen=false）。shrinkTo = 收拢目标方向偏移（DIP）：托盘向光标方向、
    /// 顶栏/余额向按钮方向（上方）；null 则原地缩小（状态卡轻量档）。
    /// 幂等：已有收拢动画在跑时直接返回（首个动画完成时统一执行关闭）；重新打开由 PlayOpen 取消。
    /// </summary>
    public static void PlayClose(FrameworkElement el, Action? done = null, CloseOptions? options = null,
        Point? shrinkTo = null)
    {
        options ??= DefaultClose;
        if ((bool)el.GetValue(IsClosingProperty)) return; // 已有收拢在跑：由它完成关闭
        el.SetValue(IsClosingProperty, true);
        ResetVisual(el);

        if (!SystemParameters.MenuAnimation)
        {
            el.SetValue(IsClosingProperty, false);
            done?.Invoke();
            return;
        }

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform(0, 0);
        el.RenderTransform = new TransformGroup { Children = { scale, translate } };
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.Opacity = 1;
        var blur = new BlurEffect { Radius = 0 };
        el.Effect = blur;

        var dur = TimeSpan.FromMilliseconds(options.DurationMs);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn }; // 加速收拢感
        var shrinkX = new DoubleAnimation(1, options.EndScale, new Duration(dur)) { EasingFunction = ease };
        var shrinkY = new DoubleAnimation(1, options.EndScale, new Duration(dur)) { EasingFunction = ease };
        var glideX = new DoubleAnimation(0, shrinkTo?.X ?? 0, new Duration(dur)) { EasingFunction = ease };
        var glideY = new DoubleAnimation(0, shrinkTo?.Y ?? 0, new Duration(dur)) { EasingFunction = ease };
        var blurIn = new DoubleAnimation(0, options.BlurRadius, new Duration(dur)) { EasingFunction = ease };
        var fadeOut = new DoubleAnimation(1, 0, new Duration(dur)) { EasingFunction = ease };

        fadeOut.Completed += (_, _) =>
        {
            el.SetValue(IsClosingProperty, false);
            ResetVisual(el);
            done?.Invoke();
        };
        el.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY);
        translate.BeginAnimation(TranslateTransform.XProperty, glideX);
        translate.BeginAnimation(TranslateTransform.YProperty, glideY);
        blur.BeginAnimation(BlurEffect.RadiusProperty, blurIn);
    }

    /// <summary>恢复静止最终态：清动画时钟、清模糊、清变换、不透明。</summary>
    private static void ResetVisual(FrameworkElement el)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.RenderTransform = null;
        el.Effect = null;
        el.Opacity = 1;
    }

    /// <summary>打开动画参数档。</summary>
    public sealed class OpenOptions
    {
        public double DurationMs { get; set; } = 360;
        public double StartScale { get; set; } = 0.5;
        public double BlurRadius { get; set; } = 14;
        public double BlurMs { get; set; } = 220;
        public bool Elastic { get; set; } = true;
        public bool Fly { get; set; } = true;
    }

    /// <summary>关闭动画参数档。</summary>
    public sealed class CloseOptions
    {
        public double DurationMs { get; set; } = 150;
        public double EndScale { get; set; } = 0.55;
        public double BlurRadius { get; set; } = 12;
    }
}
