using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace dsh_app;

/// <summary>
/// 主窗口分部：启动/下载进度条——像素点阵屏 + 彗尾光柱群。
///
/// 进度真实驱动（不再跑马灯）：启动期 = 步骤状态机（完成 N 步 → N/5），下载期 = 真实字节
/// 百分比；彗尾裁剪容器宽度用 400ms CubicEase 平滑推移。
///
/// 像素屏（屏幕模型，非"填充切割"）：90 列 × 2 行正方形像素（3px 格 + 1px 细缝）固定在
/// 轨道上永不动，颜色按列采样黑→主题蓝渐变；进度 = 点亮列数。熄灭态 12% 透明度呈屏幕
/// 点阵质感；点亮波式级联（120ms 淡入 + 8ms/列）；前沿列亮白（"亮白边缘"的像素化表达）。
///
/// 彗尾（参考音频播放器交互条）：光柱/像素块按宽度比例分布式锚定在已点亮区域内，各自
/// 段内向左匀速漂移、错峰循环；条增长时锚点逐帧重分布（ProgressFill.SizeChanged）。
/// 全部渲染线程动画——启动卡顿期不掉帧；覆盖层 Collapsed 后无渲染开销。
/// </summary>
public partial class MainWindow
{
    private const double PixelOffOpacity = 0.12; // 熄灭态透明度（屏幕点阵质感）

    /// <summary>前沿点亮列的亮白刷（"亮白边缘"的像素化表达）。</summary>
    private static readonly SolidColorBrush PixelHeadBrush = new(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));

    private double _progressPct;       // 当前目标进度 0~100（ActualWidth 未就绪时暂存）
    private bool _progressAnimating;   // 推移动画进行中（此间 SizeChanged 即时同步让位）
    private int _litCols;              // 当前已点亮列数
    private int _headCol = -1;         // 当前亮白前沿列（-1 = 无）

    /// <summary>像素列（每列上下 2 格）及其渐变刷（前沿列被亮白覆盖后恢复用）。</summary>
    private readonly List<Rectangle[]> _pixelCols = new();
    private readonly List<SolidColorBrush> _pixelBrushes = new();

    /// <summary>彗尾元素及其宽度比例锚点（0~1）与锚点偏移（拖尾像素块为负——亮头对齐锚点、
    /// 拖尾伸在左侧；RedistributeTail 换算实际 Left）。</summary>
    private readonly List<(FrameworkElement El, double Frac, double Off)> _tailItems = new();

    /// <summary>设置进度（0~100）。smooth=true 时彗尾容器 400ms 平滑推移 + 像素波式点亮；
    /// 步进跳变/复位用 false（瞬时）。</summary>
    private void SetProgress(double pct, bool smooth = true)
    {
        pct = Math.Clamp(pct, 0, 100);
        _progressPct = pct;
        UpdatePixelScreen(pct, instant: !smooth);
        var trackW = OverlayProgress.ActualWidth;
        if (trackW <= 0) return; // 尚未布局：SizeChanged 兜底即时同步
        ApplyProgressWidth(trackW * pct / 100, smooth);
    }

    private void ApplyProgressWidth(double targetW, bool smooth)
    {
        if (targetW > 0) targetW = Math.Max(targetW, 8); // 进度 >0 时彗尾容器至少露出头部区域
        if (!smooth)
        {
            _progressAnimating = false;
            ProgressFill.BeginAnimation(WidthProperty, null);
            ProgressFill.Width = targetW;
            return;
        }
        var from = double.IsNaN(ProgressFill.Width) ? 0 : ProgressFill.Width;
        if (Math.Abs(targetW - from) < 0.5) return; // 抖动过滤
        _progressAnimating = true;
        var anim = new DoubleAnimation(from, targetW, new Duration(TimeSpan.FromMilliseconds(400)))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        anim.Completed += (_, _) =>
        {
            // 自然完成：落最终值并摘除时钟（被替换的旧时钟不触发 Completed，无竞态）
            _progressAnimating = false;
            ProgressFill.BeginAnimation(WidthProperty, null);
            ProgressFill.Width = targetW;
        };
        ProgressFill.BeginAnimation(WidthProperty, anim);
    }

    /// <summary>轨道条宽变化（窗口尺寸/DPI/缩放）时即时重算填充宽；动画进行中不打扰。
    /// 首次拿到有效宽度时构建像素屏（幂等）。</summary>
    private void OnProgressTrackSizeChanged()
    {
        if (OverlayProgress.ActualWidth <= 0) return;
        BuildPixelScreen();
        if (_progressAnimating) return;
        ApplyProgressWidth(OverlayProgress.ActualWidth * _progressPct / 100, smooth: false);
    }

    // ---- 像素点阵屏 ----

    /// <summary>构建像素屏（幂等，仅首次）：3×3px 正方形像素 + 1px 细缝，两行间缝 1px，
    /// 整体居中于 8px 条高；每列颜色按位置采样黑→主题蓝渐变。构建后按暂存进度瞬时点亮。</summary>
    private void BuildPixelScreen()
    {
        if (_pixelCols.Count > 0) return;
        var trackW = OverlayProgress.ActualWidth;
        if (trackW <= 0) return;

        const double pitch = 4, cell = 3; // 4px 节距 = 3px 格 + 1px 缝
        var cols = (int)((trackW + 1) / pitch);
        var from = Color.FromRgb(0x0B, 0x12, 0x20);
        var to = Color.FromRgb(0x58, 0xA6, 0xFF);

        for (var c = 0; c < cols; c++)
        {
            var brush = new SolidColorBrush(LerpColor(from, to, cols > 1 ? (double)c / (cols - 1) : 0));
            var pair = new Rectangle[2];
            for (var row = 0; row < 2; row++)
            {
                var px = new Rectangle
                {
                    Width = cell,
                    Height = cell,
                    Opacity = PixelOffOpacity,
                    Fill = brush,
                };
                Canvas.SetLeft(px, c * pitch);
                Canvas.SetTop(px, row == 0 ? 0.5 : 4.5); // 两行 0.5~3.5 / 4.5~7.5，行缝 1px，居中
                pair[row] = px;
                PixelGrid.Children.Add(px);
            }
            _pixelCols.Add(pair);
            _pixelBrushes.Add(brush);
        }
        UpdatePixelScreen(_progressPct, instant: true); // 布局晚于 SetProgress 的兜底
    }

    /// <summary>按进度点亮/熄灭像素列。前进 = 波式级联（120ms 淡入 + 8ms/列）；
    /// 回退/复位 = 瞬时。前沿列亮白。</summary>
    private void UpdatePixelScreen(double pct, bool instant)
    {
        var cols = _pixelCols.Count;
        if (cols == 0) return; // 屏未构建：BuildPixelScreen 收尾时按 _progressPct 兜底
        var target = Math.Clamp((int)Math.Round(pct / 100 * cols), 0, cols);
        if (target == _litCols && !instant) return;

        if (instant || target < _litCols)
        {
            // 瞬时：清动画、按目标直接落定（重置/构建兜底/回退）
            for (var c = 0; c < cols; c++)
                foreach (var px in _pixelCols[c])
                {
                    px.BeginAnimation(UIElement.OpacityProperty, null);
                    px.Opacity = c < target ? 1.0 : PixelOffOpacity;
                }
            _litCols = target;
            ApplyPixelHead();
            return;
        }

        // 前进：新进列波式点亮（级联 8ms/列——步进跳变时呈"一波点亮"）
        for (var c = _litCols; c < target; c++)
        {
            var delay = TimeSpan.FromMilliseconds((c - _litCols) * 8);
            foreach (var px in _pixelCols[c])
            {
                px.BeginAnimation(UIElement.OpacityProperty, null);
                px.Opacity = PixelOffOpacity;
                px.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(120)))
                    { BeginTime = delay });
            }
        }
        _litCols = target;
        ApplyPixelHead(); // 只换填充色，透明度由本列的级联动画承担
    }

    /// <summary>前沿列亮白：旧前沿恢复渐变刷，新前沿（最后一列已点亮）换亮白刷。</summary>
    private void ApplyPixelHead()
    {
        if (_headCol >= 0 && _headCol < _pixelCols.Count)
            foreach (var px in _pixelCols[_headCol])
                px.Fill = _pixelBrushes[_headCol];
        _headCol = _litCols - 1;
        if (_headCol >= 0)
            foreach (var px in _pixelCols[_headCol])
                px.Fill = PixelHeadBrush;
    }

    /// <summary>渐变采样（黑 → 主题蓝，按列位置）。</summary>
    private static Color LerpColor(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    // ---- 彗尾光柱群 + 像素块 ----

    /// <summary>彗尾锚点重分布：按各自宽度比例换算 Left——填充宽动画（400ms）期间逐帧触发，
    /// 光柱随条增长平滑右移；26 次属性赋值，开销可忽略。</summary>
    private void RedistributeTail()
    {
        var w = ProgressFill.ActualWidth;
        if (w <= 0) return;
        foreach (var (el, frac, off) in _tailItems)
            Canvas.SetLeft(el, frac * w + off);
    }

    /// <summary>彗尾元素共用的段内漂移：锚点起 0 → -drift 匀速直线，循环往复；
    /// withFade 时另叠透明度关键帧：0 →(fadeInPct) peak →(fadeOutStartPct) 保持 →(fadeOutEndPct) 0
    /// （回卷在透明态完成，无跳变）。负 BeginTime 让首轮即呈现散落中途的自然态。</summary>
    private static void StartTailAnimations(FrameworkElement el, double drift, double durSec, double peak,
        Random rng, bool withFade, double fadeInPct = 0.15, double fadeOutStartPct = 0.7,
        double fadeOutEndPct = 1.0)
    {
        var dur = new Duration(TimeSpan.FromSeconds(durSec));
        var begin = TimeSpan.FromSeconds(-rng.NextDouble() * durSec);

        var move = new DoubleAnimation(0, -drift, dur)
        { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
        ((TranslateTransform)el.RenderTransform).BeginAnimation(TranslateTransform.XProperty, move);

        if (!withFade) return;
        var fade = new DoubleAnimationUsingKeyFrames { Duration = dur, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(fadeInPct)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(fadeOutStartPct)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(fadeOutEndPct)));
        if (fadeOutEndPct < 1.0) // 提前消散：尾段保持透明（回卷发生在透明态）
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        el.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>生成彗尾光柱群 + 像素块（构造时一次；固定种子保证每次启动布局一致）。</summary>
    private void BuildProgressTail()
    {
        var rng = new Random(42);

        // 光柱 ×10：满高 8px（= 进度条高度），宽度按像素格计：3 格（12px）或 5 格（20px）。
        // 主题蓝（#58A6FF，与点阵/品牌同色）中心单峰渐散：中线最亮，向左右连续消散至透明。
        // 锚点比例偏头部（0.45~0.98），段内左漂（行程钳制在锚点左侧可用空间内，避免高亮态被左端裁剪）
        for (var i = 0; i < 10; i++)
        {
            var w = (rng.Next(2) == 0 ? 3 : 5) * 4.0;          // 3 或 5 个像素格宽（节距 4px）
            var frac = 0.45 + rng.NextDouble() * 0.53;         // 锚点比例（偏头部）
            var dur = 1.2 + rng.NextDouble() * 0.8;            // 1.2~2.0s
            var speed = 50.0 + rng.NextDouble() * 35;          // 50~85 px/s 匀速
            var drift = Math.Min(dur * speed, frac * 360 * 0.95);
            var peak = 0.35 + rng.NextDouble() * 0.45;         // 峰值不透明度 0.35~0.8
            var pillar = new Rectangle
            {
                Width = w,
                Height = 8,                                    // 满高（= 条高）
                Opacity = 0,
                Fill = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(0, 0x58, 0xA6, 0xFF), 0),
                        new GradientStop(Color.FromArgb(235, 0x58, 0xA6, 0xFF), 0.5),
                        new GradientStop(Color.FromArgb(0, 0x58, 0xA6, 0xFF), 1),
                    },
                    new Point(0, 0.5), new Point(1, 0.5)),
                RenderTransform = new TranslateTransform(),
            };
            StartTailAnimations(pillar, drift, dur, peak, rng, withFade: true);
            _tailItems.Add((pillar, frac, 0));
            ProgressTail.Children.Add(pillar);
        }

        // 像素光点 ×10：1.5~2.5px 小亮点 + 向左渐隐拖尾（亮点在右端最亮，拖尾 6~12px
        // 向左消散至透明）。**双群混合**——全程漂移模型的时间平均密度必然左密右疏
        //（右侧点只在生成瞬间路过），故分两类互补：
        //   远行点 ×6：生成点 → 条左端全程，贴左边缘才淡出（88%→97%），途中永不消失；
        //   近程闪点 ×4：右半区局部短程（40~90px），两端各 20% 缓慢渐隐——补右半密度。
        // 结构：段漂移/淡入淡出在 host 容器上，Y 摆与呼吸在光点本体上（透明度叠乘）。
        for (var i = 0; i < 10; i++)
        {
            var size = 1.5 + rng.NextDouble();                 // 亮点高 1.5~2.5px
            var trail = 6.0 + rng.NextDouble() * 6.0;          // 拖尾长 6~12px
            var w = size + trail;                              // 总宽 = 拖尾 + 亮点（亮点在右端）

            double frac, drift, dur, fadeIn, fadeOutStart, fadeOutEnd;
            if (i < 6)
            {
                // 远行点：生成点（0.3~0.95）→ 条左端，60~100 px/s
                frac = 0.3 + rng.NextDouble() * 0.65;
                drift = frac * 360 * 0.95;
                dur = drift / (60.0 + rng.NextDouble() * 40);
                fadeIn = 0.08; fadeOutStart = 0.88; fadeOutEnd = 0.97;
            }
            else
            {
                // 近程闪点：右半区（≥0.5）局部 40~90px 段，40~70 px/s，两端渐隐
                drift = 40.0 + rng.NextDouble() * 50;
                dur = drift / (40.0 + rng.NextDouble() * 30);
                var minFrac = Math.Max(0.5, (drift + w + 4) / 360); // 段不越出条左端
                frac = minFrac + rng.NextDouble() * (0.98 - minFrac);
                fadeIn = 0.2; fadeOutStart = 0.8; fadeOutEnd = 1.0;
            }

            var host = new Grid
            {
                Width = w,
                Height = 8,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransform = new TranslateTransform(),
            };
            var px = new Rectangle
            {
                Width = w,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
                // 水平渐变：左端（拖尾末梢）透明 → 右端（亮点）最亮
                Fill = new LinearGradientBrush(
                    Color.FromArgb(0, 255, 255, 255),
                    Color.FromArgb((byte)(180 + rng.Next(76)), 255, 255, 255), 0.0),
                RenderTransform = new TranslateTransform(),
            };
            host.Children.Add(px);
            StartTailAnimations(host, drift, dur, 1.0, rng, withFade: true,
                fadeInPct: fadeIn, fadeOutStartPct: fadeOutStart, fadeOutEndPct: fadeOutEnd);
            _tailItems.Add((host, frac, -w)); // 亮头对齐锚点，拖尾伸在锚点左侧
            ProgressTail.Children.Add(host);

            // Y 轴轻摆：±1.5~3px 正弦往复（1.0~2.2s，负 BeginTime 错相）——左漂同时上下浮动
            var yAmp = 1.5 + rng.NextDouble() * 1.5;
            var yDur = 1.0 + rng.NextDouble() * 1.2;
            var sway = new DoubleAnimation(-yAmp, yAmp,
                new Duration(TimeSpan.FromSeconds(yDur)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(-rng.NextDouble() * yDur),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            ((TranslateTransform)px.RenderTransform).BeginAnimation(TranslateTransform.YProperty, sway);

            // 平滑呼吸：0.3~0.7s 往复，下限 0.35——途中任何时刻不会完全隐形（叠乘在段淡入淡出之上）
            var blinkDur = 0.3 + rng.NextDouble() * 0.4;
            var blink = new DoubleAnimation(0.35, 1.0, new Duration(TimeSpan.FromSeconds(blinkDur)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(-rng.NextDouble() * blinkDur),
            };
            px.BeginAnimation(UIElement.OpacityProperty, blink);
        }
    }
}
