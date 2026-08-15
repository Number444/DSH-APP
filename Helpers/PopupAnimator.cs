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
    /// 播放打开动画。flyFrom = 抛出起点相对最终位置的偏移（DIP）；菜单传 (0,-24)（从按钮上方抛出），
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
        // 淡入比总时长快（弹性档 240ms），保证"惯性滑出"发生时透明度已高、肉眼可见
        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(Math.Min(240, options.DurationMs))))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        // 缩放（质感档）：全程平滑展开（0.5→1.0，55% 与落位同步到位），不过冲——
        // "惯性"由位置承担（见 BuildGlide），缩放过冲观感是"猛地鼓一下"，位置惯性才是落位弹回
        AnimationTimeline growTx, growTy;
        if (options.Elastic)
        {
            growTx = BuildScale(options, dur);
            growTy = BuildScale(options, dur);
        }
        else
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            growTx = new DoubleAnimation(options.StartScale, 1, new Duration(dur)) { EasingFunction = ease };
            growTy = new DoubleAnimation(options.StartScale, 1, new Duration(dur)) { EasingFunction = ease };
        }
        // 位置（质感档）：三段关键帧——抛物线落位（0-55%）→ 沿飞行方向惯性滑出 2px（55-72%）→ 平滑弹回（72-100%）。
        // 惯性方向 = 飞行方向（-flyFrom 归一化）：顶栏继续向下 +2，托盘沿光标反方向 ±2；
        // 幅度与 AnimSafePad 配套（抛出 24px + 惯性 2px = 26px < 40px 安全区）。
        // 抛物线：水平先快后慢（抛出）+ 垂直先慢后快（下落，t² 自由落体），两轴合成弧线
        var inertia = CalcInertia(new Point(translate.X, translate.Y));
        AnimationTimeline glideTx, glideTy;
        if (options.Elastic)
        {
            glideTx = BuildGlide(translate.X, inertia.X, new KeySpline(0.2, 0.0, 0.6, 1.0), dur);
            glideTy = BuildGlide(translate.Y, inertia.Y, new KeySpline(0.5, 0.0, 1.0, 1.0), dur);
        }
        else
        {
            glideTx = new DoubleAnimation(translate.X, 0, new Duration(dur))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            glideTy = new DoubleAnimation(translate.Y, 0, new Duration(dur))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        }
        var clear = new DoubleAnimation(options.BlurRadius, 0, new Duration(TimeSpan.FromMilliseconds(options.BlurMs)))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        // 先挂完成回调再开播；主动画（growTx，总时长最晚）完成时释放 Effect
        growTx.Completed += (_, _) =>
        {
            // 孤儿动画完成时不误清新动画的 Effect
            if (ReferenceEquals(el.Effect, blur)) el.Effect = null;
        };
        el.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, growTx);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, growTy);
        translate.BeginAnimation(TranslateTransform.XProperty, glideTx);
        translate.BeginAnimation(TranslateTransform.YProperty, glideTy);
        blur.BeginAnimation(BlurEffect.RadiusProperty, clear);
    }

    /// <summary>
    /// 播放关闭动画。flyFrom = 打开时的抛出起点（DIP，相对最终位置）：非 null 时执行
    /// **打开动画的严格倒放**（时间反转 + 缓动反转）——先微动 2px（打开"弹回"的逆过程），
    /// 再沿抛物线原路飞回锚点，同时缩小/渐模糊/淡出，总时长与打开一致（480ms）。
    /// flyFrom = null（状态卡轻量档）：原地缩小 + 模糊 + 渐隐（快速 120ms）。
    /// 完成后回调（调用方在回调里置 IsOpen=false）。幂等：已有收拢在跑时直接返回；
    /// 重新打开由 PlayOpen 取消。
    /// </summary>
    public static void PlayClose(FrameworkElement el, Action? done = null, CloseOptions? options = null,
        Point? flyFrom = null)
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

        AnimationTimeline shrinkTx, shrinkTy;
        AnimationTimeline? glideTx, glideTy;
        AnimationTimeline blurIn, fadeOut;
        if (flyFrom is { } from && (from.X != 0 || from.Y != 0))
        {
            // ---- 倒放档（菜单）：打开动画严格时间反转 ----
            var dur = TimeSpan.FromMilliseconds(DefaultOpen.DurationMs);
            var inertia = CalcInertia(from);

            // Scale：1.0（0-45%）→ StartScale（100%，fast 反转）；X/Y 各一个动画实例
            shrinkTx = BuildScaleReverse(dur);
            shrinkTy = BuildScaleReverse(dur);
            // Translate：0 → inertia（0-28%，打开"弹回"的逆）→ 0（28-45%，打开"滑出"的逆）
            //          → from（45-100%，抛物线原路飞回；flyRev = 打开飞行段 KeySpline 反转）
            glideTx = BuildGlideReverse(from.X, inertia.X, new KeySpline(0.4, 0.0, 0.8, 1.0), dur);
            glideTy = BuildGlideReverse(from.Y, inertia.Y, new KeySpline(0.0, 0.0, 0.5, 1.0), dur);
            // 模糊/淡出：BeginTime 延迟到后段（倒放时序；EaseIn = 打开 EaseOut 的时间反转）
            blurIn = new DoubleAnimation(0, DefaultOpen.BlurRadius,
                new Duration(TimeSpan.FromMilliseconds(DefaultOpen.BlurMs)))
            { BeginTime = TimeSpan.FromMilliseconds(DefaultOpen.DurationMs - DefaultOpen.BlurMs),
              EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(240)))
            { BeginTime = TimeSpan.FromMilliseconds(240),
              EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        }
        else
        {
            // ---- 轻量档（状态卡）：原地缩小 + 模糊 + 渐隐 ----
            var dur = TimeSpan.FromMilliseconds(options.DurationMs);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn }; // 加速收拢感
            shrinkTx = new DoubleAnimation(1, options.EndScale, new Duration(dur)) { EasingFunction = ease };
            shrinkTy = new DoubleAnimation(1, options.EndScale, new Duration(dur)) { EasingFunction = ease };
            glideTx = null;
            glideTy = null;
            blurIn = new DoubleAnimation(0, options.BlurRadius, new Duration(dur)) { EasingFunction = ease };
            fadeOut = new DoubleAnimation(1, 0, new Duration(dur)) { EasingFunction = ease };
        }

        fadeOut.Completed += (_, _) =>
        {
            el.SetValue(IsClosingProperty, false);
            ResetVisual(el);
            done?.Invoke();
        };
        el.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkTx);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkTy);
        if (glideTx != null) translate.BeginAnimation(TranslateTransform.XProperty, glideTx);
        if (glideTy != null) translate.BeginAnimation(TranslateTransform.YProperty, glideTy);
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
        public double DurationMs { get; set; } = 480;
        public double StartScale { get; set; } = 0.5;
        public double BlurRadius { get; set; } = 20;
        public double BlurMs { get; set; } = 380;
        public bool Elastic { get; set; } = true;
        public bool Fly { get; set; } = true;
    }

    /// <summary>缩放关键帧：start→1.0（55% 与落位同步到位），此后保持——全程平滑，无过冲（惯性由位置承担）。</summary>
    private static DoubleAnimationUsingKeyFrames BuildScale(OpenOptions options, Duration dur)
    {
        var g = new DoubleAnimationUsingKeyFrames { Duration = dur };
        var fast = new KeySpline(0.2, 0.7, 0.3, 1.0);
        g.KeyFrames.Add(new SplineDoubleKeyFrame(options.StartScale, KeyTime.FromPercent(0.0), fast));
        g.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromPercent(0.55), fast));
        g.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), fast));
        return g;
    }

    /// <summary>位置三段关键帧：from→0（抛物线落位，0-55%）→ inertia（惯性滑出，55-72%）→ 0（平滑弹回）。
    /// flySpline 控制抛物线手感：X 轴先快后慢（水平抛出），Y 轴先慢后快（t² 自由落体）。</summary>
    private static DoubleAnimationUsingKeyFrames BuildGlide(double from, double inertia, KeySpline flySpline, Duration dur)
    {
        var g = new DoubleAnimationUsingKeyFrames { Duration = dur };
        var back = new KeySpline(0.4, 0.0, 0.6, 1.0); // 弹回：先快后慢，轻落定
        g.KeyFrames.Add(new SplineDoubleKeyFrame(from, KeyTime.FromPercent(0.0), flySpline));
        g.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromPercent(0.55), flySpline));
        g.KeyFrames.Add(new SplineDoubleKeyFrame(inertia, KeyTime.FromPercent(0.72), new KeySpline(0.2, 0.0, 0.4, 1.0)));
        g.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0), back));
        return g;
    }

    /// <summary>缩放关键帧（倒放）：1.0（0-45%）→ StartScale（100%），缓动 = 打开 fast 段的时间反转。</summary>
    private static DoubleAnimationUsingKeyFrames BuildScaleReverse(Duration dur)
    {
        var g = new DoubleAnimationUsingKeyFrames { Duration = dur };
        var fastRev = new KeySpline(0.7, 0.0, 0.8, 0.3); // (0.2,0.7,0.3,1.0) 反转
        g.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0), fastRev));
        g.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromPercent(0.45), fastRev));
        g.KeyFrames.Add(new SplineDoubleKeyFrame(DefaultOpen.StartScale, KeyTime.FromPercent(1.0), fastRev));
        return g;
    }

    /// <summary>位置关键帧（倒放）：0 → inertia（0-28%，打开"弹回"的逆）→ 0（28-45%，打开"滑出"的逆）
    /// → from（45-100%，抛物线原路飞回锚点）。flyRev = 打开飞行段 KeySpline 的时间反转。</summary>
    private static DoubleAnimationUsingKeyFrames BuildGlideReverse(double from, double inertia, KeySpline flyRev, Duration dur)
    {
        var g = new DoubleAnimationUsingKeyFrames { Duration = dur };
        var cRev = new KeySpline(0.4, 0.0, 0.6, 1.0); // 打开"弹回"段 (0.4,0,0.6,1) 自反
        var bRev = new KeySpline(0.6, 0.0, 0.8, 1.0); // 打开"滑出"段 (0.2,0,0.4,1) 反转
        g.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0), cRev));
        g.KeyFrames.Add(new SplineDoubleKeyFrame(inertia, KeyTime.FromPercent(0.28), cRev));
        g.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromPercent(0.45), bRev));
        g.KeyFrames.Add(new SplineDoubleKeyFrame(from, KeyTime.FromPercent(1.0), flyRev));
        return g;
    }

    /// <summary>惯性方向（沿飞行方向 2px）：飞行方向 = -flyFrom 归一化；打开落位后滑出、关闭飞回前的微动共用。</summary>
    private static Point CalcInertia(Point flyFrom)
    {
        double len = Math.Max(Math.Abs(flyFrom.X), Math.Abs(flyFrom.Y));
        return len > 0 ? new Point(-flyFrom.X / len * 2, -flyFrom.Y / len * 2) : new Point(0, 0);
    }

    /// <summary>关闭动画参数档。</summary>
    public sealed class CloseOptions
    {
        public double DurationMs { get; set; } = 150;
        public double EndScale { get; set; } = 0.55;
        public double BlurRadius { get; set; } = 12;
    }
}
